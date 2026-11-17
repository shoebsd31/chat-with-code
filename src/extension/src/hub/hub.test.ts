import { describe, expect, it } from "vitest";
import type { Branch, Repository } from "../common/api";
import { generalExamples, starterQuestions } from "./App";

const branch = (name: string, suggestedQuestions?: string[]): Branch => ({
  name, isDefault: true, status: "Ready", lastIndexedAt: null, fileCount: 1, progressDone: 0, progressTotal: 0, suggestedQuestions,
});
const repo = (id: string, branches: Branch[]): Repository => ({ id, projectId: "p", project: "P", name: id, branches });

describe("starterQuestions", () => {
  const repositories = [
    repo("web", [branch("refs/heads/main", ["W1", "W2", "W3", "W4", "W5"]), branch("refs/heads/dev", ["D1"])]),
    repo("api", [branch("refs/heads/main", ["A1", "A2", "A3"])]),
    repo("docs", [branch("refs/heads/main")]),
  ];

  it("uses the selected branch's own questions", () => {
    expect(starterQuestions(repositories, [{ repositoryId: "web", branch: "refs/heads/dev" }])).toEqual(["D1"]);
  });

  it("takes questions in turn from each selected repository", () => {
    const picked = starterQuestions(repositories, [
      { repositoryId: "web", branch: "refs/heads/main" },
      { repositoryId: "api", branch: "refs/heads/main" },
    ]);
    expect(picked).toEqual(["W1", "A1", "W2", "A2", "W3"]);
  });

  it("falls back to general examples when there are none yet", () => {
    expect(starterQuestions(repositories, [{ repositoryId: "docs", branch: "refs/heads/main" }])).toEqual(generalExamples);
    expect(starterQuestions(repositories, [])).toEqual(generalExamples);
  });
});
