using ChatWithCode.Api.Chat;
using ChatWithCode.Data.Entities;
using ChatWithCode.Data.Usage;

namespace ChatWithCode.Tests.Core;

public sealed class UsageLedgerTests
{
    private static readonly ModelPrice[] Prices = [new() { Model = "gpt-6-luna", InputPerMillion = 2m, OutputPerMillion = 8m }];

    [Fact]
    public void Cost_uses_the_price_per_model_and_reports_models_without_one()
    {
        ModelUsage[] usage = [new("GPT-6-LUNA", 1_000_000, 250_000), new("other", 5_000_000, 5_000_000)];

        Assert.Equal(4.00m, UsageLedger.Cost(usage, Prices)); // 2.00 input + 2.00 output; "other" has no price
        Assert.Equal(["other"], UsageLedger.Unpriced(usage, Prices));
    }

    [Fact]
    public void Months_are_calendar_months_in_utc()
    {
        var (start, end) = UsageLedger.MonthOf(new DateTimeOffset(2026, 12, 31, 23, 30, 0, TimeSpan.FromHours(-5))); // already January in UTC

        Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(new DateTimeOffset(2027, 2, 1, 0, 0, 0, TimeSpan.Zero), end);
    }

    [Fact]
    public void Status_percentages_and_messages()
    {
        var now = new DateTimeOffset(2026, 11, 20, 18, 0, 0, TimeSpan.Zero);
        var resetsMonth = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
        var resetsDay = new DateTimeOffset(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);

        var fine = new UsageStatus(true, "USD", 3m, 10m, 1m, 2m, resetsMonth, resetsDay);
        Assert.Equal((30, 50), (fine.MonthPercent, fine.DayPercent));
        Assert.Null(ChatService.AllowanceMessage(fine, now, 20));

        var day = fine with { DayCost = 2m };
        Assert.Equal("You've reached today's limit (20% of your monthly allowance). It resets at midnight UTC, in about 6 hours.", ChatService.AllowanceMessage(day, now, 20));

        var month = fine with { MonthCost = 10m };
        Assert.StartsWith("You've used this month's Chat with code allowance. It resets on 1 December (UTC).", ChatService.AllowanceMessage(month, now, 20), StringComparison.Ordinal);

        var off = new UsageStatus(false, "USD", 99m, null, 99m, null, resetsMonth, resetsDay);
        Assert.Null(ChatService.AllowanceMessage(off, now, 20));
    }
}
