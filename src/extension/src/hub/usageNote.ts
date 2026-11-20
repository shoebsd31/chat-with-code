import type { MyUsage } from "../common/api";

export type UsageNote = { tone: "info" | "warn" | "error"; text: string };

const day = (iso: string) => new Date(iso).toLocaleDateString(undefined, { day: "numeric", month: "short", timeZone: "UTC" });

/**
 * What to tell the user about their allowance, or null to say nothing: quiet below 50%,
 * a note from 50%, a warning from 80%, and a clear message once it's used up.
 */
export function usageNote(usage: MyUsage | null | undefined): UsageNote | null {
  if (!usage?.limited) return null;
  if (usage.monthPercent >= 100) {
    return { tone: "error", text: `You've used this month's allowance. It resets on ${day(usage.monthResetsAt)}. Ask an admin if you need more.` };
  }
  if (usage.dayPercent >= 100) {
    return { tone: "error", text: "You've reached today's limit. It resets at midnight UTC." };
  }

  const month = usage.monthPercent;
  const today = usage.dayPercent;
  if (Math.max(month, today) < 50) return null;
  const tone = Math.max(month, today) >= 80 ? "warn" : "info";
  return today > month
    ? { tone, text: `You've used ${today}% of today's limit · resets at midnight UTC` }
    : { tone, text: `You've used ${month}% of this month's allowance · resets ${day(usage.monthResetsAt)}` };
}
