import { beforeAll, describe, expect, it } from "vitest";
import {
  changeView,
  closedStatus,
  closedItems,
  confounderChips,
  failureView,
  headlineOf,
  impactChip,
  impactChipFor,
  investigationVerdict,
  needsYou,
  quietChecks,
  requestLabel,
  runTimeline,
  savingText,
  stepLabel,
  stepsOf,
  suggestedBy,
  trialView,
  trialHeading,
  trialDecisionText,
  wallTimeToIso,
  waitingToConfirm,
  optionDetail,
  verdictLook,
  usageByDay,
  checkForUsage,
  last24h,
  mcpLastUsed,
  todaySummary,
  groupUsageByCheck,
} from "./insights";
import { setHouseholdTimeZone } from "./time";
import type { Experiment, Investigation, Proposal, Setting, Usage } from "../types";

beforeAll(() => setHouseholdTimeZone("Europe/London"));

const inv = (over: Partial<Investigation>): Investigation => ({
  id: over.id ?? "i",
  at: "2026-10-05T10:00:00Z",
  title: "A finding",
  summary: "Summary.",
  category: "",
  confidence: "Medium",
  evidence: [],
  steps: [],
  provider: "ChatGpt",
  request: { question: null, from: null, to: null, scheduled: true },
  status: "Completed",
  toolEvidence: [],
  evidenceReferences: [],
  verdict: "problem",
  ...over,
});
const proposal = (over: Partial<Proposal>): Proposal => ({
  id: "p",
  title: "Lower the max charge rate",
  summary: "",
  expectedEffect: "",
  tradeoff: "",
  confidence: "Medium",
  status: "Pending",
  source: "ChatGpt",
  investigationId: "i",
  estimatedMonthlySavingGbp: null,
  baseRevision: 1,
  reviewDays: 7,
  changes: [],
  evidence: [],
  createdAt: "2026-10-05T02:10:00Z",
  documentationReferences: [],
  evidenceReferences: [],
  ...over,
});

