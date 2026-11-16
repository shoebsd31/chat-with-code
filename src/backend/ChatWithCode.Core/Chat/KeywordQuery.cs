using System.Text.RegularExpressions;

namespace ChatWithCode.Core.Chat;

/// <summary>
/// Turns a question into a keyword query for full-text search. Natural-language questions
/// rarely contain every word of the code they're about, so terms are OR-ed together and
/// ranking does the rest. Identifiers like "OrderService" or "get_user" are kept whole.
/// </summary>
public static partial class KeywordQuery
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "are", "but", "not", "you", "all", "any", "can", "had", "her", "was", "one", "our", "out",
        "has", "his", "how", "its", "may", "new", "now", "see", "two", "who", "did", "get", "let", "say", "she", "too",
        "use", "uses", "used", "using", "does", "what", "when", "where", "which", "why", "with", "this", "that", "from",
        "they", "them", "then", "there", "their", "these", "those", "into", "about", "would", "could", "should", "will",
        "have", "been", "being", "were", "your", "some", "than", "also", "just", "only", "show", "tell", "explain",
        "code", "file", "files", "work", "works", "happen", "happens", "repo", "repository", "please", "find", "make",
    };

    /// <summary>A Postgres tsquery (simple config) such as "orderservice | refund:*", or null when nothing is searchable.</summary>
    public static string? ToTsQuery(string question, int maxTerms = 12)
    {
        ArgumentNullException.ThrowIfNull(question);
        var terms = Terms(question).Take(maxTerms).ToList();
        if (terms.Count == 0)
        {
            return null;
        }

        // Prefix matching (refund:* finds refunds, refunded) for longer plain words.
        return string.Join(" | ", terms.Select(t => t.Length >= 4 && !t.Contains('_', StringComparison.Ordinal) ? $"{t}:*" : t));
    }

    /// <summary>Distinct searchable terms, lower-cased, in the order they appear.</summary>
    public static IEnumerable<string> Terms(string question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in WordPattern().Matches(question))
        {
            var term = match.Value.ToLowerInvariant();
            if (term.Length >= 3 && !StopWords.Contains(term) && seen.Add(term))
            {
                yield return term;
            }
        }
    }

    // Letters, digits and underscores only, so the result is always a valid tsquery.
    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();
}
