using System.Text;
using System.Text.RegularExpressions;

namespace ChatWithCode.Core.Indexing.Chunking;

/// <summary>A class, function, method, … found in a file. Lines are 0-based and inclusive.</summary>
internal sealed class Declaration
{
    public required string Name { get; init; }

    /// <summary>Class-like (its members are named "Container.member"), as opposed to a function.</summary>
    public required bool IsContainer { get; init; }

    public int Start { get; set; }
    public int End { get; set; } = -1;

    /// <summary>The line with the opening brace (or the def/class line in Python); members can't start above it.</summary>
    public int BodyStart { get; init; }

    public List<Declaration> Children { get; } = [];
}

/// <summary>How to read a language family well enough to find its declarations.</summary>
internal sealed record LanguageProfile(
    bool IndentBased = false,
    bool SingleQuoteStrings = false,
    bool BacktickStrings = false,
    bool TripleQuoteStrings = false,
    bool HashComments = false,
    bool PreprocessorLines = false,
    bool RegexLiterals = false,
    bool MethodsWithoutKeyword = false,
    bool ArrowFunctions = false,
    bool GoDeclarations = false,
    bool SwiftInitializers = false);

/// <summary>
/// Finds declarations without a full parser: brace languages are scanned character by character
/// (skipping strings and comments) to match each declaration's braces; Python uses indentation.
/// Returns null when the file can't be read reliably (unbalanced braces, …), so the caller can
/// fall back to plain block chunking.
/// </summary>
internal static partial class CodeStructure
{
    public static readonly IReadOnlyDictionary<string, LanguageProfile> Profiles = new Dictionary<string, LanguageProfile>(StringComparer.Ordinal)
    {
        ["typescript"] = new(SingleQuoteStrings: true, BacktickStrings: true, RegexLiterals: true, MethodsWithoutKeyword: true, ArrowFunctions: true),
        ["javascript"] = new(SingleQuoteStrings: true, BacktickStrings: true, RegexLiterals: true, MethodsWithoutKeyword: true, ArrowFunctions: true),
        ["java"] = new(MethodsWithoutKeyword: true),
        ["groovy"] = new(SingleQuoteStrings: true, TripleQuoteStrings: true, MethodsWithoutKeyword: true),
        ["dart"] = new(SingleQuoteStrings: true, TripleQuoteStrings: true, MethodsWithoutKeyword: true),
        ["c"] = new(PreprocessorLines: true, MethodsWithoutKeyword: true),
        ["cpp"] = new(PreprocessorLines: true, MethodsWithoutKeyword: true),
        ["php"] = new(SingleQuoteStrings: true, HashComments: true),
        ["go"] = new(BacktickStrings: true, GoDeclarations: true),
        ["rust"] = new(),
        ["kotlin"] = new(TripleQuoteStrings: true),
        ["scala"] = new(TripleQuoteStrings: true),
        ["swift"] = new(TripleQuoteStrings: true, SwiftInitializers: true),
        ["python"] = new(IndentBased: true),
    };

    public static List<Declaration>? Find(string[] lines, LanguageProfile profile)
    {
        var declarations = profile.IndentBased ? FindByIndentation(lines) : FindByBraces(lines, profile);
        if (declarations is null)
        {
            return null;
        }

        AttachLeadingLines(lines, declarations, lowerBound: -1, profile);
        return declarations;
    }

    // ---------------------------------------------------------------- brace languages

    private enum State { Code, LineComment, BlockComment, String }

    private sealed class Frame
    {
        public Declaration? Declaration { get; init; }

        /// <summary>A brace inside parentheses (destructuring, an object argument, a callback): part of the statement.</summary>
        public bool InsideParens { get; init; }
    }

