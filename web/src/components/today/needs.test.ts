import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { NeedsYou, needTitle } from "./NeedsYou";
import { needsYou } from "../../lib/insights";
import type { Experiment, Investigation, Proposal, Revision } from "../../types";

const NOW = Date.parse("2026-10-05T12:00:00Z");
const step = (id: string, title: string) => ({
  id,
  title,
  rationale: "",
  suggestedAction: "",
  verification: "",
  uncertainty: "",
  evidenceReferences: [],
});
const edit = (id: string, status: "pending" | "applied", snippet: string) => ({
  id,
  status,
  file: "apps.yaml",
  summary: `Edit ${id}`,
  location: "",
  snippet,
  reason: "",
});

/** One household: a suggestion, a to-do two checks repeated, a file edit, one already applied, a trial due and one not. */
const state = {
  proposals: [
    { id: "a", title: "Lower load scaling", status: "Pending", createdAt: "2026-10-05T10:00:00Z", changes: [] },
    { id: "b", title: "Old", status: "Denied", createdAt: "2026-10-05T11:00:00Z", changes: [] },
  ] as unknown as Proposal[],
  investigations: [
    {
      id: "new",
      at: "2026-10-05T09:00:00Z",
      impactPence: 40,
      nextSteps: [step("s", "Check the charger")],
      fileChanges: [edit("f1", "pending", "x: 1"), edit("f2", "applied", "y: 2")],
    },
    { id: "old", at: "2026-10-04T09:00:00Z", nextSteps: [step("s0", "check the  charger")] },
  ] as unknown as Investigation[],
  experiments: [
    {
      id: "due",
      title: "Suggestion applied: House load scaling 1.08 → 1.00",
      status: "Running",
      revisionId: 4,
      startedAt: "2026-09-28T00:00:00Z",
      reviewAt: "2026-10-05T00:00:00Z",
    },
    { id: "later", title: "Later", status: "Running", revisionId: 5, startedAt: "", reviewAt: "2026-10-09T00:00:00Z" },
  ] as unknown as Experiment[],
};
const revisions = [
  { id: 4, changes: [{ key: "load_scaling", before: "1.08", after: "1.00" }] },
] as unknown as Revision[];

const render = (items = needsYou(state, NOW)) =>
  renderToStaticMarkup(createElement(NeedsYou, { items, revisions, onReview: () => {}, onOpen: () => {} }));
const keysIn = (html: string) => [...html.matchAll(/data-key="([^"]+)"/g)].map((m) => m[1]);

describe("Today's Needs you", () => {
  it("shows the same list as Insights: due trials in, applied edits and repeats out", () => {
    const items = needsYou(state, NOW);
    expect(items.map((i) => i.key).sort()).toEqual(["e-due", "f-f1", "p-a", "t-new-s"]);
    // Up to three rows show on a wide screen, in the list's own order, with the full count on the heading.
    const html = render(items);
    expect(keysIn(html)).toEqual(items.slice(0, 3).map((i) => i.key));
    expect(html).toContain(`<span class="needs-count">${items.length}</span>`);
    expect(keysIn(render(items.filter((i) => i.kind === "trial")))).toEqual(["e-due"]);
  });

  it("names each kind as Insights does and sends a trial to its decision", () => {
    const items = needsYou(state, NOW);
    const html = render(items.filter((i) => i.kind === "trial" || i.kind === "file"));
    expect(html).toContain("Trial ready");
    expect(html).toContain("File edit");
    expect(html).toContain('href="#/insights/experiments"');
    const trial = items.find((i) => i.kind === "trial")!;
    expect(needTitle(trial, revisions, [])).not.toMatch(/^Suggestion applied/);
    expect(render(items.filter((i) => i.kind === "proposal"))).toContain("Setting change");
    expect(render(items.filter((i) => i.kind === "todo"))).toContain("To-do");
  });

  it("is hidden when nothing is waiting", () => {
    expect(render([])).toBe("");
  });
});
