using ChatWithCode.Core.Abstractions;
using ChatWithCode.Core.Indexing;

namespace ChatWithCode.Core.Chat;

/// <summary>A source shown to the model as [Number] and to the user as a citation.</summary>
public sealed record SourceChunk(
    int Number,
    Guid RepositoryId,
    string Branch,
    string Path,
    string Language,
    int StartLine,
    int EndLine,
    string? Symbol,
    string Content,
    double Score);

/// <summary>
/// Picks what goes into the prompt: the best-scoring chunks within a token budget.
/// Chunks of the same file that touch or overlap are merged into one source, so the model
/// sees continuous code and citations don't repeat the same file twice in a row.
/// </summary>
public static class ContextBuilder
{
    public static IReadOnlyList<SourceChunk> Build(IReadOnlyList<SearchHit> hits, ITokenCounter tokens, int maxSources, int tokenBudget)
    {
        ArgumentNullException.ThrowIfNull(hits);
        ArgumentNullException.ThrowIfNull(tokens);

        // 1. Best first, within budget.
        var picked = new List<SearchHit>();
        var used = 0;
        foreach (var hit in hits.OrderByDescending(h => h.Score))
        {
            if (picked.Count >= maxSources)
            {
                break;
            }

            var cost = tokens.Count(hit.Chunk.Content);
            if (used + cost > tokenBudget)
            {
                continue; // a smaller chunk further down may still fit
            }

            picked.Add(hit);
            used += cost;
        }

        // 2. Merge neighbours from the same file.
        var merged = new List<(SearchHit First, int Start, int End, string? Symbol, List<string> Parts, double Score)>();
        foreach (var group in picked.GroupBy(h => (h.Chunk.Target, h.Chunk.Path)))
        {
            foreach (var hit in group.OrderBy(h => h.Chunk.StartLine))
            {
                var last = merged.Count > 0 ? merged[^1] : default;
                if (merged.Count > 0 && last.First.Chunk.Target == hit.Chunk.Target && last.First.Chunk.Path == hit.Chunk.Path &&
                    hit.Chunk.StartLine <= last.End + 1)
                {
                    // Overlapping windows repeat a few lines; only append the new ones.
                    var newLines = hit.Chunk.Content.Split('\n').Skip(Math.Max(0, last.End - hit.Chunk.StartLine + 1));
                    last.Parts.Add(string.Join('\n', newLines));
                    merged[^1] = (last.First, last.Start, Math.Max(last.End, hit.Chunk.EndLine), last.Symbol ?? hit.Chunk.Symbol, last.Parts, Math.Max(last.Score, hit.Score));
                }
                else
                {
                    merged.Add((hit, hit.Chunk.StartLine, hit.Chunk.EndLine, hit.Chunk.Symbol, [hit.Chunk.Content], hit.Score));
                }
            }
        }

        // 3. Number by relevance.
        return merged
            .OrderByDescending(m => m.Score)
            .Select((m, i) => new SourceChunk(
                i + 1,
                m.First.Chunk.Target.RepositoryId,
                m.First.Chunk.Target.Branch,
                m.First.Chunk.Path,
                m.First.Chunk.Language,
                m.Start,
                m.End,
                m.Symbol,
                string.Join('\n', m.Parts.Where(p => p.Length > 0)),
                m.Score))
            .ToList();
    }
}
