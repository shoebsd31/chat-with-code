using System.Text;
using ChatWithCode.Core.Indexing;
using ChatWithCode.Core.Indexing.Chunking;

namespace ChatWithCode.Tests.Core;

public sealed class ChunkerTests
{
    private static readonly Cl100kTokenCounter Tokens = new();
    private static readonly ChunkingOptions Small = new() { MaxTokens = 120, OverlapLines = 2 };

    [Fact]
    public void Token_counter_matches_cl100k() =>
        Assert.Equal(2, Tokens.Count("hello world"));

    [Theory]
    [InlineData("src/A.cs", "csharp")]
    [InlineData("docs/Guide.MD", "markdown")]
    [InlineData("web/app.tsx", "typescript")]
    [InlineData("Dockerfile", "dockerfile")]
    [InlineData("LICENSE", "text")]
    public void Detects_language(string path, string expected) =>
        Assert.Equal(expected, LanguageDetector.Detect(path));

    [Fact]
    public void Small_file_stays_whole()
    {
        var chunks = new GenericChunker(Tokens, new ChunkingOptions()).Chunk("app.py", "\n\ndef main():\n    print('hi')\n\n");

        var chunk = Assert.Single(chunks);
        Assert.Equal((3, 4), (chunk.StartLine, chunk.EndLine));
        Assert.Equal("main", chunk.Symbol);
    }

