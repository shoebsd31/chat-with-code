using System.Globalization;
using ChatWithCode.Core.Abstractions;
using ChatWithCode.Core.Domain;
using ChatWithCode.Core.Options;
using ChatWithCode.Data;
using ChatWithCode.Data.Entities;
using ChatWithCode.Data.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChatWithCode.Worker.Cli;

/// <summary>
/// Admin commands for the worker container, until the settings pages in Azure DevOps exist:
///   index  --project NAME --repo NAME [--branch NAME] [--full]
///   status
/// Run with: docker compose run --rm worker &lt;command&gt; …
/// </summary>
public sealed class AdminCommands(
    ChatWithCodeDbContext db,
    IAdoGitClient ado,
    IndexJobQueue queue,
    TimeProvider clock,
    IOptions<AzureDevOpsOptions> adoOptions,
    TextWriter output)
{
    public const string Usage = """
        Usage:
          index --project <project> --repo <repository> [--branch <branch>] [--full]
              Add a repository branch (default: its default branch) and queue it for indexing.
              --full re-reads every file instead of only changed ones.
          status
              Show every indexed branch with its status and progress.
        """;

    public static bool IsCommand(string[] args) => args is ["index", ..] or ["status", ..] or ["help" or "--help" or "-h", ..];

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        try
        {
            return args[0] switch
            {
                "index" => await IndexAsync(ParseOptions(args[1..]), cancellationToken),
                "status" => await StatusAsync(cancellationToken),
                _ => PrintUsage(),
            };
        }
        catch (CommandException ex)
        {
            await output.WriteLineAsync($"Error: {ex.Message}");
            return 1;
        }
    }

    private async Task<int> IndexAsync(Dictionary<string, string?> options, CancellationToken cancellationToken)
    {
        var project = Required(options, "project");
        var repoName = Required(options, "repo");
        var full = options.ContainsKey("full");

        var organization = await db.Organizations.SingleOrDefaultAsync(o => o.Name == adoOptions.Value.Organization, cancellationToken)
            ?? throw new CommandException("The database isn't set up yet. Start the API first (docker compose up -d), then run this again.");

        AdoRepository? found;
        try
        {
            found = await ado.GetRepositoryAsync(project, repoName, cancellationToken);
        }
        catch (AdoAccessException ex)
        {
            throw new CommandException(ex.Message);
        }

        if (found is null)
        {
            throw new CommandException(
                $"Repository '{project}/{repoName}' wasn't found in organization '{organization.Name}'. Check the names, and that " +
                "the service principal has access to the project.");
        }

        var branchRef = options.TryGetValue("branch", out var b) && !string.IsNullOrWhiteSpace(b)
            ? (b.StartsWith("refs/", StringComparison.Ordinal) ? b : $"refs/heads/{b}")
            : found.DefaultBranch ?? throw new CommandException($"Repository '{found.Name}' is empty (it has no default branch yet).");

        if (await ado.GetBranchHeadAsync(found.Id, branchRef, cancellationToken) is null)
        {
            throw new CommandException($"Branch '{branchRef}' doesn't exist in '{found.ProjectName}/{found.Name}'.");
        }

        var repo = await db.Repositories.SingleOrDefaultAsync(r => r.AdoRepositoryId == found.Id, cancellationToken);
        if (repo is null)
        {
            repo = new Repository
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = organization.Id,
                AdoRepositoryId = found.Id,
                AdoProjectId = found.ProjectId,
                ProjectName = found.ProjectName,
                Name = found.Name,
                CreatedAt = clock.GetUtcNow(),
            };
            db.Repositories.Add(repo);
        }

        repo.ProjectName = found.ProjectName;
        repo.Name = found.Name;
        repo.DefaultBranch = found.DefaultBranch;
        repo.IsEnabled = true;

        var branch = await db.IndexedBranches.SingleOrDefaultAsync(x => x.RepositoryId == repo.Id && x.BranchName == branchRef, cancellationToken);
        if (branch is null)
        {
            branch = new IndexedBranch { Id = Guid.CreateVersion7(), RepositoryId = repo.Id, BranchName = branchRef };
            db.IndexedBranches.Add(branch);
        }

        branch.IsDefault = branchRef == found.DefaultBranch;
        await db.SaveChangesAsync(cancellationToken);

        await queue.EnqueueAsync(branch.Id, full ? IndexJobType.Full : IndexJobType.Incremental, cancellationToken: cancellationToken);
        await output.WriteLineAsync($"Queued {(full ? "a full" : "an")} index of {found.ProjectName}/{found.Name} ({ShortBranch(branchRef)}).");
        await output.WriteLineAsync("The running worker picks it up within a few seconds. Check progress with: docker compose run --rm worker status");
        return 0;
    }

    private async Task<int> StatusAsync(CancellationToken cancellationToken)
    {
        var rows = await db.IndexedBranches.AsNoTracking()
            .Include(b => b.Repository)
            .OrderBy(b => b.Repository!.ProjectName).ThenBy(b => b.Repository!.Name).ThenBy(b => b.BranchName)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            await output.WriteLineAsync("No repositories are being indexed yet. Add one with: index --project <project> --repo <repository>");
            return 0;
        }

        var table = rows.Select(r => new[]
        {
            $"{r.Repository!.ProjectName}/{r.Repository.Name}",
            ShortBranch(r.BranchName),
            r.Status == BranchIndexStatus.Indexing && r.ProgressTotal > 0
                ? $"Indexing {r.ProgressDone}/{r.ProgressTotal}"
                : r.Status.ToString(),
            r.FileCount.ToString(CultureInfo.InvariantCulture),
            r.ChunkCount.ToString(CultureInfo.InvariantCulture),
            r.LastIndexedAt?.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) ?? "never",
            r.LastIndexedCommit is { } c ? c[..Math.Min(8, c.Length)] : "-",
        }).ToList();
        string[] header = ["Repository", "Branch", "Status", "Files", "Chunks", "Last indexed", "Commit"];
        var widths = header.Select((h, i) => Math.Max(h.Length, table.Max(row => row[i].Length))).ToArray();

        await output.WriteLineAsync(string.Join("  ", header.Select((h, i) => h.PadRight(widths[i]))));
        foreach (var row in table)
        {
            await output.WriteLineAsync(string.Join("  ", row.Select((v, i) => v.PadRight(widths[i]))));
        }

        foreach (var failed in rows.Where(r => r.LastError is not null))
        {
            await output.WriteLineAsync($"\n{failed.Repository!.Name} ({ShortBranch(failed.BranchName)}) last error: {failed.LastError}");
        }

        return 0;
    }

    private int PrintUsage()
    {
        output.WriteLine(Usage);
        return 0;
    }

    private static Dictionary<string, string?> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new CommandException($"Unexpected argument '{args[i]}'.\n{Usage}");
            }

            var name = args[i][2..];
            var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            result[name] = hasValue ? args[++i] : null;
        }

        return result;
    }

    private static string Required(Dictionary<string, string?> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new CommandException($"--{name} is required.\n{Usage}");

    private static string ShortBranch(string branchRef) =>
        branchRef.StartsWith("refs/heads/", StringComparison.Ordinal) ? branchRef["refs/heads/".Length..] : branchRef;

    private sealed class CommandException(string message) : Exception(message);
}