describe("verdicts", () => {
  it("never calls a check that didn't finish a problem, and shows a neutral Finding for a missing verdict", () => {
    expect(investigationVerdict({ status: "Failed", verdict: "problem" })).toBe("didnt_finish");
    expect(investigationVerdict({ status: "Interrupted", verdict: null })).toBe("didnt_finish");
    expect(investigationVerdict({ status: "Completed" })).toBe("finding");
    expect(verdictLook({ status: "Failed", verdict: "problem" })).toEqual({ label: "Didn't finish", tone: "neutral" });
    expect(verdictLook({ status: "Completed", verdict: "problem" }).tone).toBe("warn");
    expect(verdictLook({ status: "Completed", verdict: "opportunity" }).tone).toBe("success");
    expect(verdictLook({ status: "Completed", verdict: "problem", severity: "info" }).label).toBe("Minor");
  });
  it("reads a problem the check says has cleared up as a neutral Resolved", () => {
    expect(verdictLook({ status: "Completed", verdict: "problem", headline: "The export gap is now fixed" })).toEqual({
      label: "Resolved",
      tone: "neutral",
    });
    expect(
      verdictLook({ status: "Completed", verdict: "problem", plain: "The sensor outage resolved itself at 14:00." }),
    ).toEqual({ label: "Resolved", tone: "neutral" });
    expect(verdictLook({ status: "Completed", verdict: "problem", headline: "Export gap is still open" }).label).toBe(
      "Problem",
    );
  });
  it("reads quiet checks and failures as plain headlines", () => {
    expect(headlineOf(inv({ verdict: "no_change", title: "No material change since the last review" }))).toBe(
      "Checked, nothing new",
    );
    expect(headlineOf(inv({ status: "Failed", verdict: null, failureKind: "provider_busy" }))).toBe(
      "ChatGPT didn't answer",
    );
    expect(headlineOf(inv({ headline: "Short", title: "Long title" }))).toBe("Short");
  });
  it("never shows the scheduled prompt as the request", () => {
    expect(
      requestLabel(
        inv({
          request: { question: "Scheduled review of the period… (1) battery…", from: null, to: null, scheduled: true },
        }),
      ),
    ).toBe("Automatic check");
    expect(
      requestLabel(
        inv({ request: { question: null, from: null, to: null, scheduled: true, trigger: "the 3-hour heartbeat" } }),
      ),
    ).toBe("Automatic check (the 3-hour heartbeat)");
    expect(
      requestLabel(inv({ request: { question: "Why was yesterday dear?", from: null, to: null, scheduled: false } })),
    ).toBe("You asked: “Why was yesterday dear?”");
    // No question and not scheduled: just "Check", never a claim that you started it.
    expect(requestLabel(inv({ request: { question: null, from: null, to: null, scheduled: false } }))).toBe("Check");
  });
  it("puts money at stake on a chip", () => {
    expect(impactChip(45)).toEqual({ label: "45p over plan", tone: "neutral" });
    expect(impactChip(-250)).toEqual({ label: "£2.50 under plan", tone: "neutral" });
    expect(impactChip(0.4)).toBeNull();
  });
  it("never says saved, and drops the chip when the headline states money the other way", () => {
    // The live contradiction: "lost about 67p" beside "£1.07 saved".
    expect(impactChipFor({ impactPence: -107, headline: "The battery lost about 67p overnight" })).toBeNull();
    expect(impactChipFor({ impactPence: 40, headline: "Exporting early saved about 40p" })).toBeNull();
    expect(impactChipFor({ impactPence: 67, headline: "The battery lost about 67p overnight" })).toEqual({
      label: "67p over plan",
      tone: "neutral",
    });
    expect(impactChipFor({ impactPence: -107, headline: "Evening load is overestimated" })?.label).toBe(
      "£1.07 under plan",
    );
    for (const pence of [-500, -3, 3, 500]) expect(impactChip(pence)?.label).not.toMatch(/saved/);
  });
});

describe("failureView", () => {
  it("explains a busy provider, keeps the raw text in technical details and offers a retry", () => {
    const f = failureView(
      inv({
        status: "Failed",
        verdict: null,
        failureKind: "provider_busy",
        summary: "ChatGPT response failed (error). Analysis was discarded; no paid API fallback was attempted.",
        providerCode: "server_error",
        nextTryAt: "2026-10-05T11:28:00Z",
      }),
      Date.parse("2026-10-05T10:30:00Z"),
    );
    expect(f.title).toBe("ChatGPT didn't answer");
    expect(f.message).toContain("Nothing in Predbat was changed.");
    expect(f.message).not.toMatch(/fallback|discarded/);
    expect(f.nextTry).toBe("Joule tries again at 12:28");
    expect(f.action).toEqual({ kind: "resume", label: "Try again" });
    expect(f.technical.map((t) => t.label)).toEqual(["What the server said", "Kind", "Provider code"]);
  });
  it("guesses the kind for older records and sends sign-in problems to settings", () => {
    expect(failureView(inv({ status: "Failed", summary: "ChatGPT response failed (error)." })).title).toBe(
      "ChatGPT didn't answer",
    );
    expect(failureView(inv({ status: "Failed", summary: "Select a model before running AI" })).action?.kind).toBe(
      "setup",
    );
    expect(failureView(inv({ status: "Failed", failureKind: "sign_in" })).action).toEqual({
      kind: "setup",
      label: "Reconnect",
    });
    expect(failureView(inv({ status: "Interrupted", failureKind: "stopped" })).action?.label).toBe("Resume");
    expect(failureView(inv({ status: "Failed", failureKind: "usage_limit" })).action).toBeNull();
  });
});

