import { describe, expect, it } from "vitest";
import { checklistCounts, shouldAutoOpenSetup, trialWarning } from "./setupApi";
import type { SetupProgress } from "../types";

const progress = (done: Record<string, boolean>, requiredDone = false): SetupProgress => {
  const steps = ["predbat", "collecting", "meters", "ai", "mcp", "reviews", "writes"].map((key) => ({
    key,
    label: key,
    done: done[key] ?? false,
    required: ["predbat", "collecting", "meters", "ai"].includes(key),
  }));
  return { done: steps.filter((s) => s.done).length, total: steps.length, requiredDone, steps };
};
const storage = (value: string | null) => ({ getItem: () => value });

describe("setup progress", () => {
  it("counts Predbat's two steps as the checklist's one row", () => {
    expect(checklistCounts(progress({ predbat: true, collecting: false, ai: true }))).toEqual({
      done: 1,
      total: 6,
      requiredDone: false,
    });
    expect(checklistCounts(progress({ predbat: true, collecting: true, ai: true, writes: true }))).toEqual({
      done: 3,
      total: 6,
      requiredDone: false,
    });
  });

  it("opens Setup only on a fresh live visit to the root while required steps are missing, once a session", () => {
    const missing = progress({ predbat: false });
    expect(shouldAutoOpenSetup("", missing, false, storage(null))).toBe(true);
    expect(shouldAutoOpenSetup("#/today", missing, false, storage(null))).toBe(true);
    expect(shouldAutoOpenSetup("#/plan", missing, false, storage(null))).toBe(false);
    expect(shouldAutoOpenSetup("", missing, true, storage(null))).toBe(false);
    expect(shouldAutoOpenSetup("", missing, false, storage("1"))).toBe(false);
    expect(shouldAutoOpenSetup("", progress({}, true), false, storage(null))).toBe(false);
    expect(shouldAutoOpenSetup("", null, false, storage(null))).toBe(false);
  });

  it("words the trial warnings", () => {
    expect(trialWarning({ id: "a", title: "Lower load", startedAt: "", effect: "rolled back" }, "2 days ago")).toBe(
      "This ends the trial “Lower load” early (started 2 days ago).",
    );
    expect(trialWarning({ id: "a", title: "Lower load", startedAt: "", effect: "confounded" }, "2 days ago")).toMatch(
      /can't be judged fairly/,
    );
  });
});
