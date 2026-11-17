namespace ChatWithCode.Core.Indexing.Chunking;

/// <summary>Picks the chunker for a file's language. Unknown languages use the generic chunker.</summary>
public sealed class ChunkerSelector
{
    /// <summary>
    /// The newest splitter version. Raise it, and <see cref="VersionFor"/> for the languages concerned,
    /// whenever chunking changes in a way that is worth re-splitting already indexed files for.
    /// Version 2: structure-aware chunking for TypeScript, Python, Java, Go and the other
    /// <see cref="StructuredChunker"/> languages (before: blank-line blocks).
    /// </summary>
    public const int LatestVersion = 2;

    private readonly ITokenCounter _tokens;
    private readonly ChunkingOptions _options;
    private readonly IChunker _markdown;
    private readonly IChunker _csharp;
    private readonly IChunker _generic;
    private readonly Dictionary<string, IChunker> _structured = new(StringComparer.Ordinal);

    public ChunkerSelector(ITokenCounter tokens, ChunkingOptions? options = null)
    {
        _tokens = tokens;
        _options = options ?? new ChunkingOptions();
        _markdown = new MarkdownChunker(tokens, _options);
        _csharp = new CSharpChunker(tokens, _options);
        _generic = new GenericChunker(tokens, _options);
    }

    public IChunker For(string language) => language switch
    {
        LanguageDetector.Markdown => _markdown,
        LanguageDetector.CSharp => _csharp,
        _ when StructuredChunker.Supports(language) => Structured(language),
        _ => _generic,
    };

    /// <summary>
    /// The splitter version that files of this language need. A file indexed with an older version is
    /// re-split once (on a run started by a push or an admin); other files are left alone.
    /// </summary>
    public static int VersionFor(string language) => StructuredChunker.Supports(language) ? 2 : 1;

    private IChunker Structured(string language)
    {
        lock (_structured)
        {
            if (!_structured.TryGetValue(language, out var chunker))
            {
                chunker = new StructuredChunker(_tokens, _options, language);
                _structured[language] = chunker;
            }

            return chunker;
        }
    }
}
