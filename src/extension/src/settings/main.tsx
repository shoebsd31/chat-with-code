import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import "../common/styles.css";
import { createApi } from "../common/api";
import { initHost, markTheme } from "../common/host";
import { Settings } from "./Settings";

const scope = new URLSearchParams(window.location.search).get("scope") === "project" ? "project" : "organization";
const host = await initHost();
markTheme();
// import.meta.env.DEV is false in production builds, so the development stand-in is left out of the package.
const api = import.meta.env.DEV && host.isDevelopment ? (await import("../dev/mockApi")).createMockApi() : createApi(host);

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <Settings host={host} api={api} scope={scope} />
  </StrictMode>,
);