describe("runTimeline", () => {
  const at = (h: number, m = 0) => new Date(Date.UTC(2026, 9, 5, h, m)).toISOString();
  it("folds consecutive quiet and unfinished checks into one row and groups by day", () => {
    const items = [
      inv({ id: "q1", at: at(5), verdict: "no_change" }),
      inv({ id: "f1", at: at(6), status: "Failed", verdict: null }),
      inv({ id: "q2", at: at(7), verdict: "no_change" }),
      inv({ id: "p1", at: at(8), verdict: "problem" }),
      inv({ id: "q3", at: at(9), verdict: "no_change" }),
    ];
    const checks = quietChecks([{ at: at(9, 30), kind: "check", message: "Checked 09:00–10:30: nothing new." }]);
    const days = runTimeline(items, checks, { now: Date.parse(at(12)) });
    expect(days).toHaveLength(1);
    expect(days[0].label).toBe("Today");
    const kinds = days[0].items.map((i) => i.kind);
    expect(kinds).toEqual(["group", "run", "group"]);
    const [newest, , oldest] = days[0].items;
    expect(newest.kind === "group" && newest.label).toBe("2 quick checks 10:00–10:30");
    expect(oldest.kind === "group" && oldest.label).toBe("2 quick checks 06:00–08:00 · 1 didn't finish");
  });
  it("keeps the newest check on its own when it didn't finish, and hides repeats", () => {
    const days = runTimeline([
      inv({ id: "q1", at: at(5), verdict: "no_change" }),
      inv({ id: "q2", at: at(6), verdict: "no_change" }),
      inv({ id: "r", at: at(7), repeatOf: "x" }),
      inv({ id: "f", at: at(8), status: "Failed", verdict: null }),
    ]);
    expect(days[0].items.map((i) => (i.kind === "run" ? i.investigation.id : i.kind))).toEqual(["f", "group"]);
    // A quick check since then doesn't fold it away.
    const later = runTimeline(
      [inv({ id: "q", at: at(6), verdict: "no_change" }), inv({ id: "f", at: at(8), status: "Failed", verdict: null })],
      [{ at: at(9), text: "Checked 08:00–09:00: nothing new." }],
    );
    expect(later[0].items.map((i) => (i.kind === "run" ? i.investigation.id : i.kind))).toEqual(["check", "f", "q"]);
  });
  it("starts a new day heading at local midnight", () => {
    const days = runTimeline(
      [inv({ id: "a", at: "2026-10-04T22:30:00Z" }), inv({ id: "b", at: "2026-10-04T23:30:00Z" })],
      [],
      { now: Date.parse("2026-10-05T09:00:00Z") },
    );
    expect(days.map((d) => d.label)).toEqual(["Today", "Yesterday"]);
  });
});

