using System.Text.RegularExpressions;

namespace ChatWithCode.Core.Indexing.Chunking;

/// <summary>
/// Language-agnostic chunking: small files stay whole; larger files are cut at blank lines
/// into chunks under the token limit. The first declaration in a chunk names it.
/// </summary>
public sealed partial class GenericChunker(ITokenCounter tokens, ChunkingOptions options) : IChunker
{
    public IReadOnlyList<TextChunk> Chunk(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var source = new SourceText(text, tokens);
        var chunks = new List<TextChunk>();
        int start = 0, end = source.Lines.Length - 1;
        if (!source.Trim(ref start, ref end))
        {
            return chunks;
        }

        if (source.TokensIn(start, end) <= options.MaxTokens)
        {
            chunks.Add(source.ToChunk(start, end, FindSymbol(source.Lines, start, end)));
            return chunks;
        }

        source.ChunkByBlocks(start, end, symbol: null, options, chunks, (s, e) => FindSymbol(source.Lines, s, e));
        return chunks;
    }

    /// <summary>Name of the first class, function or similar declared in the range, if any.</summary>
    internal static string? FindSymbol(string[] lines, int start, int end)
    {
        for (var i = start; i <= end; i++)
        {
            var match = DeclarationPattern().Match(lines[i]);
            if (match.Success)
            {
                return match.Groups["name"].Value;
            }
        }

        return null;
    }

    [GeneratedRegex(@"\b(?:class|interface|struct|enum|record|trait|impl|object|def|func|function|fn|sub|module|namespace|type)\s+(?<name>[A-Za-z_$][\w$.]*)", RegexOptions.CultureInvariant)]
    private static partial Regex DeclarationPattern();
}
