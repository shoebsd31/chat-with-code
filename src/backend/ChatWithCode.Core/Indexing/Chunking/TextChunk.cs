namespace ChatWithCode.Core.Indexing.Chunking;

/// <summary>A piece of a file. Line numbers are 1-based and inclusive.</summary>
public sealed record TextChunk(int StartLine, int EndLine, string? Symbol, string Content);

public interface IChunker
{
    IReadOnlyList<TextChunk> Chunk(string path, string text);
}

public sealed class ChunkingOptions
{
    /// <summary>Target maximum size of one chunk. Files at or below this size stay whole.</summary>
    public int MaxTokens { get; init; } = 1500;

    /// <summary>Lines repeated between consecutive pieces when a long block has to be cut.</summary>
    public int OverlapLines { get; init; } = 3;
}