describe("needsYou and closedItems", () => {
  const state = {
    proposals: [
      proposal({ id: "cheap", estimatedMonthlySavingGbp: null }),
      proposal({ id: "dear", savingLowGbpPerMonth: 2, savingHighGbpPerMonth: 4 }),
      proposal({ id: "old", status: "Denied", decidedAt: "2026-10-05T09:00:00Z" }),
    ],
    investigations: [
      inv({
        id: "i1",
        at: "2026-10-05T08:00:00Z",
        impactPence: 120,
        nextSteps: [
          {
            id: "s1",
            title: "Check the car meter",
            rationale: "",
            suggestedAction: "",
            verification: "",
            uncertainty: "",
            evidenceReferences: [],
          },
          {
            id: "s2",
            title: "Old to-do",
            status: "closed",
            closedReason: "Dismissed by user",
            rationale: "",
            suggestedAction: "",
            verification: "",
            uncertainty: "",
            evidenceReferences: [],
            thread: [{ at: "2026-10-05T09:00:00Z", role: "ai", text: "Agreed.", verdict: "accept" }],
          },
        ],
      }),
      inv({
        id: "i0",
        at: "2026-10-04T08:00:00Z",
        nextSteps: [
          {
            id: "s0",
            title: "check the  car meter",
            rationale: "",
            suggestedAction: "",
            verification: "",
            uncertainty: "",
            evidenceReferences: [],
          },
        ],
      }),
    ],
    experiments: [
      {
        id: "e1",
        status: "Running",
        reviewAt: "2026-10-05T00:00:00Z",
        startedAt: "2026-09-28T00:00:00Z",
      } as Experiment,
      {
        id: "e2",
        status: "Running",
        reviewAt: "2026-10-09T00:00:00Z",
        startedAt: "2026-10-02T00:00:00Z",
      } as Experiment,
    ],
  };
  it("lists what needs you by money at stake, then suggestions first, once per to-do", () => {
    const items = needsYou(state, Date.parse("2026-10-05T12:00:00Z"));
    expect(items.map((i) => i.key)).toEqual(["p-dear", "t-i1-s1", "p-cheap", "e-e1"]);
  });
  it("keeps a file edit you marked applied out of Needs you, waiting for the next check instead", () => {
    const edit = (id: string, status: "pending" | "applied", snippet: string) => ({
      id,
      status,
      file: "apps.yaml",
      summary: "",
      location: "",
      snippet,
      reason: "",
    });
    const withEdits = {
      ...state,
      investigations: [
        inv({
          id: "f1",
          at: "2026-10-05T07:00:00Z",
          fileChanges: [edit("a", "applied", "x: 1"), edit("b", "pending", "y: 2")],
        }),
      ],
    };
    expect(
      needsYou(withEdits, Date.parse("2026-10-05T12:00:00Z"))
        .filter((i) => i.kind === "file")
        .map((i) => i.key),
    ).toEqual(["f-b"]);
    expect(waitingToConfirm(withEdits).map((i) => i.key)).toEqual(["f-a"]);
  });
  it("says why each closed item closed", () => {
    const closed = closedItems(state);
    expect(closed.map((c) => [c.key, c.status])).toEqual([
      ["p-old", "You declined"],
      ["t-i1-s2", "Closed after your reply"],
    ]);
  });
});

describe("closedStatus", () => {
  it("reads an applied suggestion that was undone as undone, not Applied", () => {
    expect(closedStatus("proposal", "Reverted", [])).toBe("Applied, then undone");
    expect(closedStatus("proposal", "Applied", [])).toBe("Applied");
    const [item] = closedItems({
      proposals: [
        proposal({ status: "Reverted", createdAt: "2026-10-01T10:00:00Z", decidedAt: "2026-10-04T09:00:00Z" }),
      ],
      investigations: [],
    });
    expect(item.status).toBe("Applied, then undone");
    expect(item.at).toBe("2026-10-04T09:00:00Z");
  });
  it("reads a to-do closed with its Done button (no note) as Done, and a written dismissal as dismissed", () => {
    expect(closedStatus("todo", "closed", [], "Dismissed by user", null)).toBe("Done");
    expect(closedStatus("todo", "closed", [], "Dismissed by user", "Done")).toBe("Done");
    expect(closedStatus("todo", "closed", [], "Dismissed by user", "Not relevant here")).toBe("You dismissed this");
  });
});

describe("suggestions", () => {
  const settings = [{ key: "battery_rate_max_scaling", name: "Charge rate scaling", unit: "" } as Setting];
  it("shows a friendly diff", () => {
    expect(changeView({ key: "battery_rate_max_scaling", before: "1.0", after: "0.67" }, settings)).toEqual({
      key: "battery_rate_max_scaling",
      name: "Charge rate scaling",
      before: "100%",
      after: "67%",
    });
    expect(changeView({ key: "combine_charge_slots", before: "off", after: "on" })).toMatchObject({
      name: "Combine charge slots",
      before: "Off",
      after: "On",
    });
  });
  it("says a saving is not estimated without a per-month suffix", () => {
    expect(savingText(proposal({ savingEstimate: "Not estimated: this changes what Predbat assumes." }))).toEqual({
      headline: "Saving: not estimated",
      reason: "This changes what Predbat assumes.",
    });
    expect(savingText(proposal({ savingLowGbpPerMonth: 1, savingHighGbpPerMonth: 3 })).headline).toBe(
      "Saving: about £1.00–£3.00 a month",
    );
  });
  it("names who suggested it and when", () => {
    expect(suggestedBy({ source: "ChatGpt", createdAt: "2026-10-05T02:10:00Z" })).toMatch(
      /^Suggested by ChatGPT · .*03:10$/,
    );
  });
});

