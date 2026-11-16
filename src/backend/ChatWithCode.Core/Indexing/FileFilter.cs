using System.Text;

namespace ChatWithCode.Core.Indexing;

/// <summary>Why a file was left out of the index. Shown to admins on the index status page.</summary>
public enum SkipReason
{
    None,
    Excluded,
    TooLarge,
    Binary,
    NotUtf8,
}

/// <summary>
/// Decides which repository files are worth indexing. Built-in rules skip dependencies,
/// build output, lockfiles, minified and binary files. Admins add rules with
/// Indexing:ExcludeGlobs, and repositories can add or override rules (including "!pattern"
/// to re-include) in a <c>.chatwithcodeignore</c> file at the repository root.
/// </summary>
public sealed class FileFilter
{
    public const string RepoIgnoreFileName = ".chatwithcodeignore";

    /// <summary>Folders that hold dependencies, build output or tool state.</summary>
    public static readonly IReadOnlyList<string> BuiltInRules =
    [
        // Dependencies and build output
        "node_modules/", "bower_components/", "jspm_packages/", "vendor/", "packages/", "Pods/",
        "bin/", "obj/", "dist/", "build/", "out/", "target/", "coverage/", ".next/", ".nuxt/",
        "__pycache__/", ".venv/", "venv/", ".tox/", ".gradle/", ".terraform/",
        // Tool and editor folders
        ".git/", ".vs/", ".idea/", ".vscode/", ".nuget/",
        // Lockfiles
        "package-lock.json", "yarn.lock", "pnpm-lock.yaml", "npm-shrinkwrap.json", "packages.lock.json",
        "Cargo.lock", "poetry.lock", "Pipfile.lock", "composer.lock", "Gemfile.lock", "go.sum", "*.lock",
        // Minified, generated and source maps
        "*.min.js", "*.min.css", "*.map", "*.g.cs", "*.g.i.cs", "*.generated.*", "*.snap",
        // Binary formats (also caught by content sniffing, but skipping by name saves a download)
        "*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.ico", "*.webp", "*.tiff", "*.psd",
        "*.mp3", "*.mp4", "*.mov", "*.avi", "*.wav", "*.ogg", "*.webm",
        "*.zip", "*.tar", "*.gz", "*.tgz", "*.7z", "*.rar", "*.jar", "*.war", "*.nupkg",
        "*.dll", "*.exe", "*.pdb", "*.so", "*.dylib", "*.a", "*.lib", "*.o", "*.class", "*.pyc", "*.wasm",
        "*.pdf", "*.doc", "*.docx", "*.xls", "*.xlsx", "*.ppt", "*.pptx",
        "*.ttf", "*.otf", "*.woff", "*.woff2", "*.eot",
        "*.db", "*.sqlite", "*.mdb", "*.bin", "*.dat", "*.pfx", "*.p12", "*.snk", "*.keystore",
    ];

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly IgnoreRules _rules;
    private readonly long _maxBytes;

    public FileFilter(int maxFileSizeKb, IEnumerable<string>? extraRules = null, string? repoIgnoreFile = null)
    {
        _maxBytes = maxFileSizeKb * 1024L;
        _rules = IgnoreRules.Parse(BuiltInRules);
        _rules.AddRange(IgnoreRules.Parse(extraRules ?? []));
        if (!string.IsNullOrEmpty(repoIgnoreFile))
        {
            _rules.AddRange(IgnoreRules.Parse(repoIgnoreFile));
        }
    }

    /// <summary>Check a file by path and size, before downloading it.</summary>
    public SkipReason Check(string path, long sizeBytes)
    {
        if (_rules.IsIgnored(path))
        {
            return SkipReason.Excluded;
        }

        return sizeBytes > _maxBytes ? SkipReason.TooLarge : SkipReason.None;
    }

    /// <summary>
    /// Decode downloaded content. Files with NUL bytes are treated as binary, and files that
    /// aren't valid UTF-8 are skipped. Line endings are normalized to \n.
    /// </summary>
    public static SkipReason TryDecode(ReadOnlySpan<byte> content, out string text)
    {
        text = string.Empty;
        var sniff = content[..Math.Min(content.Length, 8000)];
        if (sniff.Contains((byte)0))
        {
            return SkipReason.Binary;
        }

        if (content.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            content = content[3..];
        }

        try
        {
            text = StrictUtf8.GetString(content).Replace("\r\n", "\n", StringComparison.Ordinal);
            return SkipReason.None;
        }
        catch (DecoderFallbackException)
        {
            return SkipReason.NotUtf8;
        }
    }
}
