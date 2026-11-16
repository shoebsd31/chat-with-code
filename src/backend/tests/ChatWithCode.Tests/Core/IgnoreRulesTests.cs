using ChatWithCode.Core.Indexing;

namespace ChatWithCode.Tests.Core;

public sealed class IgnoreRulesTests
{
    [Theory]
    [InlineData("node_modules/", "node_modules/react/index.js", true)]
    [InlineData("node_modules/", "web/node_modules/react/index.js", true)]
    [InlineData("node_modules/", "src/node_modules.ts", false)]
    [InlineData("*.min.js", "public/app.min.js", true)]
    [InlineData("*.min.js", "public/app.js", false)]
    [InlineData("/docs", "docs/guide.md", true)]
    [InlineData("/docs", "src/docs/guide.md", false)]
    [InlineData("docs", "src/docs/guide.md", true)]
    [InlineData("src/**/generated/", "src/a/b/generated/x.cs", true)]
    [InlineData("src/**/generated/", "src/generated/x.cs", true)]
    [InlineData("src/*.cs", "src/a/x.cs", false)]
    [InlineData("data?.csv", "data1.csv", true)]
    [InlineData("data?.csv", "data10.csv", false)]
    public void Matches_like_gitignore(string pattern, string path, bool ignored) =>
        Assert.Equal(ignored, IgnoreRules.Parse([pattern]).IsIgnored(path));

    [Fact]
    public void Later_negation_re_includes_a_path()
    {
        var rules = IgnoreRules.Parse("""
            # build output
            build/
            !build/scripts/
            """);

        Assert.True(rules.IsIgnored("build/out/app.dll"));
        Assert.False(rules.IsIgnored("build/scripts/deploy.sh"));
    }

    [Fact]
    public void Comments_and_blank_lines_are_ignored() =>
        Assert.Equal(1, IgnoreRules.Parse("\n# comment\n\n*.log\n").Count);
}
