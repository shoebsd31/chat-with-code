namespace ChatWithCode.Core.Indexing.Chunking;

/// <summary>
/// A file split into lines with per-line token counts, plus helpers to cut line ranges into
/// chunks under the token limit. Line indexes here are 0-based; chunks report 1-based lines.
/// </summary>
internal sealed class SourceText
{
    private readonly ITokenCounter _tokens;
    private readonly int[] _lineTokens;

    public SourceText(string text, ITokenCounter tokens)
    {
        _tokens = tokens;
        Lines = text.Split('\n');
        _lineTokens = new int[Lines.Length];
        for (var i = 0; i < Lines.Length; i++)
        {
            // +1 for the newline that joins lines back together.
            _lineTokens[i] = tokens.Count(Lines[i]) + 1;
        }
    }

    public string[] Lines { get; }

    public int TokensIn(int start, int end)
    {
        var sum = 0;
        for (var i = start; i <= end; i++)
        {
            sum += _lineTokens[i];
        }

        return sum;
    }

    public bool IsBlank(int line) => string.IsNullOrWhiteSpace(Lines[line]);

    public TextChunk ToChunk(int start, int end, string? symbol) =>
        new(start + 1, end + 1, symbol, string.Join('\n', Lines, start, end - start + 1));

    /// <summary>Trim blank lines from both ends of a range. Returns false when nothing is left.</summary>
    public bool Trim(ref int start, ref int end)
    {
        while (start <= end && IsBlank(start))
        {
            start++;
        }

        while (end >= start && IsBlank(end))
        {
            end--;
        }

        return start <= end;
    }

    /// <summary>
    /// Chunk a line range by merging blank-line-separated blocks up to the limit.
    /// Blocks that are too big on their own are cut into windows with a small overlap.
    /// </summary>
    public void ChunkByBlocks(int start, int end, string? symbol, ChunkingOptions options, List<TextChunk> output, Func<int, int, string?>? symbolFor = null)
    {
        if (!Trim(ref start, ref end))
        {
            return;
        }

        var blocks = new List<(int Start, int End)>();
        var blockStart = start;
        for (var i = start; i <= end; i++)
        {
            if (IsBlank(i))
            {
                if (i > blockStart)
                {
                    blocks.Add((blockStart, i - 1));
                }

                blockStart = i + 1;
            }
        }

        if (blockStart <= end)
        {
            blocks.Add((blockStart, end));
        }

        int? groupStart = null;
        var groupEnd = -1;
        foreach (var (s, e) in blocks)
        {
            if (TokensIn(s, e) > options.MaxTokens)
            {
                Flush();
                SplitWindows(s, e, symbol ?? symbolFor?.Invoke(s, e), options, output);
                continue;
            }

            if (groupStart is { } gs && TokensIn(gs, e) > options.MaxTokens)
            {
                Flush();
            }

            groupStart ??= s;
            groupEnd = e;
        }

        Flush();

        void Flush()
        {
            if (groupStart is { } gs)
            {
                output.Add(ToChunk(gs, groupEnd, symbol ?? symbolFor?.Invoke(gs, groupEnd)));
                groupStart = null;
            }
        }
    }

    /// <summary>Cut a range into consecutive windows under the limit, repeating a few lines between windows.</summary>
    public void SplitWindows(int start, int end, string? symbol, ChunkingOptions options, List<TextChunk> output)
    {
        var s = start;
        while (s <= end)
        {
            if (_lineTokens[s] > options.MaxTokens)
            {
                // One enormous line (generated data, long strings): cut it by characters.
                SplitLongLine(s, symbol, options, output);
                s++;
                continue;
            }

            var e = s;
            var total = _lineTokens[s];
            while (e + 1 <= end && total + _lineTokens[e + 1] <= options.MaxTokens)
            {
                e++;
                total += _lineTokens[e];
            }

            output.Add(ToChunk(s, e, symbol));
            if (e >= end)
            {
                break;
            }

            // Next window starts a little before this one ended, but always moves forward.
            s = Math.Max(s + 1, e + 1 - options.OverlapLines);
        }
    }

    private void SplitLongLine(int line, string? symbol, ChunkingOptions options, List<TextChunk> output)
    {
        var text = Lines[line];
        // Start from roughly 3 characters per token, then shrink until the real count fits.
        var size = Math.Max(1, options.MaxTokens * 3);
        var position = 0;
        while (position < text.Length)
        {
            var piece = text.Substring(position, Math.Min(size, text.Length - position));
            while (piece.Length > 1 && _tokens.Count(piece) > options.MaxTokens)
            {
                piece = piece[..(piece.Length * 3 / 4)];
            }

            output.Add(new TextChunk(line + 1, line + 1, symbol, piece));
            position += piece.Length;
        }
    }
}
