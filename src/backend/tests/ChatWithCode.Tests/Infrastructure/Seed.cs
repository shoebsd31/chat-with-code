using ChatWithCode.Data;
using ChatWithCode.Data.Entities;

namespace ChatWithCode.Tests.Infrastructure;

internal static class Seed
{
    public static async Task<(Organization Org, Repository Repo, IndexedBranch Main)> RepoWithMainAsync(ChatWithCodeDbContext db)
    {
        var org = new Organization { Id = Guid.NewGuid(), Name = "contoso", CreatedAt = DateTimeOffset.UtcNow };
        var repo = new Repository
        {
            Id = Guid.NewGuid(),
            OrganizationId = org.Id,
            AdoRepositoryId = Guid.NewGuid(),
            AdoProjectId = Guid.NewGuid(),
            ProjectName = "Fabrikam",
            Name = "web-app",
            DefaultBranch = "refs/heads/main",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var main = new IndexedBranch { Id = Guid.NewGuid(), RepositoryId = repo.Id, BranchName = "refs/heads/main", IsDefault = true };

        db.AddRange(org, repo, main);
        await db.SaveChangesAsync();
        return (org, repo, main);
    }

    public static async Task<IndexedBranch> AddBranchAsync(ChatWithCodeDbContext db, Guid repositoryId, string name)
    {
        var branch = new IndexedBranch { Id = Guid.NewGuid(), RepositoryId = repositoryId, BranchName = name };
        db.Add(branch);
        await db.SaveChangesAsync();
        return branch;
    }
}
