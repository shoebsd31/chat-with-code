using ChatWithCode.Core.Domain;
using ChatWithCode.Core.Options;
using ChatWithCode.Data;
using ChatWithCode.Data.Queue;
using ChatWithCode.Tests.Infrastructure;
using ChatWithCode.Worker.Cli;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ChatWithCode.Tests.Worker;

[Collection(PostgresCollection.Name)]
public sealed class AdminCommandsTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Index_registers_repository_and_queues_default_branch()
    {
        await using var t = await InitializedDbAsync();
        var ado = new FakeAdoGitClient();
        ado.Commit(new() { ["README.md"] = "# hi" });
        var output = new StringWriter();

        var exit = await Commands(t, ado, output).RunAsync(["index", "--project", "Fabrikam", "--repo", "web-app"], CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("Queued an index of Fabrikam/web-app (main)", output.ToString(), StringComparison.Ordinal);
        await using var check = t.NewContext();
        var repo = await check.Repositories.Include(r => r.Branches).SingleAsync();
        Assert.Equal(ado.Repository.Id, repo.AdoRepositoryId);
        var branch = Assert.Single(repo.Branches);
        Assert.Equal(("refs/heads/main", true), (branch.BranchName, branch.IsDefault));
        var job = await check.IndexJobs.SingleAsync();
        Assert.Equal(IndexJobType.Incremental, job.Type);
    }

    [Fact]
    public async Task Index_with_branch_and_full_flag()
    {
        await using var t = await InitializedDbAsync();
        var ado = new FakeAdoGitClient();
        ado.Commit(new() { ["a.txt"] = "a" });
        ado.Commit(new() { ["a.txt"] = "b" }, branch: "refs/heads/release");

        var exit = await Commands(t, ado, new StringWriter()).RunAsync(["index", "--project", "Fabrikam", "--repo", "web-app", "--branch", "release", "--full"], CancellationToken.None);

        Assert.Equal(0, exit);
        await using var check = t.NewContext();
        var branch = await check.IndexedBranches.SingleAsync();
        Assert.Equal(("refs/heads/release", false), (branch.BranchName, branch.IsDefault));
        Assert.Equal(IndexJobType.Full, (await check.IndexJobs.SingleAsync()).Type);
    }

    [Theory]
    [InlineData(new[] { "index", "--project", "Fabrikam", "--repo", "missing" }, "wasn't found")]
    [InlineData(new[] { "index", "--project", "Fabrikam", "--repo", "web-app", "--branch", "nope" }, "doesn't exist")]
    [InlineData(new[] { "index", "--repo", "web-app" }, "--project is required")]
    public async Task Index_reports_problems_plainly(string[] args, string expected)
    {
        await using var t = await InitializedDbAsync();
        var ado = new FakeAdoGitClient();
        ado.Commit(new() { ["a.txt"] = "a" });
        var output = new StringWriter();

        var exit = await Commands(t, ado, output).RunAsync(args, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains(expected, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_lists_branches_with_progress()
    {
        await using var t = await InitializedDbAsync();
        var ado = new FakeAdoGitClient();
        ado.Commit(new() { ["a.txt"] = "a" });
        await Commands(t, ado, new StringWriter()).RunAsync(["index", "--project", "Fabrikam", "--repo", "web-app"], CancellationToken.None);
        await t.NewContext().IndexedBranches.ExecuteUpdateAsync(s => s
            .SetProperty(b => b.Status, BranchIndexStatus.Indexing)
            .SetProperty(b => b.ProgressDone, 12)
            .SetProperty(b => b.ProgressTotal, 40));
        var output = new StringWriter();

        await Commands(t, ado, output).RunAsync(["status"], CancellationToken.None);

        var text = output.ToString();
        Assert.Contains("Fabrikam/web-app", text, StringComparison.Ordinal);
        Assert.Contains("Indexing 12/40", text, StringComparison.Ordinal);
    }

    private async Task<TestDb> InitializedDbAsync()
    {
        var t = await postgres.CreateMigratedDbAsync();
        await new DatabaseInitializer(t.Context, t.Clock, NullLogger<DatabaseInitializer>.Instance).InitializeAsync("contoso");
        return t;
    }

    private static AdminCommands Commands(TestDb t, FakeAdoGitClient ado, StringWriter output)
    {
        var db = t.NewContext();
        return new AdminCommands(db, ado, new IndexJobQueue(db, t.Clock), t.Clock, Options.Create(new AzureDevOpsOptions { Organization = "contoso" }), output);
    }
}