describe("trials", () => {
  const trial = (over: Partial<Experiment>) =>
    ({
      status: "Running",
      startedAt: "2026-10-01T00:00:00Z",
      reviewAt: "2026-10-08T00:00:00Z",
      confounders: [],
      ...over,
    }) as Experiment;
  it("shows one status line with progress", () => {
    expect(trialView(trial({}), Date.parse("2026-10-03T12:00:00Z"))).toMatchObject({
      status: "Too early to tell: 2 of 7 days",
      progress: 2 / 7,
      closed: false,
    });
    expect(trialView(trial({ status: "Kept" })).status).toBe("Kept");
  });
  it("heads a trial with the same friendly diff its suggestion showed", () => {
    const settings = [{ key: "load_scaling", name: "House load scaling", unit: "" } as Setting];
    const revisions = [{ id: 4, changes: [{ key: "load_scaling", before: "1.08", after: "1.0" }] }];
    expect(
      trialHeading({ title: "Suggestion applied: House load scaling 1.08 → 1.00", revisionId: 4 }, revisions, settings),
    ).toBe("House load scaling 108% → 100%");
    // Long values stay readable in a heading: a version keeps its number, other text is cut at a word.
    const long = [
      {
        id: 7,
        changes: [{ key: "version", before: "v9.3.4 IOG started-dispatch fix, solar band", after: "v9.3.5 Bug fixes" }],
      },
    ];
    expect(trialHeading({ title: "x", revisionId: 7 }, long, [])).toBe("Version v9.3.4 → v9.3.5 Bug fixes");
    // No setting changes on the revision (a file edit, or the revision is gone): the server title without its prefix.
    expect(trialHeading({ title: "Changed in Predbat: Export rate 5p", revisionId: 9 }, revisions, settings)).toBe(
      "Export rate 5p",
    );
  });
  it("never says you closed a trial that Joule or Predbat closed", () => {
    expect(trialDecisionText({ decision: "close", notes: "Not a tunable change" })).toEqual({
      text: "Closed by Joule: not a setting you tune",
      byYou: false,
    });
    expect(
      trialDecisionText({
        decision: "close",
        notes: "Changed back in Predbat within a day, so it isn't tracked as a trial.",
      }),
    ).toMatchObject({ byYou: false, text: "Closed: changed back in Predbat within a day" });
    expect(trialDecisionText({ decision: "automatic revert", notes: "Previous values restored." }).byYou).toBe(false);
    expect(trialDecisionText({ decision: "close", notes: "" })).toEqual({ text: "You closed the trial", byYou: true });
    expect(trialDecisionText({ decision: "keep", notes: "" }).text).toBe("You kept it");
  });
  it("turns evaluator confounders into short chips", () => {
    expect(
      confounderChips([
        "pv context has insufficient coverage.",
        "ev context has insufficient coverage.",
        "Configuration changed during the matched baseline window.",
      ]),
    ).toEqual(["Solar readings incomplete", "Car charging readings incomplete", "Another setting changed"]);
  });
});

