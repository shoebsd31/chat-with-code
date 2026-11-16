import type { Host } from "../common/host";

/** Local development only (npm run dev): stands in for Azure DevOps outside its iframe. */
export function createDevelopmentHost(): Host {
  if (new URLSearchParams(window.location.search).get("theme") === "dark") {
    const root = document.documentElement.style;
    root.setProperty("--background-color", "#1e1e1e");
    root.setProperty("--callout-background-color", "#252526");
    root.setProperty("--text-primary-color", "rgba(255, 255, 255, 0.9)");
    root.setProperty("--text-secondary-color", "rgba(255, 255, 255, 0.6)");
    root.setProperty("--palette-neutral-4", "40, 40, 40");
    root.setProperty("--palette-neutral-8", "50, 50, 50");
    root.setProperty("--palette-neutral-10", "68, 68, 68");
    root.setProperty("--palette-primary", "55, 148, 255");
  }

  return {
    context: { organization: "contoso", userName: "Ada Lovelace", projectId: "p1", projectName: "Fabrikam" },
    getAppToken: async () => "dev-app-token",
    getAccessToken: async () => "dev-access-token",
    loaded: () => undefined,
    isDevelopment: true,
  };
}
