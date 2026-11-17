import type { Citation } from "./api";

export type SourceKind = "code" | "docs" | "config";

export const sourceKinds: { kind: SourceKind; label: string }[] = [
  { kind: "code", label: "Code" },
  { kind: "docs", label: "Docs" },
  { kind: "config", label: "Config" },
];

const docExtensions = new Set(["md", "mdx", "markdown", "rst", "adoc", "asciidoc", "txt", "rtf"]);
const docNames = new Set(["readme", "license", "licence", "changelog", "contributing", "authors", "notice", "copying", "security"]);

const configExtensions = new Set([
  "json", "jsonc", "json5", "yaml", "yml", "toml", "ini", "cfg", "conf", "config", "env", "properties", "xml",
  "csproj", "vbproj", "fsproj", "sln", "props", "targets", "nuspec", "editorconfig", "bicep", "tf", "tfvars", "hcl",
  "gradle", "lock", "plist", "mod", "sum",
]);
const configNames = new Set([
  "dockerfile", "makefile", "jenkinsfile", "procfile", "vagrantfile", "gemfile", "rakefile", "cmakelists.txt",
  ".gitignore", ".gitattributes", ".dockerignore", ".npmrc", ".nvmrc", ".prettierrc", ".eslintrc", ".babelrc",
  ".chatwithcodeignore", "codeowners",
]);

/** Code, Docs or Config, from the file name. Anything unknown counts as code. */
export function sourceKind(path: string): SourceKind {
  const name = (path.split("/").pop() ?? path).toLowerCase();
  const dot = name.lastIndexOf(".");
  const extension = dot > 0 ? name.slice(dot + 1) : "";
  const stem = dot > 0 ? name.slice(0, dot) : name;

  if (configNames.has(name) || name.startsWith("dockerfile") || name.startsWith(".env")) return "config";
  if (docExtensions.has(extension) || docNames.has(stem) || docNames.has(name)) return "docs";
  if (configExtensions.has(extension) || (name.startsWith(".") && dot <= 0)) return "config";
  return "code";
}

/** Citations grouped by kind (Code, Docs, Config), keeping their order within each group; empty groups are left out. */
export function groupSources(citations: Citation[]): { kind: SourceKind; label: string; citations: Citation[] }[] {
  return sourceKinds
    .map(({ kind, label }) => ({ kind, label, citations: citations.filter((c) => sourceKind(c.path) === kind) }))
    .filter((group) => group.citations.length > 0);
}
