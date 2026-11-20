namespace ChatWithCode.Api.Admin;

/// <summary>The signed-in user's own usage, as percentages (no money is shown to users).</summary>
public sealed record MyUsageDto(bool Limited, bool Exempt, int MonthPercent, int DayPercent, DateTimeOffset MonthResetsAt, DateTimeOffset DayResetsAt);

public sealed record ModelPriceDto(string Model, decimal InputPerMillion, decimal OutputPerMillion);

public sealed record UsageTotalsDto(int Questions, long InputTokens, long OutputTokens, decimal Cost);

public sealed record UserUsageDto(
    string UserId, string? UserName, int Questions, long InputTokens, long OutputTokens, decimal Cost,
    decimal? OwnAllowance, decimal? Allowance, int Percent);

/// <summary>The admin usage report for one month, with the allowance settings.</summary>
public sealed record UsageReportDto(
    string Month,
    string Currency,
    decimal? MonthlyAllowance,
    int DailyCapPercent,
    IReadOnlyList<ModelPriceDto> Prices,
    IReadOnlyList<string> Models,
    IReadOnlyList<string> UnpricedModels,
    UsageTotalsDto Total,
    UsageTotalsDto Organization,
    IReadOnlyList<UserUsageDto> Users);

public sealed record SaveUsageSettingsRequest(string? Currency, decimal? MonthlyAllowance, IReadOnlyList<ModelPriceDto>? Prices);

public sealed record SetUserAllowanceRequest(decimal? MonthlyAllowance, string? UserName);
