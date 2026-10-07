import { describe, expect, it } from "vitest";
import { closeBody, closePaths, closedMessage, inboxClosePaths } from "./closing";
import { closedItems, closedStatus, findingClosedStatus } from "./insights";
import type { Investigation, Proposal } from "../types";

const investigation = (over: Partial<Investigation> = {}) =>
  ({
    id: "inv 1",
    at: "2026-10-06T09:00:00Z",
    title: "Battery charged at 1.7 kW",
    summary: "",
    category: "",
    confidence: "",
    evidence: [],
    steps: [],
    provider: "Api",
    request: { question: null, from: null, to: null },
    status: "Completed",
    toolEvidence: [],
    evidenceReferences: [],
    ...over,
  }) as Investigation;

describe("closePaths", () => {
  it("names the close and reopen endpoints for every kind", () => {
    expect(closePaths("proposal", "inv", "p1")).toEqual({
      close: "/proposals/p1/deny",
      reopen: "/proposals/p1/reopen",
    });
    expect(closePaths("todo", "inv 1", "s/1")).toEqual({
      close: "/investigations/inv%201/followups/s%2F1/dismiss",
      reopen: "/investigations/inv%201/followups/s%2F1/reopen",
    });
    expect(closePaths("file", "inv", "f1").close).toBe("/investigations/inv/filechanges/f1/dismiss");
    expect(closePaths("finding", "inv")).toEqual({
      close: "/investigations/inv/dismiss",
      reopen: "/investigations/inv/reopen",
    });
  });

  it("gives every row of Needs you a close path except trials and old to-dos without an id", () => {
    const inv = investigation();
    expect(
      inboxClosePaths({
        kind: "todo",
        key: "t",
        at: "",
        impact: 0,
        investigation: inv,
        step: { title: "x" } as never,
        index: 0,
      }),
    ).toBeNull();
    expect(
      inboxClosePaths({
        kind: "todo",
        key: "t",
        at: "",
        impact: 0,
        investigation: inv,
        step: { id: "s1", title: "x" } as never,
        index: 0,
      })?.close,
    ).toBe("/investigations/inv%201/followups/s1/dismiss");
    expect(inboxClosePaths({ kind: "trial", key: "e", at: "", impact: 0, experiment: {} as never })).toBeNull();
    expect(
      inboxClosePaths({
        kind: "proposal",
        key: "p",
        at: "",
        impact: 0,
        proposal: { id: "p1", investigationId: "inv" } as Proposal,
      })?.reopen,
    ).toBe("/proposals/p1/reopen");
  });
});

describe("closing words", () => {
  it("says what happened and that it won't come back", () => {
    expect(closedMessage("todo", "done")).toBe("Marked as done.");
    expect(closedMessage("file", "not_needed")).toBe("Closed as not needed. Joule won't raise it again for 30 days.");
    expect(closedMessage("finding", "dismissed")).toMatch(/^Finding dismissed, with everything from it\./);
    expect(closeBody("dismissed", "  ")).toEqual({ outcome: "dismissed" });
    expect(closeBody("dismissed", " Wrong meter ")).toEqual({ outcome: "dismissed", note: "Wrong meter" });
  });

  it("names how each item closed", () => {
    expect(closedStatus("todo", "closed", [], "Not needed", null)).toBe("Not needed");
    expect(closedStatus("todo", "closed", [], "Done by you", null)).toBe("Done");
    // A dismiss with no reason is a dismiss, not a Done (older Done buttons wrote "Dismissed by user" with no note).
    expect(closedStatus("todo", "closed", [], "Dismissed by you", null)).toBe("You dismissed this");
    expect(closedStatus("proposal", "Denied", [], "Not needed")).toBe("Not needed");
    expect(closedStatus("proposal", "Denied", [], "Findings dismissed by user")).toBe("Closed with its findings");
    expect(closedStatus("todo", "closed", [], "About Joule's own connection to Predbat, not your system")).toBe(
      "About Joule's own connection, not your system",
    );
    expect(findingClosedStatus({ closedReason: "resolved" })).toBe("Nothing left to do");
    expect(findingClosedStatus({ closedReason: "not_needed" })).toBe("Not needed");
    expect(findingClosedStatus({ closedReason: null })).toBe("You dismissed this");
  });

  it("lists closed findings under Closed, but not the repeats Joule held back", () => {
    const items = closedItems({
      proposals: [],
      investigations: [
        investigation({ id: "a", dismissedAt: "2026-10-06T10:00:00Z", closedReason: "not_needed" }),
        investigation({ id: "b", dismissedAt: "2026-10-06T11:00:00Z", closedReason: "repeat" }),
        investigation({ id: "c" }),
      ],
    });
    expect(items.map((i) => [i.kind, i.key, i.status])).toEqual([["finding", "i-a", "Not needed"]]);
  });
});
