using System.Globalization;
using ChatWithCode.Api.Auth;
using ChatWithCode.Api.Chat;
using ChatWithCode.Core.Domain;
using ChatWithCode.Core.Options;
using ChatWithCode.Data;
using ChatWithCode.Data.Entities;
using ChatWithCode.Data.Usage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChatWithCode.Api.Admin;

/// <summary>Usage allowances: the user's own usage, and the admin report, prices and per-user allowances.</summary>
public sealed class UsageAdminService(
    ChatWithCodeDbContext db,
    UsageLedger ledger,
    IOptions<ChatOptions> chatOptions,
    IOptions<AzureDevOpsOptions> adoOptions,
    TimeProvider clock)
{
    private const decimal MaxAmount = 1_000_000m;

    public async Task<MyUsageDto> MineAsync(UserAccess access, CancellationToken ct)
    {
        var status = await ledger.StatusAsync(await OrganizationIdAsync(ct), access.User.Id, chatOptions.Value.DailyCapPercent, ct);
        var limited = status.Limited && !access.IsAdmin;
        return new MyUsageDto(limited, status.Limited && access.IsAdmin,
            limited ? status.MonthPercent : 0, limited ? status.DayPercent : 0, status.MonthResetsAt, status.DayResetsAt);
    }

    public async Task<UsageReportDto> ReportAsync(UserAccess access, string? month, CancellationToken ct)
    {
        RequireAdmin(access);
        var orgId = await OrganizationIdAsync(ct);
        var (from, to) = ParseMonth(month);
        var settings = await ledger.SettingsAsync(orgId, ct);
        var prices = settings.Prices;

        var rows = await db.UsageRecords.AsNoTracking()
            .Where(r => r.OrganizationId == orgId && r.CreatedAt >= from && r.CreatedAt < to)
            .GroupBy(r => new { r.UserId, r.Model, r.Kind })
            .Select(g => new
            {
                g.Key.UserId, g.Key.Model, g.Key.Kind, Calls = g.Count(),
                Input = g.Sum(r => (long)r.InputTokens), Output = g.Sum(r => (long)r.OutputTokens),
                UserName = g.Max(r => r.UserName),
            })
            .ToListAsync(ct);

        var own = await db.UserAllowances.AsNoTracking().Where(a => a.OrganizationId == orgId).ToListAsync(ct);
        var ownById = own.ToDictionary(a => a.UserId, StringComparer.Ordinal);

        var users = rows.Where(r => r.UserId is not null).GroupBy(r => r.UserId!)
            .Select(g =>
            {
                var cost = UsageLedger.Cost(g.Select(r => new ModelUsage(r.Model, r.Input, r.Output)), prices);
                var mine = ownById.GetValueOrDefault(g.Key)?.MonthlyAllowance;
                var allowance = mine ?? settings.MonthlyAllowance;
                return new UserUsageDto(
                    g.Key, g.Select(r => r.UserName).FirstOrDefault(n => n is not null) ?? ownById.GetValueOrDefault(g.Key)?.UserName,
                    g.Where(r => r.Kind == UsageKind.Answer).Sum(r => r.Calls), g.Sum(r => r.Input), g.Sum(r => r.Output), cost,
                    mine, allowance, allowance is > 0 ? (int)Math.Floor(cost / allowance.Value * 100) : 0);
            })
            .ToList();

        // People with their own allowance who haven't used anything this month still appear, so it can be changed.
        users.AddRange(own.Where(a => users.All(u => u.UserId != a.UserId))
            .Select(a => new UserUsageDto(a.UserId, a.UserName, 0, 0, 0, 0, a.MonthlyAllowance, a.MonthlyAllowance, 0)));
        users = users.OrderByDescending(u => u.Cost).ThenByDescending(u => u.InputTokens + u.OutputTokens).ThenBy(u => u.UserName, StringComparer.OrdinalIgnoreCase).ToList();

        var all = rows.Select(r => new ModelUsage(r.Model, r.Input, r.Output)).ToList();
        var orgRows = rows.Where(r => r.UserId is null).ToList();
        var configured = await db.ModelSettings.AsNoTracking().Where(s => s.OrganizationId == orgId && s.ChatDeployment != null).Select(s => s.ChatDeployment!).ToListAsync(ct);
        var models = configured.Concat(rows.Select(r => r.Model)).Concat(prices.Select(p => p.Model))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

        return new UsageReportDto(
            from.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            settings.Currency,
            settings.MonthlyAllowance,
            chatOptions.Value.DailyCapPercent,
            prices.Select(p => new ModelPriceDto(p.Model, p.InputPerMillion, p.OutputPerMillion)).ToList(),
            models,
            UsageLedger.Unpriced(all, prices),
            new UsageTotalsDto(rows.Where(r => r.Kind == UsageKind.Answer).Sum(r => r.Calls), rows.Sum(r => r.Input), rows.Sum(r => r.Output), UsageLedger.Cost(all, prices)),
            new UsageTotalsDto(0, orgRows.Sum(r => r.Input), orgRows.Sum(r => r.Output), UsageLedger.Cost(orgRows.Select(r => new ModelUsage(r.Model, r.Input, r.Output)), prices)),
            users);
    }

    public async Task<UsageReportDto> SaveSettingsAsync(UserAccess access, SaveUsageSettingsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireAdmin(access);
        var currency = (request.Currency ?? "USD").Trim();
        if (currency.Length is 0 or > 8)
        {
            throw new ChatRequestException(400, "Enter a currency of up to 8 characters, like USD or EUR.");
        }

        CheckAmount(request.MonthlyAllowance, "The monthly allowance");
        var prices = new List<ModelPrice>();
        foreach (var p in request.Prices ?? [])
        {
            var model = p.Model?.Trim() ?? "";
            if (model.Length is 0 or > 200)
            {
                throw new ChatRequestException(400, "Each price needs the name of a chat model deployment.");
            }

            CheckAmount(p.InputPerMillion, $"The input price of {model}");
            CheckAmount(p.OutputPerMillion, $"The output price of {model}");
            if (prices.Any(x => string.Equals(x.Model, model, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ChatRequestException(400, $"{model} has more than one price.");
            }

            prices.Add(new ModelPrice { Model = model, InputPerMillion = p.InputPerMillion, OutputPerMillion = p.OutputPerMillion });
        }

        var orgId = await OrganizationIdAsync(ct);
        var row = await db.UsageSettings.SingleOrDefaultAsync(s => s.OrganizationId == orgId, ct);
        if (row is null)
        {
            row = new UsageSettings { Id = Guid.CreateVersion7(), OrganizationId = orgId };
            db.UsageSettings.Add(row);
        }

        row.Currency = currency;
        row.MonthlyAllowance = request.MonthlyAllowance;
        row.Prices = prices;
        row.UpdatedAt = clock.GetUtcNow();
        row.UpdatedBy = access.User.DisplayName;
        await db.SaveChangesAsync(ct);
        return await ReportAsync(access, null, ct);
    }

    public async Task<UsageReportDto> SetUserAllowanceAsync(UserAccess access, string userId, SetUserAllowanceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireAdmin(access);
        if (string.IsNullOrWhiteSpace(userId) || userId.Length > 200)
        {
            throw new ChatRequestException(400, "Unknown user.");
        }

        CheckAmount(request.MonthlyAllowance, "The allowance");
        var orgId = await OrganizationIdAsync(ct);
        var row = await db.UserAllowances.SingleOrDefaultAsync(a => a.OrganizationId == orgId && a.UserId == userId, ct);
        if (request.MonthlyAllowance is not { } amount)
        {
            if (row is not null)
            {
                db.UserAllowances.Remove(row); // back to the organization's allowance
            }
        }
        else
        {
            if (row is null)
            {
                row = new UserAllowance { Id = Guid.CreateVersion7(), OrganizationId = orgId, UserId = userId };
                db.UserAllowances.Add(row);
            }

            row.MonthlyAllowance = amount;
            row.UserName = request.UserName?.Trim() is { Length: > 0 and <= 200 } name ? name : row.UserName;
            row.UpdatedAt = clock.GetUtcNow();
            row.UpdatedBy = access.User.DisplayName;
        }

        await db.SaveChangesAsync(ct);
        return await ReportAsync(access, null, ct);
    }

    private (DateTimeOffset From, DateTimeOffset To) ParseMonth(string? month)
    {
        if (string.IsNullOrWhiteSpace(month))
        {
            return UsageLedger.MonthOf(clock.GetUtcNow());
        }

        if (!DateTime.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            throw new ChatRequestException(400, "The month must look like 2026-11.");
        }

        return UsageLedger.MonthOf(new DateTimeOffset(parsed.Year, parsed.Month, 1, 0, 0, 0, TimeSpan.Zero));
    }

    private static void CheckAmount(decimal? amount, string what)
    {
        if (amount is < 0 or > MaxAmount)
        {
            throw new ChatRequestException(400, $"{what} must be between 0 and {MaxAmount:N0}.");
        }
    }

    private static void RequireAdmin(UserAccess access)
    {
        if (!access.IsAdmin)
        {
            throw new ChatRequestException(403, "Only Chat with code admins can see usage and change allowances.");
        }
    }

    private Task<Guid> OrganizationIdAsync(CancellationToken ct) =>
        db.Organizations.Where(o => o.Name == adoOptions.Value.Organization).Select(o => o.Id).SingleAsync(ct);
}