describe("AI usage", () => {
  const now = Date.parse("2026-10-05T12:00:00Z");
  const runs = [
    inv({ id: "a", at: "2026-10-05T09:00:00Z", verdict: "problem" }),
    inv({ id: "b", at: "2026-10-05T10:00:00Z", verdict: "no_change" }),
    inv({ id: "c", at: "2026-10-05T11:00:00Z", status: "Failed", verdict: null }),
    inv({ id: "old", at: "2026-10-03T11:00:00Z", verdict: "problem" }),
  ];
  const usage = [
    {
      at: "2026-10-05T09:04:00Z",
      provider: "ChatGpt",
      model: "m",
      inputTokens: 1000,
      outputTokens: 10,
      estimatedUsd: null,
      status: "Completed",
    },
    {
      at: "2026-10-05T10:03:00Z",
      provider: "ChatGpt",
      model: "m",
      inputTokens: 500,
      outputTokens: 5,
      estimatedUsd: null,
      status: "Completed",
    },
    {
      at: "2026-10-05T11:02:00Z",
      provider: "ChatGpt",
      model: "m",
      inputTokens: 200,
      outputTokens: 0,
      estimatedUsd: null,
      status: "Failed",
    },
    {
      at: "2026-10-03T11:05:00Z",
      provider: "ChatGpt",
      model: "m",
      inputTokens: 300,
      outputTokens: 3,
      estimatedUsd: null,
      status: "Completed",
    },
  ];
  it("splits each day's tokens by what the check found", () => {
    const bars = usageByDay(usage, runs, { days: 3, now });
    expect(bars.map((b) => b.date)).toEqual(["2026-10-03", "2026-10-04", "2026-10-05"]);
    expect(bars[2].tokens).toEqual({ found: 1010, quiet: 505, unfinished: 200 });
    expect(bars[0].tokens.found).toBe(303);
    expect(bars[1].total).toBe(0);
  });
  it("sums the last 24 hours", () => {
    expect(
      last24h(
        {
          investigations: runs,
          usage,
          activities: [{ at: "2026-10-05T11:30:00Z", kind: "check", message: "Checked" }],
        },
        now,
      ),
    ).toEqual({ checks: 3, found: 1, unfinished: 1, quick: 1, tokens: 1715 });
  });
  it("finds when Predbat's tools were last used", () => {
    expect(
      mcpLastUsed([
        inv({ at: "2026-10-05T09:00:00Z", steps: ['mcp: {"name":"get_log"} (retrieved; evidence tool-1)'] }),
        inv({ at: "2026-10-05T10:00:00Z", steps: ["plan_vs_actual: x/y (retrieved; t)"] }),
      ]),
    ).toBe("2026-10-05T09:00:00Z");
  });
});

describe("steps", () => {
  it("translates raw tool calls into words", () => {
    expect(
      stepLabel(
        'mcp: {"name":"get_log","arguments":{"filter":"all","search":"Warn","start":"2026-10-05 14:39:00","end":"2026-10-05 15:40:00"}} (retrieved; evidence tool-1)',
      ).label,
    ).toBe("Read Predbat's log for “Warn”, 14:39–15:40");
    expect(
      stepLabel(
        "plan_vs_actual: 2026-10-05T04:00:00.0000000+01:00/2026-10-05T08:18:00.0000000+01:00 (retrieved; evidence tool-9)",
      ).label,
    ).toMatch(/^Compared the plan with your meters, .*04:00–08:18$/);
    expect(stepLabel("schema: MCP input schema: get_apps (retrieved; x)").minor).toBe(true);
  });
  it("prefers the server's labelled steps", () => {
    expect(
      stepsOf({ steps: ["raw"], stepDetails: [{ at: "", kind: "mcp", label: "Read Predbat's log", detail: "raw" }] }),
    ).toEqual([{ label: "Read Predbat's log", detail: "raw", kind: "mcp", minor: false }]);
  });
});