    private static List<Declaration>? FindByBraces(string[] lines, LanguageProfile profile)
    {
        var top = new List<Declaration>();
        var stack = new List<Frame>();
        var header = new List<(int Line, StringBuilder Text)>();
        var parenDepth = 0;
        var state = State.Code;
        var stringEnd = "";
        var lastSignificant = '\0';

        for (var lineNo = 0; lineNo < lines.Length; lineNo++)
        {
            var line = lines[lineNo];
            if (state == State.LineComment)
            {
                state = State.Code;
            }

            if (state == State.Code && profile.PreprocessorLines && line.TrimStart().StartsWith('#'))
            {
                continue; // #include, #define, #if …
            }

            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                switch (state)
                {
                    case State.LineComment:
                        i = line.Length;
                        continue;
                    case State.BlockComment:
                        if (c == '*' && i + 1 < line.Length && line[i + 1] == '/')
                        {
                            state = State.Code;
                            i++;
                        }

                        continue;
                    case State.String:
                        if (c == '\\' && !(stringEnd == "`" && profile.GoDeclarations)) // Go's backtick strings are raw
                        {
                            i++;
                            continue;
                        }

                        if (string.CompareOrdinal(line, i, stringEnd, 0, stringEnd.Length) == 0)
                        {
                            state = State.Code;
                            i += stringEnd.Length - 1;
                        }

                        continue;
                }

                // Code.
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/' || c == '#' && profile.HashComments && !(i + 1 < line.Length && line[i + 1] == '['))
                {
                    state = State.LineComment;
                    i = line.Length;
                    continue;
                }

                if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
                {
                    state = State.BlockComment;
                    i++;
                    continue;
                }

                if (profile.TripleQuoteStrings && string.CompareOrdinal(line, i, "\"\"\"", 0, 3) == 0)
                {
                    Append(header, lineNo, "\"\"");
                    state = State.String;
                    stringEnd = "\"\"\"";
                    i += 2;
                    continue;
                }

                // A quote right after a letter is an apostrophe in text (JSX: <p>Couldn't load</p>), not a string.
                var apostrophe = c == '\'' && i > 0 && char.IsLetterOrDigit(line[i - 1]);
                if (c == '"' || c == '\'' && profile.SingleQuoteStrings && !apostrophe || c == '`' && profile.BacktickStrings)
                {
                    Append(header, lineNo, "\"\"");
                    state = State.String;
                    stringEnd = c.ToString();
                    lastSignificant = '"';
                    continue;
                }

                if (c == '\'' && !apostrophe)
                {
                    // A character literal ('a', '\n', '{') in C-like languages; otherwise (a Rust lifetime) an ordinary character.
                    var literal = CharLiteral().Match(line, i);
                    if (literal.Success && literal.Index == i)
                    {
                        Append(header, lineNo, "' '");
                        i += literal.Length - 1;
                        lastSignificant = '\'';
                        continue;
                    }
                }

                if (c == '/' && profile.RegexLiterals && "(,=:[!&|?{};".Contains(lastSignificant, StringComparison.Ordinal) || c == '/' && profile.RegexLiterals && lastSignificant == '\0')
                {
                    var end = RegexLiteralEnd(line, i);
                    if (end > i)
                    {
                        Append(header, lineNo, "/ /");
                        i = end;
                        lastSignificant = '/';
                        continue;
                    }
                }

                switch (c)
                {
                    case '{' when parenDepth > 0:
                        stack.Add(new Frame { InsideParens = true });
                        Append(header, lineNo, "{");
                        break;
                    case '}' when stack.Count > 0 && stack[^1].InsideParens:
                        stack.RemoveAt(stack.Count - 1);
                        Append(header, lineNo, "}");
                        break;
                    case '{':
                    {
                        var declaration = Recognize(header, lineNo, profile, Container(stack));
                        if (declaration is not null)
                        {
                            (Parent(stack)?.Children ?? top).Add(declaration);
                        }

                        stack.Add(new Frame { Declaration = declaration });
                        header.Clear();
                        parenDepth = 0;
                        break;
                    }
                    case '}':
                        if (stack.Count == 0)
                        {
                            return null; // more closing than opening braces: don't trust this file's structure
                        }

                        if (stack[^1].Declaration is { } closed)
                        {
                            closed.End = lineNo;
                        }

                        stack.RemoveAt(stack.Count - 1);
                        header.Clear();
                        parenDepth = 0;
                        break;
                    case ';' when parenDepth == 0:
                        header.Clear();
                        break;
                    default:
                        if (c == '(')
                        {
                            parenDepth++;
                        }
                        else if (c == ')')
                        {
                            parenDepth = Math.Max(0, parenDepth - 1);
                        }

                        Append(header, lineNo, c.ToString());
                        break;
                }

                if (!char.IsWhiteSpace(c))
                {
                    lastSignificant = c;
                }
            }

            if (state == State.String && stringEnd.Length == 1 && stringEnd != "`")
            {
                state = State.Code; // an unterminated ordinary string ends with its line
            }

            Append(header, lineNo, " ");
        }

