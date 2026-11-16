using System.Text;
using System.Text.RegularExpressions;

namespace ChatWithCode.Core.Indexing;

/// <summary>
/// Gitignore-style path rules. Supports comments (#), negation (!), directory patterns (dir/),
/// root anchoring (/path), and the wildcards *, ** and ?. The last matching rule wins.
/// Paths are repository-relative with forward slashes and no leading slash, e.g. "src/app.cs".
/// </summary>
public sealed class IgnoreRules
{
    private readonly List<Rule> _rules = [];

    public int Count => _rules.Count;

    public static IgnoreRules Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var rules = new IgnoreRules();
        foreach (var line in lines)
        {
            rules.Add(line);
        }

        return rules;
    }

    public static IgnoreRules Parse(string text) =>
        Parse((text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));

    public void AddRange(IgnoreRules other)
    {
        ArgumentNullException.ThrowIfNull(other);
        _rules.AddRange(other._rules);
    }

    public void Add(string pattern)
    {
        var p = (pattern ?? string.Empty).Trim();
        if (p.Length == 0 || p.StartsWith('#'))
        {
            return;
        }

        var negate = p.StartsWith('!');
        if (negate)
        {
            p = p[1..];
        }

        var directoryOnly = p.EndsWith('/');
        var anchored = p.StartsWith('/');
        p = p.Trim('/');
        if (p.Length == 0)
        {
            return;
        }

        // A pattern without a slash (other than a trailing one) matches a name at any depth.
        anchored |= p.Contains('/', StringComparison.Ordinal);

        var body = GlobToRegex(p);
        var prefix = anchored ? "^" : "^(?:.*/)?";
        // A directory pattern matches everything inside the directory; a file pattern also
        // matches a directory of that name (and so everything inside it), like git.
        var suffix = directoryOnly ? "/.*$" : "(?:/.*)?$";
        _rules.Add(new Rule(new Regex(prefix + body + suffix, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase), negate));
    }

    /// <summary>True when the path is excluded by the rules.</summary>
    public bool IsIgnored(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var ignored = false;
        foreach (var rule in _rules)
        {
            if (rule.Pattern.IsMatch(path))
            {
                ignored = !rule.Negate;
            }
        }

        return ignored;
    }

    private static string GlobToRegex(string glob)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*')
            {
                var doubleStar = i + 1 < glob.Length && glob[i + 1] == '*';
                if (doubleStar)
                {
                    i++;
                    // "**/" matches zero or more directories.
                    if (i + 1 < glob.Length && glob[i + 1] == '/')
                    {
                        i++;
                        sb.Append("(?:.*/)?");
                    }
                    else
                    {
                        sb.Append(".*");
                    }
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }

        return sb.ToString();
    }

    private sealed record Rule(Regex Pattern, bool Negate);
}
