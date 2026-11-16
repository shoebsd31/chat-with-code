// Packages the extension as a .vsix for your own private Marketplace publisher.
//
//   npm run package -- --backend-url https://chat.example.com --publisher your-publisher-id [--version 1.0.1]
//
// The backend URL is built into the extension on purpose: org-wide extension settings can be
// changed by any user, so a URL stored there could be redirected to collect sign-in tokens.
import { execFileSync } from "node:child_process";
import { mkdirSync } from "node:fs";

const args = Object.fromEntries(
  process.argv.slice(2).reduce((pairs, arg, i, all) => {
    if (arg.startsWith("--")) pairs.push([arg.slice(2), all[i + 1]?.startsWith("--") ? "true" : all[i + 1]]);
    return pairs;
  }, []),
);

function fail(message) {
  console.error(`Error: ${message}\n\nUsage: npm run package -- --backend-url https://chat.example.com --publisher your-publisher-id [--version 1.0.1]`);
  process.exit(1);
}

const backendUrl = args["backend-url"];
if (!backendUrl) fail("--backend-url is required.");
let url;
try {
  url = new URL(backendUrl);
} catch {
  fail(`"${backendUrl}" isn't a valid URL.`);
}
if (url.protocol !== "https:" && url.hostname !== "localhost") fail("The backend URL must use https:// (Azure DevOps pages can't call http:// services).");
if (!args.publisher || args.publisher === "true") fail("--publisher is required (your Marketplace publisher ID).");

const env = { ...process.env, CWC_BACKEND_URL: url.origin + url.pathname.replace(/\/+$/, "") };
console.log(`Building with backend ${env.CWC_BACKEND_URL} …`);
execFileSync("npm", ["run", "build"], { stdio: "inherit", env });

mkdirSync("out", { recursive: true });
const tfx = ["tfx", "extension", "create", "--manifest-globs", "vss-extension.json", "--publisher", args.publisher, "--output-path", "out", "--no-prompt"];
if (args.version) tfx.push("--override", JSON.stringify({ version: args.version }));
execFileSync("npx", tfx, { stdio: "inherit" });
console.log(`\nDone. Upload the .vsix in out/ at https://marketplace.visualstudio.com/manage/publishers/${args.publisher} and share it with your organization.`);
console.log(`Then set Security__AllowedOrigins to https://${args.publisher}.gallerycdn.vsassets.io on the backend (EXTENSION_ORIGIN in .env).`);