describe("wallTimeToIso", () => {
  it("reads a date field as the household's wall-clock time, not the browser's", () => {
    expect(wallTimeToIso("2026-10-05T10:20", "Europe/London")).toBe("2026-10-05T09:20:00.000Z");
    expect(wallTimeToIso("2026-12-05T10:20", "Europe/London")).toBe("2026-12-05T10:20:00.000Z");
    expect(wallTimeToIso("2026-10-05T10:20", "America/New_York")).toBe("2026-10-05T14:20:00.000Z");
    expect(wallTimeToIso("nonsense", "Europe/London")).toBeNull();
  });
});

describe("optionDetail", () => {
  it("drops the label a description repeats", () => {
    expect(optionDetail("Limit battery wear", "Limit battery wear: prefer fewer cycles.")).toBe("Prefer fewer cycles.");
    expect(optionDetail("Save the most money", "Lowest net cost (default): every penny counts.")).toBe(
      "Every penny counts.",
    );
    expect(optionDetail("Save the most money", "Lowest net cost.")).toBe("Lowest net cost.");
  });
});

describe("usage by day and pairing", () => {
  it("steps back by calendar day, so the day the clocks change is never skipped", () => {
    const bars = usageByDay([], [], { days: 3, now: Date.parse("2026-03-29T23:30:00Z"), timeZone: "Europe/London" });
    expect(bars.map((b) => b.date)).toEqual(["2026-03-28", "2026-03-29", "2026-03-30"]);
  });
  it("pairs a usage record with the check that finished just after it, even when another started later", () => {
    const long = inv({ id: "long", at: "2026-10-05T10:00:00Z", finishedAt: "2026-10-05T10:20:00Z" });
    const short = inv({ id: "short", at: "2026-10-05T10:05:00Z", finishedAt: "2026-10-05T10:08:00Z" });
    expect(checkForUsage({ at: "2026-10-05T10:19:30Z" }, [long, short])?.id).toBe("long");
    expect(checkForUsage({ at: "2026-10-05T10:07:50Z" }, [long, short])?.id).toBe("short");
    // Older records without finishedAt: the latest check that started within 30 minutes before.
    const old = inv({ id: "old", at: "2026-10-04T10:00:00Z" });
    expect(checkForUsage({ at: "2026-10-04T10:12:00Z" }, [old])?.id).toBe("old");
  });
});

describe("AI checks page", () => {
  const usage = (at: string, input: number, over: Partial<Usage> = {}): Usage =>
    ({
      at,
      provider: "ChatGpt",
      model: "gpt",
      inputTokens: input,
      outputTokens: 10,
      status: "Completed",
      ...over,
    }) as Usage;
  it("counts today from local midnight, matching the daily cap", () => {
    const now = Date.parse("2026-10-05T09:00:00Z"); // 10:00 in London
    const s = todaySummary(
      {
        investigations: [
          inv({ id: "a", at: "2026-10-05T08:00:00Z" }),
          inv({ id: "b", at: "2026-10-04T22:30:00Z" }), // 23:30 yesterday, inside the last 24 hours
        ],
        usage: [usage("2026-10-05T08:05:00Z", 100), usage("2026-10-04T22:35:00Z", 900)],
        activities: [],
      },
      { now, timeZone: "Europe/London" },
    );
    expect(s.checks).toBe(1);
    expect(s.tokens).toBe(110);
  });
  it("groups a check's tries into one row with summed tokens, timed by the check's start", () => {
    const check = inv({ id: "x", at: "2026-10-05T19:00:00Z", finishedAt: "2026-10-05T19:10:30Z" });
    const groups = groupUsageByCheck(
      [usage("2026-10-05T19:08:00Z", 63600), usage("2026-10-05T19:10:00Z", 38400), usage("2026-10-01T10:00:00Z", 5)],
      [check],
    );
    expect(groups).toHaveLength(2);
    expect(groups[0].check?.id).toBe("x");
    expect(groups[0].records).toHaveLength(2);
    expect(groups[0].inputTokens).toBe(102000);
    expect(groups[0].at).toBe("2026-10-05T19:00:00Z");
    expect(groups[1].check).toBeNull();
  });
});
