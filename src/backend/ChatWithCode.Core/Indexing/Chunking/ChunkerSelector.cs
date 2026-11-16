namespace ChatWithCode.Core.Indexing.Chunking;

/// <summary>Picks the chunker for a file's language. Unknown languages use the generic chunker.</summary>
public sealed class ChunkerSelector
{
    private readonly IChunker _markdown;
    private readonly IChunker _csharp;
    private readonly IChunker _generic;

    public ChunkerSelector(ITokenCounter tokens, ChunkingOptions? options = null)
    {
        options ??= new ChunkingOptions();
        _markdown = new MarkdownChunker(tokens, options);
        _csharp = new CSharpChunker(tokens, options);
        _generic = new GenericChunker(tokens, options);
    }

    public IChunker For(string language) => language switch
    {
        LanguageDetector.Markdown => _markdown,
        LanguageDetector.CSharp => _csharp,
        _ => _generic,
    };
}
