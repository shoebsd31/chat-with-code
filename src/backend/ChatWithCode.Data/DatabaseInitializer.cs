using ChatWithCode.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ChatWithCode.Data;

/// <summary>Applies pending migrations and makes sure the configured organization has a row.</summary>
public sealed class DatabaseInitializer(ChatWithCodeDbContext db, TimeProvider clock, ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeAsync(string organizationName, CancellationToken cancellationToken = default)
    {
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count > 0)
        {
            logger.LogInformation("Applying {Count} database migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
            await db.Database.MigrateAsync(cancellationToken);
        }

        if (!await db.Organizations.AnyAsync(o => o.Name == organizationName, cancellationToken))
        {
            db.Organizations.Add(new Organization
            {
                Id = Guid.CreateVersion7(),
                Name = organizationName,
                CreatedAt = clock.GetUtcNow(),
            });
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Registered Azure DevOps organization {Organization}", organizationName);
        }
    }
}
