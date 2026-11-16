using ChatWithCode.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ChatWithCode.Data;

/// <summary>Metadata database: settings, repositories, index state, job queue and chat history.</summary>
public sealed class ChatWithCodeDbContext(DbContextOptions<ChatWithCodeDbContext> options) : DbContext(options)
{
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<ModelSettings> ModelSettings => Set<ModelSettings>();
    public DbSet<Repository> Repositories => Set<Repository>();
    public DbSet<IndexedBranch> IndexedBranches => Set<IndexedBranch>();
    public DbSet<IndexJob> IndexJobs => Set<IndexJob>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Citation> Citations => Set<Citation>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // Store enums as readable text, so the database stays understandable and enum reordering is safe.
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(32);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Organization>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(50);
            e.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<ModelSettings>(e =>
        {
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            // One organization row (ProjectId null) and one row per project.
            e.HasIndex(x => new { x.OrganizationId, x.ProjectId }).IsUnique().AreNullsDistinct(false);
            e.Property(x => x.AzureOpenAIEndpoint).HasMaxLength(500);
            e.Property(x => x.ChatDeployment).HasMaxLength(200);
            e.Property(x => x.EmbeddingDeployment).HasMaxLength(200);
            e.Property(x => x.AzureSearchEndpoint).HasMaxLength(500);
            e.Property(x => x.AzureSearchIndexName).HasMaxLength(128);
            e.Property(x => x.UpdatedBy).HasMaxLength(200);
            e.ToTable(t => t.HasCheckConstraint(
                "ck_model_settings_scope_project",
                "(scope = 'Organization' AND project_id IS NULL) OR (scope = 'Project' AND project_id IS NOT NULL)"));
        });

        modelBuilder.Entity<Repository>(e =>
        {
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.AdoRepositoryId).IsUnique();
            e.HasIndex(x => x.AdoProjectId);
            e.Property(x => x.ProjectName).HasMaxLength(256);
            e.Property(x => x.Name).HasMaxLength(256);
            e.Property(x => x.DefaultBranch).HasMaxLength(400);
        });

        modelBuilder.Entity<IndexedBranch>(e =>
        {
            e.HasOne(x => x.Repository).WithMany(x => x.Branches).HasForeignKey(x => x.RepositoryId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.RepositoryId, x.BranchName }).IsUnique();
            e.Property(x => x.BranchName).HasMaxLength(400);
            e.Property(x => x.LastIndexedCommit).HasMaxLength(40);
            e.Property(x => x.EmbeddingModel).HasMaxLength(200);
        });

        modelBuilder.Entity<IndexJob>(e =>
        {
            e.HasOne(x => x.IndexedBranch).WithMany().HasForeignKey(x => x.IndexedBranchId).OnDelete(DeleteBehavior.Cascade);
            // The worker looks for the oldest queued job that is due.
            e.HasIndex(x => new { x.State, x.AvailableAt });
            e.HasIndex(x => x.IndexedBranchId);
            e.Property(x => x.TargetCommit).HasMaxLength(40);
            e.Property(x => x.LockedBy).HasMaxLength(200);
        });

        modelBuilder.Entity<Conversation>(e =>
        {
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.OrganizationId, x.UserId, x.UpdatedAt });
            e.Property(x => x.UserId).HasMaxLength(200);
            e.Property(x => x.Title).HasMaxLength(200);
            e.OwnsMany(x => x.Targets, t =>
            {
                t.ToJson();
                t.Property(x => x.Branch).HasMaxLength(400);
            });
        });

        modelBuilder.Entity<Message>(e =>
        {
            e.HasOne<Conversation>().WithMany(x => x.Messages).HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ConversationId, x.CreatedAt });
            e.Property(x => x.Model).HasMaxLength(200);
        });

        modelBuilder.Entity<Citation>(e =>
        {
            e.HasOne<Message>().WithMany(x => x.Citations).HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.MessageId);
            e.Property(x => x.Branch).HasMaxLength(400);
            e.Property(x => x.Path).HasMaxLength(2000);
        });
    }
}
