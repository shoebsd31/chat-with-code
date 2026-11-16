using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ChatWithCode.Data;

/// <summary>Used only by the `dotnet ef` tool when creating migrations. No database connection is made.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ChatWithCodeDbContext>
{
    public ChatWithCodeDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<ChatWithCodeDbContext>();
        DataServiceCollectionExtensions.ConfigureDb(builder, "Host=localhost;Database=chatwithcode_design");
        return new ChatWithCodeDbContext(builder.Options);
    }
}
