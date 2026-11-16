using Microsoft.ML.Tokenizers;

namespace ChatWithCode.Core.Indexing;

/// <summary>Counts tokens the way the embedding models do, so chunk limits are exact.</summary>
public interface ITokenCounter
{
    int Count(string text);
}

/// <summary>
/// cl100k_base, the encoding used by text-embedding-3-small/large.
/// Thread-safe; register as a singleton.
/// </summary>
public sealed class Cl100kTokenCounter : ITokenCounter
{
    private readonly TiktokenTokenizer _tokenizer = TiktokenTokenizer.CreateForEncoding("cl100k_base");

    public int Count(string text) => string.IsNullOrEmpty(text) ? 0 : _tokenizer.CountTokens(text);
}