    [Fact]
    public void Generic_chunks_respect_limit_and_cover_every_line()
    {
        var text = string.Join("\n\n", Enumerable.Range(1, 40).Select(i => $"function f{i}(a, b) {{\n  return a + b + {i};\n}}"));

        var chunks = new GenericChunker(Tokens, Small).Chunk("app.js", text);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(Tokens.Count(c.Content) <= Small.MaxTokens + 5, $"chunk at line {c.StartLine} too big"));
        AssertCoversNonBlankLines(text, chunks);
        Assert.Equal("f1", chunks[0].Symbol);
    }

    [Fact]
    public void Oversized_block_is_split_into_overlapping_windows()
    {
        var text = string.Join("\n", Enumerable.Range(1, 200).Select(i => $"x{i} = compute({i}) + offset"));

        var chunks = new GenericChunker(Tokens, Small).Chunk("data.py", text);

        Assert.True(chunks.Count > 2);
        for (var i = 1; i < chunks.Count; i++)
        {
            Assert.True(chunks[i].StartLine <= chunks[i - 1].EndLine, "windows should overlap");
            Assert.True(chunks[i].StartLine > chunks[i - 1].StartLine, "windows should move forward");
        }

        Assert.Equal(200, chunks[^1].EndLine);
    }

    [Fact]
    public void Very_long_single_line_is_cut_by_characters()
    {
        var text = string.Concat(Enumerable.Repeat("token ", 2_000));

        var chunks = new GenericChunker(Tokens, Small).Chunk("one-line.txt", text);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(Tokens.Count(c.Content) <= Small.MaxTokens));
        Assert.Equal(text.TrimEnd(), string.Concat(chunks.Select(c => c.Content)).TrimEnd());
    }

    [Fact]
    public void Markdown_splits_by_heading_with_heading_path()
    {
        var md = new StringBuilder("# Payments\n\nIntro text.\n\n");
        md.Append("## Setup\n\n").Append(Paragraphs("Setup", 6));
        md.Append("### Install\n\n").Append(Paragraphs("Install", 6));
        md.Append("```bash\n# not a heading\nnpm install\n```\n\n");
        md.Append("## Usage\n\n").Append(Paragraphs("Usage", 6));

        var chunks = new MarkdownChunker(Tokens, Small).Chunk("README.md", md.ToString());
        var symbols = chunks.Select(c => c.Symbol).Distinct().ToList();

        Assert.Contains("Payments", symbols);
        Assert.Contains("Payments > Setup", symbols);
        Assert.Contains("Payments > Setup > Install", symbols);
        Assert.Contains("Payments > Usage", symbols);
        Assert.DoesNotContain(symbols, s => s != null && s.Contains("not a heading", StringComparison.Ordinal));
        Assert.Contains(chunks, c => c.Content.Contains("npm install", StringComparison.Ordinal) && c.Symbol == "Payments > Setup > Install");
    }

    [Fact]
    public void Small_markdown_file_is_one_chunk_named_by_title()
    {
        var chunk = Assert.Single(new MarkdownChunker(Tokens, new ChunkingOptions()).Chunk("README.md", "# Orders API\n\nHandles orders.\n\n## Run\n\ndotnet run\n"));
        Assert.Equal("Orders API", chunk.Symbol);
    }

    [Fact]
    public void CSharp_large_type_is_split_by_member_with_qualified_names()
    {
        var code = new StringBuilder("""
            using System;
            using System.Linq;

            namespace Contoso.Orders;

            /// <summary>Handles orders.</summary>
            public sealed class OrderService
            {
            """);
        for (var i = 1; i <= 8; i++)
        {
            code.Append($$"""

                /// <summary>Step {{i}}.</summary>
                public int Step{{i}}(int value)
                {
                    var total = value;
                    for (var k = 0; k < {{i}}; k++) { total += k * {{i}}; }
                    Console.WriteLine($"Step {{i}} produced {total} for {value}");
                    return total;
                }

            """);
        }

        code.Append("}\n\npublic enum OrderState { New, Paid, Shipped }\n");

        var chunks = new CSharpChunker(Tokens, Small).Chunk("OrderService.cs", code.ToString());

        Assert.Contains(chunks, c => c.Symbol == "Contoso.Orders.OrderService.Step1" || c.Symbol == "Contoso.Orders.OrderService");
        Assert.Contains(chunks, c => c.Symbol == "Contoso.Orders.OrderState");
        Assert.All(chunks, c => Assert.True(Tokens.Count(c.Content) <= Small.MaxTokens + 5));
        Assert.DoesNotContain(chunks, c => c.Content.StartsWith("using ", StringComparison.Ordinal));
        // Doc comments travel with their member.
        Assert.Contains(chunks, c => c.Content.Contains("/// <summary>Step 3.</summary>", StringComparison.Ordinal) && c.Content.Contains("Step3(int value)", StringComparison.Ordinal));
        // Every method is in some chunk.
        for (var i = 1; i <= 8; i++)
        {
            Assert.Contains(chunks, c => c.Content.Contains($"public int Step{i}(", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void CSharp_top_level_statements_are_kept()
    {
        var code = "using ChatWithCode;\n\n" + string.Join("\n\n", Enumerable.Range(1, 30).Select(i => $"var service{i} = builder.Services.AddScoped<IService{i}, Service{i}>();\nlogger.LogInformation(\"registered {i}\");"))
            + "\n\npublic partial class Program;\n";

        var chunks = new CSharpChunker(Tokens, Small).Chunk("Program.cs", code);

        Assert.Contains(chunks, c => c.Content.Contains("service1 =", StringComparison.Ordinal));
        Assert.Contains(chunks, c => c.Content.Contains("service30 =", StringComparison.Ordinal));
        Assert.Contains(chunks, c => c.Symbol == "Program");
    }

    [Fact]
    public void Selector_routes_by_language()
    {
        var selector = new ChunkerSelector(Tokens);
        Assert.IsType<CSharpChunker>(selector.For("csharp"));
        Assert.IsType<MarkdownChunker>(selector.For("markdown"));
        Assert.IsType<GenericChunker>(selector.For("python"));
    }

    private static string Paragraphs(string topic, int count) =>
        string.Concat(Enumerable.Range(1, count).Select(i => $"{topic} paragraph {i} explains one more detail about {topic.ToUpperInvariant()} in a full sentence.\n\n"));

    private static void AssertCoversNonBlankLines(string text, IReadOnlyList<TextChunk> chunks)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]))
            {
                var line = i + 1;
                Assert.Contains(chunks, c => c.StartLine <= line && line <= c.EndLine);
            }
        }
    }
}
