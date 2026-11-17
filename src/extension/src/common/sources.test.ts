import { describe, expect, it } from "vitest";
import type { Citation } from "./api";
import { groupSources, sourceKind } from "./sources";

describe("sourceKind", () => {
  it.each([
    ["src/Orders/OrderService.cs", "code"],
    ["web/app.tsx", "code"],
    ["scripts/deploy.ps1", "code"],
    ["README.md", "docs"],
    ["docs/guide.rst", "docs"],
    ["LICENSE", "docs"],
    ["appsettings.json", "config"],
    [".github/workflows/ci.yml", "config"],
    ["src/Api/Api.csproj", "config"],
    ["infra/main.bicep", "config"],
    ["Dockerfile", "config"],
    ["deploy/Dockerfile.worker", "config"],
    [".env.example", "config"],
    [".gitignore", "config"],
  ])("%s is %s", (path, kind) => expect(sourceKind(path)).toBe(kind));
});

describe("groupSources", () => {
  const cite = (number: number, path: string): Citation => ({
    number, path, repositoryId: "r", project: null, repository: null, branch: "refs/heads/main", startLine: 1, endLine: 2, symbol: null, url: null, used: true,
  });

  it("groups in the order Code, Docs, Config and keeps citation order within a group", () => {
    const groups = groupSources([cite(1, "README.md"), cite(2, "src/b.ts"), cite(3, "app.json"), cite(4, "src/a.ts")]);

    expect(groups.map((g) => [g.label, g.citations.map((c) => c.number)])).toEqual([
      ["Code", [2, 4]],
      ["Docs", [1]],
      ["Config", [3]],
    ]);
  });

  it("leaves out empty groups", () => {
    expect(groupSources([cite(1, "a.cs"), cite(2, "b.cs")]).map((g) => g.kind)).toEqual(["code"]);
  });
});
