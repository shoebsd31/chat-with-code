using System.Text.RegularExpressions;

namespace ChatWithCode.Core.Indexing.Chunking;

/// <summary>
/// Chunks TypeScript/JavaScript, Python, Java, Go, Kotlin, Rust, C/C++, PHP, Swift, Scala, Dart and
/// Groovy along their structure, like the C# chunker: small files stay whole; otherwise each class
/// or function that fits becomes one chunk, bigger classes are split into groups of members, and
/// members too big on their own are split further. Chunks are named like "OrderService.submit".
/// Code outside declarations is chunked by blocks, and import lines are dropped.
/// If the file's structure can't be read reliably, it's chunked like any other text.
/// </summary>
public sealed partial class StructuredChunker(ITokenCounter tokens, ChunkingOptions options, string language) : IChunker
{
    private readonly LanguageProfile _profile = CodeStructure.Profiles[language];
    private readonly GenericChunker _fallback = new(tokens, options);

    public static bool Supports(string language) => CodeStructure.Profiles.ContainsKey(language);

    public IReadOnlyList<TextChunk> Chunk(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var source = new SourceText(text, tokens);
        int first = 0, last = source.Lines.Length - 1;
        if (!source.Trim(ref first, ref last))
        {
            return [];
        }

        List<Declaration>? declarations;
        try
        {
            declarations = CodeStructure.Find(source.Lines, _profile);
        }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or RegexMatchTimeoutException)
        {
            declarations = null;
        }

        if (declarations is null || declarations.Count == 0)
        {
            return _fallback.Chunk(path, text);
        }

        var chunks = new List<TextChunk>();
        if (source.TokensIn(first, last) <= options.MaxTokens)
        {
            chunks.Add(source.ToChunk(first, last, declarations[0].Name));
            return chunks;
        }

        var covered = new bool[source.Lines.Length];
        foreach (var declaration in declarations)
        {
            Emit(declaration, source, chunks, covered);
        }

        ChunkUncovered(source, covered, chunks);
        chunks.Sort((a, b) => a.StartLine.CompareTo(b.StartLine));
        return chunks;
    }

    private void Emit(Declaration declaration, SourceText source, List<TextChunk> chunks, bool[] covered)
    {
        int start = declaration.Start, end = declaration.End;
        Array.Fill(covered, true, start, end - start + 1);

        if (source.TokensIn(start, end) <= options.MaxTokens)
        {
            chunks.Add(source.ToChunk(start, end, declaration.Name));
            return;
        }

        if (declaration.Children.Count == 0)
        {
            source.SplitWindows(start, end, declaration.Name, options, chunks);
            return;
        }

        // The declaration as a sequence of member declarations and the code between them (fields, the
        // header, the closing brace), grouped up to the limit. Members too big on their own are split further.
        int? groupStart = null;
        var groupEnd = -1;
        var groupMembers = new List<string>();
        var position = start;
        foreach (var child in declaration.Children)
        {
            AddGap(position, child.Start - 1);
            if (source.TokensIn(child.Start, child.End) > options.MaxTokens)
            {
                Flush();
                Emit(child, source, chunks, covered);
            }
            else
            {
                Add(child.Start, child.End, child.Name);
            }

            position = child.End + 1;
        }

        AddGap(position, end);
        Flush();

        void AddGap(int gapStart, int gapEnd)
        {
            if (!source.Trim(ref gapStart, ref gapEnd) || OnlyPunctuation(source, gapStart, gapEnd))
            {
                return;
            }

            if (source.TokensIn(gapStart, gapEnd) > options.MaxTokens)
            {
                Flush();
                source.ChunkByBlocks(gapStart, gapEnd, declaration.Name, options, chunks);
                return;
            }

            Add(gapStart, gapEnd, member: null);
        }

        void Add(int s, int e, string? member)
        {
            if (groupStart is { } gs && source.TokensIn(gs, e) > options.MaxTokens)
            {
                Flush();
            }

            groupStart ??= s;
            groupEnd = e;
            if (member is not null)
            {
                groupMembers.Add(member);
            }
        }

        void Flush()
        {
            if (groupStart is { } gs)
            {
                chunks.Add(source.ToChunk(gs, groupEnd, groupMembers.Count == 1 ? groupMembers[0] : declaration.Name));
                groupStart = null;
                groupMembers.Clear();
            }
        }
    }

    /// <summary>Code outside any declaration (top-level statements, constants, exports), without import lines.</summary>
    private void ChunkUncovered(SourceText source, bool[] covered, List<TextChunk> chunks)
    {
        for (var i = 0; i < covered.Length; i++)
        {
            if (!covered[i] && ImportLine().IsMatch(source.Lines[i]))
            {
                covered[i] = true;
            }
        }

        var start = -1;
        for (var i = 0; i <= covered.Length; i++)
        {
            var isFree = i < covered.Length && !covered[i];
            if (isFree && start < 0)
            {
                start = i;
            }
            else if (!isFree && start >= 0)
            {
                source.ChunkByBlocks(start, i - 1, symbol: null, options, chunks, (s, e) => GenericChunker.FindSymbol(source.Lines, s, e));
                start = -1;
            }
        }
    }

    /// <summary>Lines such as "}" or "});" carry nothing worth a chunk of their own.</summary>
    private static bool OnlyPunctuation(SourceText source, int start, int end)
    {
        for (var i = start; i <= end; i++)
        {
            if (source.Lines[i].Any(c => char.IsLetterOrDigit(c)))
            {
                return false;
            }
        }

        return true;
    }

    [GeneratedRegex(@"^\s*(?:import\b|from\s+\S+\s+import\b|export\s+\*\s+from\b|package\s+[\w.]+\s*;?\s*$|use\s+[\w:\\{}, *]+;\s*$|using\s+[\w.]+;\s*$|(?:const|let|var)\s+[\w{}, ]+\s*=\s*require\()")]
    private static partial Regex ImportLine();
}