        return stack.Count == 0 && state != State.BlockComment && AllClosed(top) ? top : null;
    }

    private static bool AllClosed(List<Declaration> declarations) =>
        declarations.All(d => d.End >= d.Start && AllClosed(d.Children));

    private static Declaration? Parent(List<Frame> stack)
    {
        for (var i = stack.Count - 1; i >= 0; i--)
        {
            if (stack[i].Declaration is { } d)
            {
                return d;
            }
        }

        return null;
    }

    private static Declaration? Container(List<Frame> stack)
    {
        for (var i = stack.Count - 1; i >= 0; i--)
        {
            if (stack[i].Declaration is { IsContainer: true } d)
            {
                return d;
            }
        }

        return null;
    }

    private static void Append(List<(int Line, StringBuilder Text)> header, int line, string text)
    {
        if (header.Count == 0 || header[^1].Line != line)
        {
            if (header.Count == 0 && string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            header.Add((line, new StringBuilder()));
        }

        header[^1].Text.Append(text);
    }

    /// <summary>
    /// Is the code before this "{" a declaration? Tries the whole statement first, then the last
    /// few lines on their own (for languages without semicolons, where statements run together).
    /// </summary>
    private static Declaration? Recognize(List<(int Line, StringBuilder Text)> header, int braceLine, LanguageProfile profile, Declaration? container)
    {
        var lines = header.Where(h => h.Text.ToString().Trim().Length > 0).ToList();
        for (var from = 0; from < lines.Count; from++)
        {
            if (from > 0 && lines.Count - from > 6)
            {
                continue;
            }

            var text = string.Join(' ', lines.Skip(from).Select(h => h.Text.ToString().Trim()));
            text = Annotation().Replace(Whitespace().Replace(text, " "), " ").Trim();
            if (Match(text, profile) is { } found)
            {
                var name = found.Name.Replace("::", ".", StringComparison.Ordinal);
                if (container is not null)
                {
                    name = $"{container.Name}.{name}";
                }

                return new Declaration { Name = name, IsContainer = found.IsContainer, Start = lines[from].Line, BodyStart = braceLine };
            }
        }

        return null;
    }

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "if", "else", "for", "foreach", "while", "do", "switch", "case", "catch", "try", "finally", "return", "new",
        "throw", "await", "yield", "with", "using", "lock", "synchronized", "when", "match", "loop", "unsafe", "typeof",
        "sizeof", "elif", "except", "guard", "defer", "select", "go", "function", "delete", "void", "in", "of", "is",
        "as", "assert", "super", "this", "class", "interface", "enum", "struct", "static", "const", "let", "var",
    };

    private static (string Name, bool IsContainer)? Match(string text, LanguageProfile profile)
    {
        if (profile.GoDeclarations)
        {
            if (GoMethod().Match(text) is { Success: true } method)
            {
                return ($"{method.Groups["recv"].Value}.{method.Groups["name"].Value}", false);
            }

            if (GoType().Match(text) is { Success: true } type)
            {
                return (type.Groups["name"].Value, true);
            }
        }

        if (RustImpl().Match(text) is { Success: true } impl)
        {
            return (StripGenerics(impl.Groups["for"].Success ? impl.Groups["for"].Value : impl.Groups["name"].Value), true);
        }

        foreach (Match type in TypeKeyword().Matches(text))
        {
            var name = type.Groups["name"].Value;
            if (!Keywords.Contains(name) && !TypeKeywords.Contains(name))
            {
                return (StripGenerics(name), true);
            }
        }

        if (FunctionKeyword().Match(text) is { Success: true } function && !Keywords.Contains(function.Groups["name"].Value))
        {
            return (function.Groups["name"].Value, false);
        }

        if (profile.ArrowFunctions && (ArrowFunction().Match(text) is { Success: true } arrow))
        {
            return (arrow.Groups["name"].Value, false);
        }

        if (profile.SwiftInitializers && SwiftInit().Match(text) is { Success: true } init)
        {
            return (init.Groups["name"].Value, false);
        }

        return profile.MethodsWithoutKeyword ? MatchMethod(text) : null;
    }

    private static readonly HashSet<string> TypeKeywords = new(StringComparer.Ordinal)
    {
        "class", "interface", "enum", "record", "struct", "trait", "object", "protocol", "extension", "actor",
        "namespace", "module", "union", "mod", "data", "sealed", "abstract", "open", "final", "public", "private",
        "internal", "export", "default", "inline", "value", "annotation", "companion",
    };

    /// <summary>"name(...) trailer" at the end of the text, like a Java/C++/TypeScript method or a C function.</summary>
    private static (string Name, bool IsContainer)? MatchMethod(string text)
    {
        foreach (Match m in MethodName().Matches(text))
        {
            var name = m.Groups["name"].Value;
            var bare = name.Contains("::", StringComparison.Ordinal) ? name[(name.LastIndexOf("::", StringComparison.Ordinal) + 2)..] : name;
            if (Keywords.Contains(bare))
            {
                continue;
            }

            var prefix = text[..m.Index];
            if (prefix.Contains('=', StringComparison.Ordinal) || prefix.Contains('(', StringComparison.Ordinal) ||
                prefix.TrimEnd().EndsWith('.') || BadPrefix().IsMatch(prefix))
            {
                continue;
            }

            var close = MatchingParen(text, m.Index + m.Length - 1);
            if (close < 0 || !MethodTrailer().IsMatch(text[(close + 1)..]))
            {
                continue;
            }

            return (name, false);
        }

        return null;
    }

    private static int MatchingParen(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')' && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static string StripGenerics(string name)
    {
        var lt = name.IndexOf('<', StringComparison.Ordinal);
        return (lt > 0 ? name[..lt] : name).Trim();
    }

    private static int RegexLiteralEnd(string line, int start)
    {
        var inClass = false;
        for (var i = start + 1; i < line.Length; i++)
        {
            switch (line[i])
            {
                case '\\':
                    i++;
                    break;
                case '[':
                    inClass = true;
                    break;
                case ']':
                    inClass = false;
                    break;
                case '/' when !inClass:
                    return i == start + 1 ? -1 : i; // "//" is a comment, not an empty regex
            }
        }

        return -1;
    }

    // ---------------------------------------------------------------- Python

    private static List<Declaration>? FindByIndentation(string[] lines)
    {
        var code = PythonCodeLines(lines);
        var top = new List<Declaration>();
        var open = new List<(Declaration Declaration, int Indent)>();
        var lastCode = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            if (!code[i])
            {
                continue;
            }

            var indent = Indent(lines[i]);
            while (open.Count > 0 && indent <= open[^1].Indent)
            {
                open[^1].Declaration.End = lastCode;
                open.RemoveAt(open.Count - 1);
            }

            var m = PythonDeclaration().Match(lines[i]);
            if (m.Success)
            {
                var container = open.LastOrDefault(o => o.Declaration.IsContainer).Declaration;
                var name = m.Groups["name"].Value;
                var declaration = new Declaration
                {
                    Name = container is null ? name : $"{container.Name}.{name}",
                    IsContainer = m.Groups["kind"].Value == "class",
                    Start = i,
                    BodyStart = i,
                };
                (open.Count > 0 ? open[^1].Declaration.Children : top).Add(declaration);
                open.Add((declaration, indent));
            }

            lastCode = i;
        }

        foreach (var (declaration, _) in open)
        {
            declaration.End = lastCode;
        }

        return top;
    }

    /// <summary>Lines that start a Python statement: not blank, not only a comment, not inside a multi-line string or bracket.</summary>
    private static bool[] PythonCodeLines(string[] lines)
    {
        var result = new bool[lines.Length];
        string? inString = null;
        var depth = 0;
        for (var n = 0; n < lines.Length; n++)
        {
            var line = lines[n];
            var trimmed = line.TrimStart();
            result[n] = inString is null && depth == 0 && trimmed.Length > 0 && !trimmed.StartsWith('#');
            for (var i = 0; i < line.Length; i++)
            {
                if (inString is not null)
                {
                    if (line[i] == '\\')
                    {
                        i++;
                    }
                    else if (string.CompareOrdinal(line, i, inString, 0, inString.Length) == 0)
                    {
                        i += inString.Length - 1;
                        inString = null;
                    }

                    continue;
                }

                var c = line[i];
                if (c == '#')
                {
                    break;
                }

                if (c is '"' or '\'')
                {
                    inString = i + 2 < line.Length && line[i + 1] == c && line[i + 2] == c ? new string(c, 3) : c.ToString();
                    i += inString.Length - 1;
                }
                else if (c is '(' or '[' or '{')
                {
                    depth++;
                }
                else if (c is ')' or ']' or '}')
                {
                    depth = Math.Max(0, depth - 1);
                }
            }

            if (inString is { Length: 1 })
            {
                inString = null; // ordinary strings end with their line
            }
        }

        return result;
    }

    private static int Indent(string line)
    {
        var width = 0;
        foreach (var c in line)
        {
            if (c == ' ')
            {
                width++;
            }
            else if (c == '\t')
            {
                width += 4;
            }
            else
            {
                break;
            }
        }

        return width;
    }

    // ---------------------------------------------------------------- shared

    /// <summary>Doc comments, annotations, decorators and attributes right above a declaration belong to it.</summary>
    private static void AttachLeadingLines(string[] lines, List<Declaration> declarations, int lowerBound, LanguageProfile profile)
    {
        var bound = lowerBound;
        foreach (var d in declarations)
        {
            while (d.Start - 1 > bound && IsLeadingLine(lines[d.Start - 1].TrimStart(), profile))
            {
                d.Start--;
            }

            AttachLeadingLines(lines, d.Children, d.BodyStart, profile);
            bound = d.End;
        }
    }

    private static bool IsLeadingLine(string trimmed, LanguageProfile profile) =>
        trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("/*", StringComparison.Ordinal) ||
        trimmed.StartsWith('*') || trimmed.StartsWith('@') || trimmed.StartsWith("#[", StringComparison.Ordinal) ||
        (profile.IndentBased || profile.HashComments) && trimmed.StartsWith('#');

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Annotations and decorators such as @GetMapping("/x"), which can come before a declaration.</summary>
    [GeneratedRegex(@"@[\w.]+(?:\((?:[^()]|\([^()]*\))*\))?")]
    private static partial Regex Annotation();

    [GeneratedRegex(@"'(?:\\[^']{1,10}|[^'\\])'")]
    private static partial Regex CharLiteral();

    [GeneratedRegex(@"\b(?:class|interface|enum|record|struct|trait|object|protocol|extension|actor|namespace|module|union|mod)\s+(?<name>[A-Za-z_$][\w$.]*(?:<[^{]*?>)?)")]
    private static partial Regex TypeKeyword();

    [GeneratedRegex(@"\bimpl\b\s*(?:<[^{]*?>)?\s*(?<name>[\w:]+(?:<[^{]*?>)?)(?:\s+for\s+(?<for>[\w:]+(?:<[^{]*?>)?))?")]
    private static partial Regex RustImpl();

    [GeneratedRegex(@"\b(?:function\*?|func|fun|fn|def)\s+(?:<[^>]*>\s*)?(?<name>[A-Za-z_$][\w$.]*)\s*[(<\[]")]
    private static partial Regex FunctionKeyword();

    [GeneratedRegex(@"^(?:export\s+)?(?:default\s+)?(?:(?:const|let|var|public|private|protected|static|readonly|override)\s+)*(?<name>[A-Za-z_$#][\w$]*)\s*(?::[^=]+)?=\s*(?:async\s+)?(?:function\b[^(]*\(.*\)|(?:\(.*\)|[A-Za-z_$][\w$]*)\s*(?::\s*[^=]+)?=>)")]
    private static partial Regex ArrowFunction();

    [GeneratedRegex(@"^(?:[\w@]+\s+)*(?<name>init|deinit|subscript)\b")]
    private static partial Regex SwiftInit();

    [GeneratedRegex(@"\bfunc\s*\(\s*\w*\s*\*?\s*(?<recv>\w+)[^)]*\)\s*(?<name>\w+)")]
    private static partial Regex GoMethod();

    [GeneratedRegex(@"\btype\s+(?<name>\w+)(?:\[[^\]]*\])?\s+(?:struct|interface)\b")]
    private static partial Regex GoType();

    [GeneratedRegex(@"(?<name>[A-Za-z_$~][\w$~]*(?:::[A-Za-z_~][\w~]*)*)\s*(?:<[^<>()]*(?:<[^<>()]*>[^<>()]*)*>)?\s*\(")]
    private static partial Regex MethodName();

    [GeneratedRegex(@"\b(?:new|return|throw|await|else|case|yield|typeof)\b")]
    private static partial Regex BadPrefix();

    [GeneratedRegex(@"^\s*(?:(?:const|noexcept|override|final|async|mutating|throws?\s+[\w.,\s<>]+|:\s*[^{};]*|->\s*[^{};=]+|where\s+[^{};]+)\s*)*$")]
    private static partial Regex MethodTrailer();

    [GeneratedRegex(@"^\s*(?:async\s+)?(?<kind>def|class)\s+(?<name>\w+)")]
    private static partial Regex PythonDeclaration();
}
