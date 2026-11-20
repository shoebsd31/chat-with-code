import { describe, expect, it } from "vitest";
import type { MyUsage } from "../common/api";
import { formatMoney, formatTokens } from "../settings/UsageCard";
import { usageNote } from "./usageNote";

const usage = (monthPercent: number, dayPercent: number, limited = true): MyUsage => ({
  limited, exempt: false, monthPercent, dayPercent, monthResetsAt: "2026-12-01T00:00:00Z", dayResetsAt: "2026-11-21T00:00:00Z",
});

describe("usageNote", () => {
  it("says nothing without limits or below half", () => {
    expect(usageNote(usage(90, 90, false))).toBeNull();
    expect(usageNote(usage(30, 49))).toBeNull();
    expect(usageNote(null)).toBeNull();
  });

  it("notes from 50% and warns from 80%, showing whichever is higher", () => {
    expect(usageNote(usage(62, 10))).toMatchObject({ tone: "info", text: expect.stringContaining("62% of this month's allowance") });
    expect(usageNote(usage(40, 85))).toMatchObject({ tone: "warn", text: expect.stringContaining("85% of today's limit") });
  });

  it("explains when it's used up", () => {
    expect(usageNote(usage(100, 30))).toMatchObject({ tone: "error", text: expect.stringContaining("used this month's allowance") });
    expect(usageNote(usage(70, 100))).toMatchObject({ tone: "error", text: "You've reached today's limit. It resets at midnight UTC." });
  });
});

describe("formatting", () => {
  it("formats money in the chosen currency, or with a custom label", () => {
    expect(formatMoney(12.5, "USD")).toMatch(/12\.50/);
    expect(formatMoney(3, "XYZ1")).toBe("3.00 XYZ1");
  });

  it("shortens token counts", () => {
    expect([formatTokens(950), formatTokens(12_400), formatTokens(2_350_000), formatTokens(12_000_000)]).toEqual(["950", "12k", "2.4M", "12M"]);
  });
});
