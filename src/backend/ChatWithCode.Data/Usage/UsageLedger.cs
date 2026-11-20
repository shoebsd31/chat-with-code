using ChatWithCode.Core.Domain;
using ChatWithCode.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ChatWithCode.Data.Usage;

/// <summary>A user's usage against their allowance. Costs are in <see cref="Currency"/>.</summary>
public sealed record UsageStatus(
    bool Limited,
    string Currency,
    decimal MonthCost,
    decimal? MonthAllowance,
    decimal DayCost,
    decimal? DayCap,
    DateTimeOffset MonthResetsAt,
    DateTimeOffset DayResetsAt)
{
    public bool MonthUsedUp => MonthAllowance is { } allowance && MonthCost >= allowance;
    public bool DayUsedUp => DayCap is { } cap && DayCost >= cap;

    public int MonthPercent => MonthAllowance is > 0 and var a ? (int)Math.Min(999, Math.Floor(MonthCost / a * 100)) : 0;
    public int DayPercent => DayCap is > 0 and var c ? (int)Math.Min(999, Math.Floor(DayCost / c * 100)) : 0;
}

/// <summary>Tokens per model for a period, priced with the current prices.</summary>
public sealed record ModelUsage(string Model, long InputTokens, long OutputTokens);

/// <summary>
/// Records every chat model call and works out costs and allowances. Costs are calculated when read,
/// from the token counts and the current prices, so prices entered later apply to past usage too.
/// Periods are calendar months and days in UTC.
/// </summary>
public sealed class UsageLedger(ChatWithCodeDbContext db, TimeProvider clock)
{
    public async Task RecordAsync(
        Guid organizationId, string? userId, string? userName, Guid? projectId, string model, UsageKind kind,
        int inputTokens, int outputTokens, CancellationToken cancellationToken)
    {
        db.UsageRecords.Add(new UsageRecord
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            UserId = userId,
            UserName = userName,
            ProjectId = projectId,
            Model = string.IsNullOrWhiteSpace(model) ? "unknown" : model,
            Kind = kind,
            InputTokens = Math.Max(0, inputTokens),
            OutputTokens = Math.Max(0, outputTokens),
            CreatedAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<UsageSettings> SettingsAsync(Guid organizationId, CancellationToken cancellationToken) =>
        await db.UsageSettings.AsNoTracking().SingleOrDefaultAsync(s => s.OrganizationId == organizationId, cancellationToken)
        ?? new UsageSettings { OrganizationId = organizationId };

    /// <summary>A user's usage this month and today against their allowance (their own, or the organization's).</summary>
    public async Task<UsageStatus> StatusAsync(Guid organizationId, string userId, int dailyCapPercent, CancellationToken cancellationToken)
    {
        var settings = await SettingsAsync(organizationId, cancellationToken);
        var (monthStart, monthEnd) = MonthOf(clock.GetUtcNow());
        var dayStart = new DateTimeOffset(clock.GetUtcNow().UtcDateTime.Date, TimeSpan.Zero);
        var dayEnd = dayStart.AddDays(1);

        var own = await db.UserAllowances.AsNoTracking()
            .Where(a => a.OrganizationId == organizationId && a.UserId == userId)
            .Select(a => (decimal?)a.MonthlyAllowance)
            .SingleOrDefaultAsync(cancellationToken);
        var allowance = own ?? settings.MonthlyAllowance;

        var month = await TokensByModelAsync(organizationId, userId, monthStart, monthEnd, cancellationToken);
        var day = await TokensByModelAsync(organizationId, userId, dayStart, dayEnd, cancellationToken);
        return new UsageStatus(
            Limited: allowance is not null,
            settings.Currency,
            Cost(month, settings.Prices),
            allowance,
            Cost(day, settings.Prices),
            allowance is { } a ? Math.Round(a * dailyCapPercent / 100m, 4) : null,
            monthEnd,
            dayEnd);
    }

    /// <summary>Tokens per model for one user (or everyone, when userId is null) in a period.</summary>
    public async Task<IReadOnlyList<ModelUsage>> TokensByModelAsync(Guid organizationId, string? userId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var query = db.UsageRecords.AsNoTracking().Where(r => r.OrganizationId == organizationId && r.CreatedAt >= from && r.CreatedAt < to);
        if (userId is not null)
        {
            query = query.Where(r => r.UserId == userId && r.Kind != UsageKind.StarterQuestions);
        }

        return await query
            .GroupBy(r => r.Model)
            .Select(g => new ModelUsage(g.Key, g.Sum(r => (long)r.InputTokens), g.Sum(r => (long)r.OutputTokens)))
            .ToListAsync(cancellationToken);
    }

    /// <summary>The cost of some usage at the given prices. Models without a price cost nothing (see <see cref="Unpriced"/>).</summary>
    public static decimal Cost(IEnumerable<ModelUsage> usage, IReadOnlyCollection<ModelPrice> prices)
    {
        decimal total = 0;
        foreach (var u in usage)
        {
            if (PriceFor(prices, u.Model) is { } price)
            {
                total += u.InputTokens * price.InputPerMillion / 1_000_000m + u.OutputTokens * price.OutputPerMillion / 1_000_000m;
            }
        }

        return Math.Round(total, 4);
    }

    /// <summary>Models in the usage that have no price, so the admin can be told costs are incomplete.</summary>
    public static IReadOnlyList<string> Unpriced(IEnumerable<ModelUsage> usage, IReadOnlyCollection<ModelPrice> prices) =>
        usage.Where(u => PriceFor(prices, u.Model) is null).Select(u => u.Model).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    public static ModelPrice? PriceFor(IReadOnlyCollection<ModelPrice> prices, string model) =>
        prices.FirstOrDefault(p => string.Equals(p.Model, model, StringComparison.OrdinalIgnoreCase));

    /// <summary>The calendar month (UTC) containing the moment: [start, next month's start).</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) MonthOf(DateTimeOffset moment)
    {
        var utc = moment.UtcDateTime;
        var start = new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
        return (start, start.AddMonths(1));
    }
}
