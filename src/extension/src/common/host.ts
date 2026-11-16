import * as SDK from "azure-devops-extension-sdk";

/** Where the extension is running and who is signed in. */
export interface HostContext {
  organization: string;
  userName: string;
  projectId?: string;
  projectName?: string;
}

/** The Azure DevOps host page, or a stand-in during local development. */
export interface Host {
  context: HostContext;
  /** Proves to the backend that a request comes from this extension. */
  getAppToken(): Promise<string>;
  /** The user's own Azure DevOps token; the backend asks Azure DevOps what it may read. */
  getAccessToken(): Promise<string>;
  /** Tell Azure DevOps the page has rendered (hides its loading spinner). */
  loaded(): void;
  /** True for the local development stand-in. */
  isDevelopment: boolean;
}

const ProjectPageServiceId = "ms.vss-tfs-web.tfs-page-data-service";

interface ProjectPageService {
  getProject(): Promise<{ id: string; name: string } | undefined>;
}

export async function initHost(): Promise<Host> {
  if (import.meta.env.DEV && window.self === window.top) {
    const { createDevelopmentHost } = await import("../dev/devHost");
    return createDevelopmentHost();
  }

  await SDK.init({ loaded: false, applyTheme: true });
  await SDK.ready();
  const project = await SDK.getService<ProjectPageService>(ProjectPageServiceId)
    .then((service) => service.getProject())
    .catch(() => undefined);

  return {
    context: {
      organization: SDK.getHost().name,
      userName: SDK.getUser().displayName,
      projectId: project?.id,
      projectName: project?.name,
    },
    getAppToken: () => SDK.getAppToken(),
    getAccessToken: () => SDK.getAccessToken(),
    loaded: () => SDK.notifyLoadSucceeded(),
    isDevelopment: false,
  };
}

/**
 * Adds "theme-dark" to the page when Azure DevOps uses a dark theme. The theme is applied as
 * CSS variables (not prefers-color-scheme), so this checks the actual background brightness.
 */
export function markTheme(): void {
  const rgb = getComputedStyle(document.body).backgroundColor.match(/\d+/g)?.map(Number) ?? [255, 255, 255];
  const dark = (rgb[0] * 299 + rgb[1] * 587 + rgb[2] * 114) / 1000 < 128;
  document.documentElement.classList.toggle("theme-dark", dark);
}
