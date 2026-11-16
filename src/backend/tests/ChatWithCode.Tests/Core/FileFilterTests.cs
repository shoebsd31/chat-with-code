using System.Text;
using ChatWithCode.Core.Indexing;

namespace ChatWithCode.Tests.Core;

public sealed class FileFilterTests
{
    private readonly FileFilter _filter = new(maxFileSizeKb: 500);

    [Theory]
    [InlineData("src/Orders/OrderService.cs")]
    [InlineData("README.md")]
    [InlineData("web/src/app.tsx")]
    [InlineData("infra/main.bicep")]
    [InlineData("Dockerfile")]
    public void Source_and_docs_are_kept(string path) =>
        Assert.Equal(SkipReason.None, _filter.Check(path, 1_000));

    [Theory]
    [InlineData("web/node_modules/react/index.js")]
    [InlineData("src/Api/bin/Debug/net10.0/Api.dll")]
    [InlineData("src/Api/obj/project.assets.json")]
    [InlineData("package-lock.json")]
    [InlineData("web/yarn.lock")]
    [InlineData("public/vendor.min.js")]
    [InlineData("docs/logo.png")]
    [InlineData("tools/setup.exe")]
    [InlineData(".git/config")]
    [InlineData("go.sum")]
    public void Dependencies_build_output_and_binaries_are_excluded(string path) =>
        Assert.Equal(SkipReason.Excluded, _filter.Check(path, 1_000));

    [Fact]
    public void Large_files_are_skipped() =>
        Assert.Equal(SkipReason.TooLarge, new FileFilter(maxFileSizeKb: 1).Check("data/seed.sql", 2_000));

    [Fact]
    public void Admin_rules_and_repo_ignore_file_apply_in_order()
    {
        var filter = new FileFilter(500, extraRules: ["legacy/"], repoIgnoreFile: "!legacy/keep/\nfixtures/");

        Assert.Equal(SkipReason.Excluded, filter.Check("legacy/old.cs", 10));
        Assert.Equal(SkipReason.None, filter.Check("legacy/keep/new.cs", 10));
        Assert.Equal(SkipReason.Excluded, filter.Check("tests/fixtures/big.json", 10));
    }

    [Fact]
    public void Repo_ignore_file_can_re_include_a_built_in_exclusion()
    {
        var filter = new FileFilter(500, repoIgnoreFile: "!build/");
        Assert.Equal(SkipReason.None, filter.Check("build/pipeline.yml", 10));
    }

    [Fact]
    public void Decode_strips_bom_and_normalizes_line_endings()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. "line1\r\nline2"u8];

        Assert.Equal(SkipReason.None, FileFilter.TryDecode(bytes, out var text));
        Assert.Equal("line1\nline2", text);
    }

    [Fact]
    public void Decode_rejects_binary_content() =>
        Assert.Equal(SkipReason.Binary, FileFilter.TryDecode([0x50, 0x4B, 0x00, 0x01], out _));

    [Fact]
    public void Decode_rejects_invalid_utf8() =>
        Assert.Equal(SkipReason.NotUtf8, FileFilter.TryDecode(Encoding.Latin1.GetBytes("Café ünïcode"), out _));
}
