using System.Text.RegularExpressions;

namespace ChatWithCode.Core.Indexing.Chunking;

/// <summary>
/// Markdown: small files stay whole; larger files are split into one chunk per heading
/// section. Each chunk is named by its heading path, e.g. "Setup &gt; Install".
/// Headings inside fenced code blocks are ignored.
/// </summary>
public sealed partial class MarkdownChunker(ITokenCounter tokens, ChunkingOptions options) : IChunker
{
    public IReadOnlyList<TextChunk> Chunk(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var source = new SourceText(text, tokens);
        var chunks = new List<TextChunk>();
        var lines = source.Lines;
        int first = 0, last = lines.Length - 1;
        if (!source.Trim(ref first, ref last))
        {
            return chunks;
        }

        var headings = FindHeadings(lines);
        if (source.TokensIn(first, last) <= options.MaxTokens)
        {
            chunks.Add(source.ToChunk(first, last, headings.FirstOrDefault(h => h.Level == 1)?.Title ?? headings.FirstOrDefault()?.Title));
            return chunks;
        }

        // Section boundaries: the start of the file, then every heading.
        var stack = new List<(int Level, string Title)>();
        var sectionStart = 0;
        string? sectionPath = null;
        foreach (var heading in headings)
        {
            AddSection(sectionStart, heading.Line - 1, sectionPath);

            while (stack.Count > 0 && stack[^1].Level >= heading.Level)
            {
                stack.RemoveAt(stack.Count - 1);
            }

            stack.Add((heading.Level, heading.Title));
            sectionPath = string.Join(" > ", stack.Select(s => s.Title));
            sectionStart = heading.Line;
        }

        AddSection(sectionStart, lines.Length - 1, sectionPath);
        return chunks;

        void AddSection(int start, int end, string? symbol)
        {
            if (!source.Trim(ref start, ref end))
            {
                return;
            }

            if (source.TokensIn(start, end) <= options.MaxTokens)
            {
                chunks.Add(source.ToChunk(start, end, symbol));
            }
            else
            {
                source.ChunkByBlocks(start, end, symbol, options, chunks);
            }
        }
    }

    private static List<Heading> FindHeadings(string[] lines)
    {
        var result = new List<Heading>();
        string? fence = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                var marker = trimmed[..3];
                fence = fence is null ? marker : (fence == marker ? null : fence);
                continue;
            }

            if (fence is not null)
            {
                continue;
            }

            var match = HeadingPattern().Match(lines[i]);
            if (match.Success)
            {
                result.Add(new Heading(i, match.Groups["hashes"].Length, match.Groups["title"].Value.Trim()));
            }
        }

        return result;
    }

    private sealed record Heading(int Line, int Level, string Title);

    [GeneratedRegex(@"^ {0,3}(?<hashes>#{1,6})\s+(?<title>.*?)\s*#*\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingPattern();
}
