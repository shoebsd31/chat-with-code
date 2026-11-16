namespace ChatWithCode.Core.Indexing;

/// <summary>Maps file names to a language id used for chunking and shown in citations.</summary>
public static class LanguageDetector
{
    public const string Markdown = "markdown";
    public const string CSharp = "csharp";
    public const string Text = "text";

    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".md"] = Markdown, [".markdown"] = Markdown, [".mdx"] = Markdown,
        [".cs"] = CSharp, [".csx"] = CSharp,
        [".vb"] = "vbnet", [".fs"] = "fsharp", [".fsx"] = "fsharp",
        [".ts"] = "typescript", [".tsx"] = "typescript", [".mts"] = "typescript", [".cts"] = "typescript",
        [".js"] = "javascript", [".jsx"] = "javascript", [".mjs"] = "javascript", [".cjs"] = "javascript",
        [".py"] = "python", [".java"] = "java", [".kt"] = "kotlin", [".kts"] = "kotlin", [".scala"] = "scala",
        [".go"] = "go", [".rs"] = "rust", [".rb"] = "ruby", [".php"] = "php", [".swift"] = "swift",
        [".c"] = "c", [".h"] = "c", [".cpp"] = "cpp", [".cc"] = "cpp", [".cxx"] = "cpp", [".hpp"] = "cpp",
        [".m"] = "objective-c", [".dart"] = "dart", [".lua"] = "lua", [".r"] = "r", [".pl"] = "perl",
        [".sql"] = "sql", [".sh"] = "shell", [".bash"] = "shell", [".zsh"] = "shell", [".ps1"] = "powershell",
        [".psm1"] = "powershell", [".bat"] = "batch", [".cmd"] = "batch",
        [".html"] = "html", [".htm"] = "html", [".cshtml"] = "razor", [".razor"] = "razor", [".vue"] = "vue",
        [".svelte"] = "svelte", [".css"] = "css", [".scss"] = "scss", [".less"] = "less",
        [".json"] = "json", [".jsonc"] = "json", [".yaml"] = "yaml", [".yml"] = "yaml", [".toml"] = "toml",
        [".xml"] = "xml", [".csproj"] = "xml", [".props"] = "xml", [".targets"] = "xml", [".config"] = "xml",
        [".bicep"] = "bicep", [".tf"] = "terraform", [".proto"] = "protobuf", [".graphql"] = "graphql",
        [".gradle"] = "groovy", [".groovy"] = "groovy", [".ini"] = "ini", [".txt"] = Text, [".rst"] = "rst",
    };

    private static readonly Dictionary<string, string> ByFileName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Dockerfile"] = "dockerfile", ["Makefile"] = "makefile", ["Jenkinsfile"] = "groovy",
        ["CMakeLists.txt"] = "cmake", ["Rakefile"] = "ruby", ["Gemfile"] = "ruby",
    };

    public static string Detect(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var name = path[(path.LastIndexOf('/') + 1)..];
        if (ByFileName.TryGetValue(name, out var language))
        {
            return language;
        }

        var dot = name.LastIndexOf('.');
        return dot >= 0 && ByExtension.TryGetValue(name[dot..], out language) ? language : Text;
    }
}
