/**
 * The Insights model: what needs you, the run timeline, plain failure copy, friendly setting diffs and step labels.
 * Pure functions over the server state, so the page components stay simple and every rule here has a unit test.
 */
import type {
  ConfigFileChange,
  Experiment,
  Investigation,
  InvestigationNextStep,
  InvestigationVerdict,
  Proposal,
  ReplyMessage,
  Setting,
  State,
  Usage,
} from "../types";
import type { Tone } from "../components/ui/Chip";
import { gbp } from "./format";
import { clock, dayLabel, dayTime, householdTimeZone, localDate, range } from "./time";
import { providerLabel } from "./labels";

// ---------------------------------------------------------------- verdicts

/** What a check's badge shows: its verdict, or its status when it didn't finish (those have no verdict). */
export type VerdictView = InvestigationVerdict | "didnt_finish" | "running";

export const verdictLabels: Record<VerdictView, string> = {
  problem: "Problem",
  opportunity: "Opportunity",
  no_change: "Nothing new",
  finding: "Finding",
  didnt_finish: "Didn't finish",
  running: "Checking",
};

/** A failed or interrupted check is never a household problem; a missing verdict on an older record is a neutral finding. */
export function investigationVerdict(
  investigation: Pick<Investigation, "verdict"> & { status?: string | null },
): VerdictView {
  if (investigation.status === "Running") return "running";
  if (investigation.status === "Failed" || investigation.status === "Interrupted") return "didnt_finish";
  return investigation.verdict && investigation.verdict in verdictLabels
    ? (investigation.verdict as InvestigationVerdict)
    : "finding";
}

export interface VerdictLook {
  label: string;
  tone: Tone;
}
/** A problem the check itself says has already cleared up ("the gap is now fixed", "resolved since"). */
const RESOLVED = /\b(resolved|now fixed)\b/i;
/**
 * Problem is amber, an opportunity green, a minor problem (under 5p either way) neutral, a problem the check says is
 * already resolved neutral "Resolved"; nothing new and didn't finish are grey.
 */
export function verdictLook(
  i: Pick<Investigation, "verdict" | "status" | "severity"> & Partial<Pick<Investigation, "headline" | "plain">>,
): VerdictLook {
  const v = investigationVerdict(i);
  if (v === "problem" && (RESOLVED.test(i.headline ?? "") || RESOLVED.test(i.plain ?? "")))
    return { label: "Resolved", tone: "neutral" };
  if (v === "problem" && i.severity === "info") return { label: "Minor", tone: "neutral" };
  const tone: Tone =
    v === "problem"
      ? "warn"
      : v === "opportunity"
        ? "success"
        : v === "running"
          ? "info"
          : v === "finding"
            ? "info"
            : "neutral";
  return { label: verdictLabels[v], tone };
}

export const isUnfinished = (i: Pick<Investigation, "status">) => i.status === "Failed" || i.status === "Interrupted";
export const isQuiet = (i: Pick<Investigation, "verdict" | "status">) => investigationVerdict(i) === "no_change";

/** The one line a list row shows. */
export function headlineOf(i: Investigation): string {
  if (isUnfinished(i)) return failureView(i).title;
  if (isQuiet(i)) return "Checked, nothing new";
  return (i.headline || i.title || "").trim();
}

/** A short plain summary for a row (the server's `plain`, or the start of the summary). */
export function plainOf(i: Investigation, limit = 180): string {
  const text = (i.plain || i.summary || "").replace(/\s+/g, " ").trim();
  return shorten(text, limit);
}

export function shorten(text: string, limit: number) {
  if (text.length <= limit) return text;
  const cut = text.slice(0, limit);
  const space = cut.lastIndexOf(" ");
  return `${(space > limit * 0.6 ? cut.slice(0, space) : cut).replace(/[\s,;:.–—-]+$/, "")}…`;
}

/**
 * What the money at stake reads like on a chip, from the server's impactPence (positive = cost more than planned).
 * Always against the plan, never "saved": a figure under plan isn't money the household saved, and the check's own
 * headline carries the judgement. Neutral either way, so the chip never argues with the verdict's colour.
 */
export function impactChip(pence: number | null | undefined): { label: string; tone: Tone } | null {
  if (pence == null || !Number.isFinite(pence) || Math.abs(pence) < 1) return null;
  const amount = Math.abs(pence) >= 100 ? gbp(Math.abs(pence) / 100) : `${Math.round(Math.abs(pence))}p`;
  return pence > 0
    ? { label: `${amount} over plan`, tone: "neutral" }
    : { label: `${amount} under plan`, tone: "neutral" };
}

/** A headline that already names a loss or cost in money ("lost about 67p", "cost £1.20 more"). */
const MONEY_LOSS = /\b(lost|cost)\b.*(\d+p|£)/i;
/** A headline that already names a saving in money ("saved about 40p"). */
const MONEY_GAIN = /\b(saved|saving|gained|earned)\b.*(\d+p|£)/i;
/**
 * A check's money chip, left out when its headline already states a money figure with the opposite sign: "lost about
 * 67p" beside "£1.07 under plan" reads as a contradiction, and the headline is the sentence people trust.
 */
export function impactChipFor(
  i: Pick<Investigation, "impactPence"> & Partial<Pick<Investigation, "headline" | "title">>,
): { label: string; tone: Tone } | null {
  const chip = impactChip(i.impactPence);
  if (!chip) return null;
  const text = `${i.headline ?? ""} ${i.title ?? ""}`;
  const pence = i.impactPence ?? 0;
  if (pence < 0 && MONEY_LOSS.test(text)) return null;
  if (pence > 0 && MONEY_GAIN.test(text)) return null;
  return chip;
}

/** "Seen 3 times" for a finding a later check found again. */
export function repeatText(i: Investigation): string | null {
  if (!i.occurrences || i.occurrences < 2) return null;
  return `Seen ${i.occurrences} times${i.lastSeenAt ? ` · last ${dayTime(i.lastSeenAt)}` : ""}`;
}

/** How the check started, in words: never the scheduled prompt itself. */
export function requestLabel(i: Investigation): string {
  const r = i.request ?? { question: null, from: null, to: null };
  if (r.resumeOf) return "Picked up a check that didn't finish";
  if (r.scheduled) {
    if (r.label) return r.label;
    return r.trigger ? `Automatic check (${r.trigger})` : "Automatic check";
  }
  if (r.question) return `You asked: “${r.question}”`;
  return "Check";
}
export const isUserQuestion = (i: Investigation) => !i.request?.scheduled && !!i.request?.question;

// ---------------------------------------------------------------- failures

export interface FailureView {
  /** "ChatGPT didn't answer" */
  title: string;
  /** What happened and that nothing changed, in plain words. */
  message: string;
  /** "Joule tries again at 11:28", when there is a next automatic try in the future. */
  nextTry: string | null;
  /** resume: POST /investigations/{id}/resume. setup: open AI settings. */
  action: { kind: "resume" | "setup"; label: string } | null;
  technical: { label: string; value: string }[];
}

const NOTHING = "Nothing in Predbat was changed.";

/** Legacy failed records have no failureKind; their summary still says what went wrong. */
function guessKind(i: Investigation): string {
  if (i.failureKind) return i.failureKind;
  const text = `${i.summary} ${i.plain ?? ""}`.toLowerCase();
  if (i.status === "Interrupted") return /restart/.test(text) ? "restart" : "stopped";
  if (/usage limit|plan's limit|quota/.test(text)) return "usage_limit";
  if (/sign.?in|reconnect|unauthori[sz]ed|401/.test(text)) return "sign_in";
  if (/timed out|timeout|took too long/.test(text)) return "timeout";
  if (/rate limit|429|too many requests/.test(text)) return "rate_limited";
  if (/select a model|configure|api key|not set up/.test(text)) return "setup";
  if (/validat|couldn.t be used|invalid|cited evidence|bounded input budget|too many or invalid/.test(text))
    return "invalid_answer";
  if (/response failed|didn.t answer|httpioexception|server|busy|error/.test(text)) return "provider_busy";
  return "unknown";
}

/** A check that didn't finish, explained plainly with what to do next. The raw server text stays in technical details. */
export function failureView(i: Investigation, now: Date | number = Date.now()): FailureView {
  const who = providerLabel(i.provider) === "Demo" ? "The demo AI" : providerLabel(i.provider);
  const kind = guessKind(i);
  const retry = { kind: "resume" as const, label: "Try again" };
  const copy: Record<string, Omit<FailureView, "nextTry" | "technical">> = {
    provider_busy: {
      title: `${who} didn't answer`,
      message: `The AI service was busy or didn't reply in time. ${NOTHING}`,
      action: retry,
    },
    timeout: {
      title: `${who} took too long`,
      message: `One step took longer than Joule waits. ${NOTHING}`,
      action: retry,
    },
    rate_limited: {
      title: `${who} asked Joule to slow down`,
      message: `Too many requests in a short time. ${NOTHING}`,
      action: retry,
    },
    usage_limit: {
      title: `Your ${who} plan's limit is used up`,
      message: `Checks pause until the limit resets. ${NOTHING}`,
      action: null,
    },
    sign_in: {
      title: `${who} needs signing in again`,
      message: `Reconnect ${who} in Setup › AI checks. ${NOTHING}`,
      action: { kind: "setup", label: "Reconnect" },
    },
    rejected: {
      title: `${who} turned the request down`,
      message: `It didn't say why in a way Joule can fix. ${NOTHING}`,
      action: retry,
    },
    incomplete: {
      title: "The AI's answer was cut off",
      message: `It ran out of room before finishing. ${NOTHING}`,
      action: retry,
    },
    content_filter: {
      title: "The AI's safety filter stopped the answer",
      message: `This sometimes happens with log text. ${NOTHING}`,
      action: retry,
    },
    invalid_answer: {
      title: "The AI's answer couldn't be used",
      message: `Joule checks every answer against what it read. This one didn't pass, so it was set aside. ${NOTHING}`,
      action: retry,
    },
    setup: {
      title: "The AI isn't set up yet",
      message: `Choose a provider and model in Setup › AI checks. ${NOTHING}`,
      action: { kind: "setup", label: "Open AI settings" },
    },
    stopped: {
      title: "You stopped this check",
      message: "What it found so far is kept. You can pick up where it stopped.",
      action: { kind: "resume", label: "Resume" },
    },
    restart: {
      title: "Joule restarted during this check",
      message: "What it found so far is kept, and it picks up where it stopped.",
      action: { kind: "resume", label: "Resume" },
    },
    unknown: { title: "The check didn't finish", message: NOTHING, action: retry },
  };
  const base = copy[kind] ?? copy.unknown;
  const next = i.nextTryAt && Date.parse(i.nextTryAt) > new Date(now).getTime() ? i.nextTryAt : null;
  const technical = [
    { label: "What the server said", value: (i.summary || i.plain || "").trim() },
    { label: "Kind", value: kind.replace(/_/g, " ") },
    { label: "Provider code", value: i.providerCode ?? "" },
    { label: "Reference", value: i.providerReference ?? "" },
    { label: "Tries", value: i.attempts && i.attempts > 1 ? String(i.attempts) : "" },
  ].filter((t) => t.value);
  return { ...base, nextTry: next ? `Joule tries again at ${clock(next)}` : null, technical };
}

// ---------------------------------------------------------------- run timeline

/** A quiet check the scheduler made without the AI ("Checked 15:40–16:41: nothing new…"), from the activity log. */
export interface QuietCheck {
  at: string;
  text: string;
}
export function quietChecks(activities: State["activities"] | undefined): QuietCheck[] {
  return (activities ?? []).filter((a) => a.kind === "check").map((a) => ({ at: a.at, text: a.message }));
}

export type CalmEntry = { kind: "run"; investigation: Investigation } | { kind: "check"; check: QuietCheck };
export type TimelineItem =
  | { kind: "run"; key: string; investigation: Investigation }
  | { kind: "check"; key: string; check: QuietCheck }
  | {
      kind: "group";
      key: string;
      entries: CalmEntry[];
      quiet: number;
      unfinished: number;
      from: string;
      to: string;
      /** "6 quick checks 20:52–09:28 · 2 didn't finish" */
      label: string;
    };
export interface TimelineDay {
  date: string;
  label: string;
  items: TimelineItem[];
}

const entryAt = (e: CalmEntry) => (e.kind === "run" ? e.investigation.at : e.check.at);
const isCalm = (i: Investigation) => isQuiet(i) || isUnfinished(i);

function groupLabel(entries: CalmEntry[], timeZone?: string) {
  const unfinished = entries.filter((e) => e.kind === "run" && isUnfinished(e.investigation)).length;
  const quiet = entries.length - unfinished;
  const times = entries.map(entryAt).sort();
  const a = times[0],
    b = times[times.length - 1];
  // Within a day the clock times say it all ("20:52–09:28", as overnight groups read); longer stretches name the days.
  const span =
    Date.parse(b) - Date.parse(a) < 86400000
      ? a === b
        ? clock(a, { timeZone })
        : `${clock(a, { timeZone })}–${clock(b, { timeZone })}`
      : range(a, b, { timeZone });
  const head = quiet ? `${quiet} quick ${quiet === 1 ? "check" : "checks"}` : `${unfinished} checks didn't finish`;
  const tail = quiet && unfinished ? ` · ${unfinished} didn't finish` : "";
  return { quiet, unfinished, label: `${head} ${span}${tail}` };
}

/**
 * Newest first, grouped by local day. Consecutive quiet checks (with or without the AI) and checks that didn't finish fold
 * into one row; the newest check stays on its own if it didn't finish, so its "Try again" is in sight. Repeats of an earlier
 * finding (repeatOf) are left out: the original carries the count. Running checks are shown separately.
 */
export function runTimeline(
  investigations: Investigation[],
  checks: QuietCheck[] = [],
  options: { timeZone?: string; now?: Date | number } = {},
): TimelineDay[] {
  const runs = investigations.filter((i) => i.status !== "Running" && !i.repeatOf);
  const entries: CalmEntry[] = [
    ...runs.map((investigation) => ({ kind: "run" as const, investigation })),
    ...checks.map((check) => ({ kind: "check" as const, check })),
  ].sort((a, b) => Date.parse(entryAt(b)) - Date.parse(entryAt(a)));

  const items: TimelineItem[] = [];
  let calm: CalmEntry[] = [];
  // The newest AI check stays on its own when it didn't finish (quick checks since then don't hide its Try again).
  const newestRun = entries.find((e) => e.kind === "run");
  const flush = () => {
    if (calm.length >= 2) {
      const first = calm[calm.length - 1],
        last = calm[0];
      const g = groupLabel(calm, options.timeZone);
      items.push({
        kind: "group",
        key: `group-${entryAt(last)}`,
        entries: calm,
        quiet: g.quiet,
        unfinished: g.unfinished,
        from: entryAt(first),
        to: entryAt(last),
        label: g.label,
      });
    } else if (calm.length === 1) items.push(single(calm[0]));
    calm = [];
  };
  entries.forEach((e) => {
    const calmEntry = e.kind === "check" || isCalm(e.investigation);
    const newestUnfinished = e === newestRun && e.kind === "run" && isUnfinished(e.investigation);
    if (calmEntry && !newestUnfinished) {
      calm.push(e);
      return;
    }
    flush();
    items.push(single(e));
  });
  flush();

  const days: TimelineDay[] = [];
  for (const item of items) {
    const at = item.kind === "group" ? item.to : item.kind === "run" ? item.investigation.at : item.check.at;
    const date = localDate(at, { timeZone: options.timeZone });
    let day = days.at(-1);
    if (!day || day.date !== date) {
      day = { date, label: dayLabel(at, { timeZone: options.timeZone, relative: true, now: options.now }), items: [] };
      days.push(day);
    }
    day.items.push(item);
  }
  return days;
}
function single(e: CalmEntry): TimelineItem {
  return e.kind === "run"
    ? { kind: "run", key: e.investigation.id, investigation: e.investigation }
    : { kind: "check", key: `check-${e.check.at}`, check: e.check };
}

// ---------------------------------------------------------------- what needs you

export const isOpenNextStep = (step: InvestigationNextStep) => (step.status ?? "open") !== "closed";
export const isOpenFileChange = (change: ConfigFileChange) =>
  change.status === "pending" || change.status === "applied";
const CLOSED_TRIALS = ["kept", "closed", "rolled back", "reverted", "undone"];
export const isOpenTrial = (e: Experiment) => !CLOSED_TRIALS.includes(e.status.toLowerCase());

export type InboxItem =
  | { kind: "proposal"; key: string; at: string; impact: number; proposal: Proposal }
  | { kind: "file"; key: string; at: string; impact: number; investigation: Investigation; change: ConfigFileChange }
  | {
      kind: "todo";
      key: string;
      at: string;
      impact: number;
      investigation: Investigation;
      step: InvestigationNextStep;
      index: number;
    }
  | { kind: "trial"; key: string; at: string; impact: number; experiment: Experiment };

const KIND_ORDER: Record<InboxItem["kind"], number> = { proposal: 0, file: 1, todo: 2, trial: 3 };
const fold = (text: string) => text.trim().replace(/\s+/g, " ").toLowerCase();

/** Pence a month a suggestion might save, for sorting (the larger end of any range). */
export function proposalImpact(p: Proposal) {
  const pounds = Math.max(
    Math.abs(p.savingHighGbpPerMonth ?? 0),
    Math.abs(p.savingLowGbpPerMonth ?? 0),
    Math.abs(p.estimatedMonthlySavingGbp ?? 0),
  );
  return pounds * 100;
}

/**
 * Everything waiting for you: setting changes to approve, file edits and to-dos from checks, and trials due a decision.
 * Sorted by the money at stake, then setting changes first, then newest. A to-do or file edit repeated by later checks
 * appears once (the newest).
 */
export function needsYou(
  state: Pick<State, "proposals" | "investigations" | "experiments">,
  now: Date | number = Date.now(),
) {
  const items: InboxItem[] = [];
  for (const p of state.proposals)
    if (p.status === "Pending")
      items.push({ kind: "proposal", key: `p-${p.id}`, at: p.createdAt, impact: proposalImpact(p), proposal: p });
  const seen = new Set<string>();
  const newestFirst = [...state.investigations].sort((a, b) => Date.parse(b.at) - Date.parse(a.at));
  for (const investigation of newestFirst) {
    const impact = Math.abs(investigation.impactPence ?? 0);
    (investigation.fileChanges ?? []).forEach((change) => {
      const key = `f|${fold(change.file)}|${fold(change.snippet)}`;
      if (!isOpenFileChange(change) || seen.has(key)) return;
      seen.add(key);
      // An edit you've already marked applied isn't waiting for you: it waits for the next check to confirm it.
      if (change.status === "applied") return;
      items.push({ kind: "file", key: `f-${change.id}`, at: investigation.at, impact, investigation, change });
    });
    (investigation.nextSteps ?? []).forEach((step, index) => {
      const key = `t|${fold(step.title)}`;
      if (!isOpenNextStep(step) || seen.has(key)) return;
      seen.add(key);
      items.push({
        kind: "todo",
        key: `t-${investigation.id}-${step.id ?? index}`,
        at: investigation.at,
        impact,
        investigation,
        step,
        index,
      });
    });
  }
  const t = new Date(now).getTime();
  for (const e of state.experiments)
    if (isOpenTrial(e) && Date.parse(e.reviewAt) <= t)
      items.push({ kind: "trial", key: `e-${e.id}`, at: e.reviewAt, impact: 0, experiment: e });
  return items.sort(
    (a, b) => b.impact - a.impact || KIND_ORDER[a.kind] - KIND_ORDER[b.kind] || Date.parse(b.at) - Date.parse(a.at),
  );
}

/** File edits you marked applied that the next check will confirm: shown apart from what needs you, never counted in it. */
export function waitingToConfirm(state: Pick<State, "investigations">) {
  const seen = new Set<string>();
  const items: Extract<InboxItem, { kind: "file" }>[] = [];
  const newestFirst = [...state.investigations].sort((a, b) => Date.parse(b.at) - Date.parse(a.at));
  for (const investigation of newestFirst)
    for (const change of investigation.fileChanges ?? []) {
      const key = `f|${fold(change.file)}|${fold(change.snippet)}`;
      if (!isOpenFileChange(change) || seen.has(key)) continue;
      seen.add(key);
      if (change.status === "applied")
        items.push({
          kind: "file",
          key: `f-${change.id}`,
          at: change.appliedAt ?? investigation.at,
          impact: 0,
          investigation,
          change,
        });
    }
  return items;
}

export type ClosedItem =
  | { kind: "proposal"; key: string; at: string; proposal: Proposal; status: string; lastAi: ReplyMessage | null }
  | {
      kind: "finding";
      key: string;
      at: string;
      investigation: Investigation;
      status: string;
      lastAi: ReplyMessage | null;
    }
  | {
      kind: "file";
      key: string;
      at: string;
      investigation: Investigation;
      change: ConfigFileChange;
      status: string;
      lastAi: ReplyMessage | null;
    }
  | {
      kind: "todo";
      key: string;
      at: string;
      investigation: Investigation;
      step: InvestigationNextStep;
      status: string;
      lastAi: ReplyMessage | null;
    };

const lastAi = (thread?: ReplyMessage[]) => [...(thread ?? [])].reverse().find((m) => m.role === "ai") ?? null;
/** True when the AI agreed with the user's reply and closed the item because of it. */
const closedByReply = (thread?: ReplyMessage[]) => lastAi(thread)?.verdict === "accept";

/** Why a suggestion, to-do or file edit closed, in plain words. */
export function closedStatus(
  kind: Exclude<ClosedItem["kind"], "finding">,
  status: string,
  thread: ReplyMessage[] | undefined,
  reason?: string | null,
  note?: string | null,
): string {
  if (/joule's own connection/i.test(reason ?? "")) return "About Joule's own connection, not your system";
  if (closedByReply(thread)) return "Closed after your reply";
  if (/^not needed$/i.test(reason ?? "")) return "Not needed";
  if (/^done by (user|you)$/i.test(reason ?? "")) return "Done";
  if (kind !== "proposal" && /^done\.?$/i.test(note ?? "")) return "Done";
  const s = status.toLowerCase();
  // An older to-do closed by its Done button carries no note (a dismiss you write always has one).
  if (kind === "todo" && !note?.trim() && /^dismissed by user$/i.test(reason ?? "")) return "Done";
  if (/findings dismissed/i.test(reason ?? "")) return "Closed with its findings";
  if (kind === "proposal") {
    if (s === "applied") return "Applied";
    // Applied, then put back (by you from Trials, or by Joule's automatic undo): never left reading "Applied".
    if (s === "reverted" || s === "undone" || s === "rolled back") return "Applied, then undone";
    if (s === "done") return "Done in Predbat by you";
    if (s === "denied") return "You declined";
    if (s === "superseded") return "Replaced by a newer suggestion";
    return status;
  }
  if (s === "verified") return "Checked: it took effect";
  if (/dismissed by (user|you)/i.test(reason ?? "") || s === "dismissed") return "You dismissed this";
  if (/no longer carried forward/i.test(reason ?? "") || s === "retired") return "No longer needed";
  return "Closed";
}

/** True when a check's findings closed: dismissed or not needed by you, resolved, or closed by Joule. */
export const isClosedFinding = (i: Pick<Investigation, "dismissedAt">) => !!i.dismissedAt;

/** How a check's findings closed, in plain words. */
export function findingClosedStatus(i: Pick<Investigation, "closedReason" | "thread">): string {
  switch (i.closedReason) {
    case "not_needed":
      return closedByReply(i.thread) ? "Closed after your reply" : "Not needed";
    case "resolved":
      return "Nothing left to do";
    case "repeat":
      return "Not raised again";
    case "own_traffic":
      return "About Joule's own connection, not your system";
    default:
      return closedByReply(i.thread) ? "Closed after your reply" : "You dismissed this";
  }
}

/** Proposals, to-dos, file edits and findings that are no longer waiting for you, newest first. */
export function closedItems(state: Pick<State, "proposals" | "investigations">): ClosedItem[] {
  const items: ClosedItem[] = [];
  for (const p of state.proposals)
    if (p.status !== "Pending")
      items.push({
        kind: "proposal",
        key: `p-${p.id}`,
        at: p.decidedAt ?? p.createdAt,
        proposal: p,
        status: closedStatus("proposal", p.status, p.thread, p.closedReason),
        lastAi: lastAi(p.thread),
      });
  // Findings you closed (or that closed when nothing was left): a repeat Joule held back is not shown.
  for (const investigation of state.investigations)
    if (investigation.dismissedAt && investigation.closedReason !== "repeat")
      items.push({
        kind: "finding",
        key: `i-${investigation.id}`,
        at: investigation.dismissedAt,
        investigation,
        status: findingClosedStatus(investigation),
        lastAi: lastAi(investigation.thread),
      });
  const seen = new Set<string>();
  for (const investigation of [...state.investigations].sort((a, b) => Date.parse(b.at) - Date.parse(a.at))) {
    for (const change of investigation.fileChanges ?? []) {
      const key = `f|${fold(change.file)}|${fold(change.snippet)}`;
      if (isOpenFileChange(change) || seen.has(key)) continue;
      seen.add(key);
      items.push({
        kind: "file",
        key: `f-${change.id}`,
        at: change.closedAt ?? change.decidedAt ?? investigation.at,
        investigation,
        change,
        status: closedStatus("file", change.status, change.thread, change.closedReason, change.decisionNote),
        lastAi: lastAi(change.thread),
      });
    }
    for (const step of investigation.nextSteps ?? []) {
      const key = `t|${fold(step.title)}`;
      if (isOpenNextStep(step) || seen.has(key)) continue;
      seen.add(key);
      items.push({
        kind: "todo",
        key: `t-${investigation.id}-${step.id ?? step.title}`,
        at: step.closedAt ?? step.decidedAt ?? investigation.at,
        investigation,
        step,
        status: closedStatus("todo", step.status ?? "closed", step.thread, step.closedReason, step.decisionNote),
        lastAi: lastAi(step.thread),
      });
    }
  }
  return items.sort((a, b) => Date.parse(b.at) - Date.parse(a.at));
}

// ---------------------------------------------------------------- suggestions

export interface ChangeView {
  key: string;
  name: string;
  before: string;
  after: string;
}
const looksLikeScaling = (key: string) => /(^|_)scaling(_|$)/.test(key);
const asNumber = (v: string) => (v.trim() === "" ? NaN : Number(v));

/** A value as people read it: on/off → On/Off, scaling factors as percentages, units from the setting. */
export function settingValue(key: string, value: string, setting?: Pick<Setting, "type" | "unit">) {
  if (value == null || value === "") return "Not set";
  if (/^(on|off)$/i.test(value)) return /^on$/i.test(value) ? "On" : "Off";
  const n = asNumber(value);
  if (Number.isFinite(n) && looksLikeScaling(key) && !setting?.unit && n >= 0 && n <= 5)
    return `${Math.round(n * 100)}%`;
  const unit = setting?.unit ?? "";
  if (unit && Number.isFinite(n)) return unit === "%" ? `${value}%` : `${value} ${unit}`;
  return value;
}

/** "Charge rate scaling 100% → 67%", with the key kept for a small mono label. */
export function changeView(
  change: { key: string; before: string; after: string },
  settings: Setting[] = [],
): ChangeView {
  const setting = settings.find((s) => s.key === change.key);
  const name = setting?.name && setting.name !== change.key ? setting.name : humaniseKey(change.key);
  return {
    key: change.key,
    name,
    before: settingValue(change.key, change.before, setting),
    after: settingValue(change.key, change.after, setting),
  };
}
const humaniseKey = (key: string) => {
  const words = key.replace(/[_.]+/g, " ").trim();
  return words ? words[0].toUpperCase() + words.slice(1) : key;
};

/** "Saving: about £2–£3 a month" or "Saving: not estimated" with the server's reason. */
export function savingText(p: Proposal): { headline: string; reason: string | null } {
  const low = p.savingLowGbpPerMonth,
    high = p.savingHighGbpPerMonth;
  if (low != null && high != null && Number.isFinite(low) && Number.isFinite(high))
    return {
      headline:
        low === high
          ? `Saving: about ${gbp(low)} a month`
          : `Saving: about ${gbp(low)}–${gbp(high).replace(/^−?/, "")} a month`,
      reason: p.savingEstimate && !/^not estimated/i.test(p.savingEstimate) ? p.savingEstimate : null,
    };
  if (p.estimatedMonthlySavingGbp != null)
    return { headline: `Saving: about ${gbp(p.estimatedMonthlySavingGbp)} a month`, reason: null };
  const reason = (p.savingEstimate ?? "").replace(/^not estimated[:.]?\s*/i, "").trim();
  return { headline: "Saving: not estimated", reason: reason ? reason[0].toUpperCase() + reason.slice(1) : null };
}

/** "Suggested by ChatGPT". */
export function suggestedByWho(p: Pick<Proposal, "source">) {
  const who = p.source === "Demo" ? "the demo" : p.source === "AI" ? "the AI" : providerLabel(p.source);
  return `Suggested by ${who}`;
}
/** "Suggested by ChatGPT · 5 Oct 03:10". */
export function suggestedBy(p: Pick<Proposal, "source" | "createdAt">) {
  return `${suggestedByWho(p)} · ${dayTime(p.createdAt)}`;
}

/** Stale only when one of the suggestion's own settings moved since it was made (C's staleKeys). */
export const staleKeys = (p: Proposal) => p.staleKeys ?? [];
export const isStale = (p: Proposal) => staleKeys(p).length > 0;

// ---------------------------------------------------------------- trials

export interface TrialView {
  /** "Too early to tell: 2 of 7 days" */
  status: string;
  tone: Tone;
  /** 0..1 progress through the trial window, or null when closed. */
  progress: number | null;
  /** Short plain chips for what makes the comparison less reliable. */
  confounders: string[];
  closed: boolean;
}
const CONFOUNDER_CHIPS: [RegExp, string][] = [
  [/\bpv\b|solar/i, "Solar readings incomplete"],
  [/\bev\b|car/i, "Car charging readings incomplete"],
  [/load changed/i, "Home use changed a lot"],
  [/import tariff/i, "Import prices incomplete"],
  [/export tariff/i, "Export prices incomplete"],
  [/financial|cost/i, "Cost readings incomplete"],
  [/configuration changed|other setting|another setting/i, "Another setting changed"],
  [/weather/i, "Weather changed"],
];
export function confounderChips(confounders: string[]): string[] {
  const chips = new Set<string>();
  for (const c of confounders) {
    const hit = CONFOUNDER_CHIPS.find(([pattern]) => pattern.test(c));
    chips.add(hit ? hit[1] : shorten(c.replace(/\.$/, ""), 40));
  }
  return [...chips];
}

export function trialView(e: Experiment, now: Date | number = Date.now()): TrialView {
  const closed = !isOpenTrial(e);
  const started = Date.parse(e.startedAt),
    review = Date.parse(e.reviewAt),
    t = new Date(now).getTime();
  const totalDays = Math.max(1, Math.round((review - started) / 86400000));
  const elapsed = Math.min(totalDays, Math.max(0, Math.floor((t - started) / 86400000)));
  const confounders = confounderChips(e.confounders ?? []);
  if (closed) {
    const s = e.status.toLowerCase();
    return {
      status: s === "kept" ? "Kept" : s === "rolled back" || s === "reverted" || s === "undone" ? "Undone" : "Closed",
      tone: s === "kept" ? "success" : "neutral",
      progress: null,
      confounders,
      closed,
    };
  }
  const haveFigures = e.currentCostGbpPerDay != null && e.baselineCostGbpPerDay != null;
  if (t >= review)
    return {
      status: haveFigures ? "Ready for your decision" : "Ready for your decision, though the figures are incomplete",
      tone: "info",
      progress: 1,
      confounders,
      closed,
    };
  return {
    status: `Too early to tell: ${elapsed} of ${totalDays} ${totalDays === 1 ? "day" : "days"}`,
    tone: "neutral",
    progress: elapsed / totalDays,
    confounders,
    closed,
  };
}

/** The server's trial title without its "Suggestion applied:" style prefix. */
export const strippedTrialTitle = (title: string) =>
  title.replace(/^(Suggestion applied|Automatic change|Changed in Predbat):\s*/i, "");

/** A value short enough for a heading: a version keeps just its number ("v9.3.4"), long text is cut at a word. */
function headingValue(value: string) {
  const version = /^v?\d+(?:\.\d+)+\b/.exec(value);
  if (version && value.length > 24) return version[0];
  return shorten(value, 28);
}

/**
 * A trial's heading as the same friendly diff its suggestion showed ("House load scaling 108% → 100%"), built from the
 * revision it tracks; the stripped server title when that revision has no setting changes (a file edit, or pruned).
 */
export function trialHeading(
  e: Pick<Experiment, "title" | "revisionId">,
  revisions: { id: number; changes: { key: string; before: string; after: string }[] }[] = [],
  settings: Setting[] = [],
): string {
  const changes = revisions.find((r) => r.id === e.revisionId)?.changes ?? [];
  if (!changes.length) return strippedTrialTitle(e.title);
  const parts = changes.slice(0, 2).map((c) => {
    const v = changeView(c, settings);
    return `${v.name} ${headingValue(v.before)} → ${headingValue(v.after)}`;
  });
  return parts.join(", ") + (changes.length > 2 ? ` and ${changes.length - 2} more` : "");
}

/**
 * Who closed or changed a trial, in words. Joule and Predbat close trials too (a change that isn't a setting you tune, a
 * change put straight back in Predbat, an automatic undo), and those must never read as if you did it.
 */
export function trialDecisionText(d: { decision: string; notes?: string | null }): { text: string; byYou: boolean } {
  const notes = d.notes ?? "";
  if (/^not a tunable change/i.test(notes)) return { text: "Closed by Joule: not a setting you tune", byYou: false };
  if (/^changed back in predbat within a day/i.test(notes))
    return {
      text:
        d.decision === "close"
          ? "Closed: changed back in Predbat within a day"
          : "Stopped: changed back in Predbat within a day",
      byYou: false,
    };
  switch (d.decision) {
    case "automatic revert":
      return { text: "Joule undid it automatically", byYou: false };
    case "keep":
      return { text: "You kept it", byYou: true };
    case "close":
      return { text: "You closed the trial", byYou: true };
    case "extend":
      return { text: "You gave it longer", byYou: true };
    case "revert":
      return { text: "You undid it", byYou: true };
    case "notes":
      return { text: "You added a note", byYou: true };
    default:
      return { text: d.decision, byYou: true };
  }
}

/**
 * A datetime-local value ("2026-10-05T10:20") read as wall-clock time in the household's zone, as an ISO instant.
 * The browser would read it in its own zone; a phone abroad must still mean the home's 10:20.
 */
export function wallTimeToIso(value: string, timeZone?: string): string | null {
  const m = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})/.exec(value);
  if (!m) return null;
  if (!timeZone) return new Date(value).toISOString();
  const target = Date.UTC(+m[1], +m[2] - 1, +m[3], +m[4], +m[5]);
  const f = new Intl.DateTimeFormat("en-GB", {
    timeZone,
    year: "numeric",
    month: "numeric",
    day: "numeric",
    hour: "numeric",
    minute: "numeric",
    hourCycle: "h23",
  });
  const wallOf = (t: number) => {
    const p = Object.fromEntries(f.formatToParts(new Date(t)).map((x) => [x.type, Number(x.value)]));
    return Date.UTC(p.year, p.month - 1, p.day, p.hour, p.minute);
  };
  // Shift by the zone's offset; a second pass settles the hour either side of a clock change.
  let t = target;
  for (let i = 0; i < 2; i++) t += target - wallOf(t);
  return new Date(t).toISOString();
}

/**
 * An option's description without the short title it starts with, which only repeats the option's label:
 * "Limit battery wear: prefer fewer…" → "Prefer fewer…", "Lowest net cost (default): every penny…" → "Every penny…".
 */
export function optionDetail(label: string, description: string) {
  const m = /^([^:.]{2,40}):\s+(.+)$/s.exec(description);
  const rest = m ? m[2] : description;
  return rest ? rest[0].toUpperCase() + rest.slice(1) : rest;
}

// ---------------------------------------------------------------- steps

export interface StepView {
  label: string;
  detail: string;
  kind: string;
  /** Schema look-ups and evidence re-reads are bookkeeping: hidden unless asked for. */
  minor: boolean;
}
const MINOR_KINDS = new Set(["schema", "evidence"]);

function windowText(request: string) {
  const m = /^(\S+?)\/(\S+?)(?:\s|$)/.exec(request);
  if (!m) return "";
  const a = Date.parse(m[1]),
    b = Date.parse(m[2]);
  return Number.isFinite(a) && Number.isFinite(b) ? range(a, b).replace(/^Today /, "") : "";
}

/** A raw step from an older record ("mcp: {\"name\":\"get_log\"…} (retrieved; evidence tool-…)") in words. */
export function stepLabel(raw: string): StepView {
  const text = raw.trim();
  const m = /^([a-z_]+):\s*(.*)$/s.exec(text);
  const kind = m?.[1] ?? "";
  const rest = (m?.[2] ?? text).replace(/\s*\((?:retrieved|failed|not retrieved)[^)]*\)\s*$/, "");
  const failed = /\(failed|not retrieved/.test(text) ? " (not available)" : "";
  const minor = MINOR_KINDS.has(kind);
  const label = (() => {
    switch (kind) {
      case "plan_vs_actual": {
        const w = windowText(rest);
        return `Compared the plan with your meters${w ? `, ${w}` : ""}`;
      }
      case "summary": {
        const w = windowText(rest);
        return `Added up your meters${w ? `, ${w}` : ""}`;
      }
      case "sql":
      case "query":
        return "Looked up stored readings";
      case "configuration":
        return "Read Predbat's settings";
      case "documentation":
        return `Read Predbat's guide${rest ? ` on “${shorten(rest, 50)}”` : ""}`;
      case "snapshots":
        return "Looked at Predbat's earlier plans";
      case "schema":
        return "Checked how to call a Predbat tool";
      case "evidence": {
        const s = /search: (.+)$/.exec(rest);
        return s ? `Re-read earlier results for “${shorten(s[1], 40)}”` : "Re-read earlier results";
      }
      case "mcp": {
        const name = /"name"\s*:\s*"([a-z_]+)"/.exec(rest)?.[1] ?? "";
        const search = /"(?:search|pattern)"\s*:\s*"([^"]{1,60})"/.exec(rest)?.[1];
        const times = [...rest.matchAll(/"(?:start|end)"\s*:\s*"[^"]*?(\d{2}:\d{2})/g)].map((x) => x[1]);
        const when = times.length === 2 ? `, ${times[0]}–${times[1]}` : "";
        const forText = search ? ` for “${search}”` : "";
        if (name === "get_log") return `Read Predbat's log${forText}${when}`;
        if (name === "get_entity_history") return `Read a sensor's history${when}`;
        if (name === "get_apps_config" || name === "get_apps") return "Read Predbat's configuration file";
        if (name === "get_plan") return "Read Predbat's current plan";
        if (name === "get_status") return "Read Predbat's status";
        return name ? `Read Predbat's ${name.replace(/^get_/, "").replace(/_/g, " ")}` : "Asked Predbat for data";
      }
      case "model":
        if (/corrective retry|rejected/.test(rest)) return "Asked the AI to correct its answer";
        return "The AI answered";
      case "provider":
        if (/retry/.test(rest)) return "The AI service was slow, so Joule tried again";
        return "Talked to the AI service";
      case "server":
        if (/declined/.test(rest)) return "Skipped a suggestion you already declined";
        if (/dismissed/.test(rest)) return "Skipped something you already dismissed";
        if (/resumed/.test(rest)) return "Picked up where the last try stopped";
        return "Joule tidied up the answer";
      default:
        return shorten(text, 90);
    }
  })();
  return { label: label + failed, detail: text, kind: kind || "other", minor };
}

/** Steps in words: the server's labelled steps when it recorded them, otherwise the raw steps translated here. */
export function stepsOf(i: Pick<Investigation, "steps" | "stepDetails">): StepView[] {
  if (i.stepDetails?.length)
    return i.stepDetails.map((s) => ({
      label: s.label,
      detail: s.detail,
      kind: s.kind,
      minor: MINOR_KINDS.has(s.kind),
    }));
  return (i.steps ?? []).map(stepLabel);
}

/** The stage a running check is in, for the progress stepper. */
export const STAGES = [
  "Reading your plan and meters",
  "Reading Predbat's log",
  "Thinking it through",
  "Writing it up",
] as const;
export function stageOf(steps: StepView[]): number {
  const kinds = steps.map((s) => s.kind);
  const last = steps.at(-1);
  if (last && last.kind === "model" && /correct|answer/i.test(last.label)) return 3;
  if (kinds.includes("mcp")) return kinds.slice(-1)[0] === "mcp" ? 1 : 2;
  if (kinds.some((k) => ["plan_vs_actual", "summary", "sql", "query", "configuration", "snapshots"].includes(k)))
    return steps.length > 3 ? 2 : 0;
  return 0;
}

/** "1 min 20 s", "45 s". */
export function elapsedText(fromIso: string, now: Date | number = Date.now()) {
  const seconds = Math.max(0, Math.round((new Date(now).getTime() - Date.parse(fromIso)) / 1000));
  if (!Number.isFinite(seconds)) return "";
  if (seconds < 60) return `${seconds} s`;
  const m = Math.floor(seconds / 60),
    s = seconds % 60;
  return s ? `${m} min ${s} s` : `${m} min`;
}

// ---------------------------------------------------------------- friendly names in AI text

export interface NameBook {
  /** entity id or setting key → friendly name */
  names: Map<string, string>;
}
/** Friendly names for entity ids and setting keys: Joule's meter mappings and Predbat's settings. */
export function nameBook(
  settings: Setting[] = [],
  entityMappings: Record<string, string> = {},
  metricName?: (m: string) => string,
) {
  const names = new Map<string, string>();
  for (const s of settings) {
    if (!s.name || s.name === s.key) continue;
    names.set(s.key, s.name);
    if (s.entityId) names.set(s.entityId, s.name);
  }
  for (const [metric, entity] of Object.entries(entityMappings)) {
    if (!entity) continue;
    const label = metricName ? metricName(metric) : metric;
    names.set(entity, `${label} sensor`);
  }
  return { names } satisfies NameBook;
}

// ---------------------------------------------------------------- AI usage

export type RunOutcome = "found" | "quiet" | "unfinished";
export const outcomeLabels: Record<RunOutcome, string> = {
  found: "Found something",
  quiet: "Nothing new",
  unfinished: "Didn't finish",
};

/**
 * The check a usage record paid for. Usage is recorded as the AI call ends, just before the check is saved as finished,
 * so a check whose finishedAt is within a few minutes after the record is an exact match. Older records without
 * finishedAt fall back to the latest check that started at most 30 minutes before.
 */
export function checkForUsage(u: Pick<Usage, "at">, investigations: Investigation[]): Investigation | null {
  const t = Date.parse(u.at);
  let best: Investigation | null = null,
    bestGap = Infinity;
  for (const i of investigations) {
    if (!i.finishedAt) continue;
    const gap = Date.parse(i.finishedAt) - t;
    if (gap >= -60_000 && gap <= 10 * 60_000 && Math.abs(gap) < bestGap) {
      best = i;
      bestGap = Math.abs(gap);
    }
  }
  if (best) return best;
  for (const i of investigations) {
    const start = Date.parse(i.at);
    if (start <= t + 60_000 && start >= t - 30 * 60_000 && (!best || start > Date.parse(best.at))) best = i;
  }
  return best;
}

export function outcomeOf(u: Pick<Usage, "at" | "status">, investigations: Investigation[]): RunOutcome {
  const i = checkForUsage(u, investigations);
  if (i) {
    if (isUnfinished(i)) return "unfinished";
    return isQuiet(i) ? "quiet" : "found";
  }
  return /fail|interrupt/i.test(u.status) ? "unfinished" : "quiet";
}

export interface UsageDayBar {
  date: string;
  label: string;
  tokens: Record<RunOutcome, number>;
  runs: Record<RunOutcome, number>;
  total: number;
}
/** Tokens per local day for the last `days` days (oldest first), split by what each check found. */
export function usageByDay(
  usage: Usage[],
  investigations: Investigation[],
  options: { days?: number; now?: Date | number; timeZone?: string } = {},
): UsageDayBar[] {
  const days = options.days ?? 14;
  const now = new Date(options.now ?? Date.now()).getTime();
  const bars: UsageDayBar[] = [];
  const index = new Map<string, UsageDayBar>();
  // Step back by calendar days (not 24-hour steps, which skip or repeat a day across a clock change), labelling each
  // day from its local noon.
  const [y, m, dd] = localDate(now, { timeZone: options.timeZone }).split("-").map(Number);
  for (let d = days - 1; d >= 0; d--) {
    const date = new Date(Date.UTC(y, m - 1, dd - d)).toISOString().slice(0, 10);
    const at = Date.parse(
      wallTimeToIso(`${date}T12:00`, options.timeZone ?? householdTimeZone()) ?? `${date}T12:00:00Z`,
    );
    if (index.has(date)) continue;
    const bar: UsageDayBar = {
      date,
      label: dayLabel(at, { timeZone: options.timeZone, now }),
      tokens: { found: 0, quiet: 0, unfinished: 0 },
      runs: { found: 0, quiet: 0, unfinished: 0 },
      total: 0,
    };
    index.set(date, bar);
    bars.push(bar);
  }
  for (const u of usage) {
    if (u.provider === "Demo") continue;
    const bar = index.get(localDate(u.at, { timeZone: options.timeZone }));
    if (!bar) continue;
    const outcome = outcomeOf(u, investigations);
    const tokens = (u.inputTokens || 0) + (u.outputTokens || 0);
    bar.tokens[outcome] += tokens;
    bar.runs[outcome] += 1;
    bar.total += tokens;
  }
  return bars;
}

export interface DaySummary {
  checks: number;
  found: number;
  unfinished: number;
  quick: number;
  tokens: number;
}
/** The last 24 hours: AI checks, how many found something or didn't finish, quick checks without the AI, tokens used. */
export function last24h(
  state: Pick<State, "investigations" | "usage" | "activities">,
  now: Date | number = Date.now(),
): DaySummary {
  const since = new Date(now).getTime() - 86400000;
  const recent = state.investigations.filter((i) => Date.parse(i.at) >= since && i.status !== "Running");
  return {
    checks: recent.length,
    found: recent.filter((i) => !isUnfinished(i) && !isQuiet(i)).length,
    unfinished: recent.filter(isUnfinished).length,
    quick: (state.activities ?? []).filter((a) => a.kind === "check" && Date.parse(a.at) >= since).length,
    tokens: state.usage
      .filter((u) => Date.parse(u.at) >= since && u.provider !== "Demo")
      .reduce((n, u) => n + (u.inputTokens || 0) + (u.outputTokens || 0), 0),
  };
}

/**
 * Today in the household's zone (midnight to now): the same day the "most AI checks a day" cap counts, so the summary
 * and the cap always agree.
 */
export function todaySummary(
  state: Pick<State, "investigations" | "usage" | "activities">,
  options: { now?: Date | number; timeZone?: string } = {},
): DaySummary {
  const now = new Date(options.now ?? Date.now()).getTime();
  const today = localDate(now, { timeZone: options.timeZone });
  const isToday = (at: string) => {
    const t = Date.parse(at);
    return t <= now + 60_000 && localDate(t, { timeZone: options.timeZone }) === today;
  };
  const recent = state.investigations.filter((i) => isToday(i.at) && i.status !== "Running");
  return {
    checks: recent.length,
    found: recent.filter((i) => !isUnfinished(i) && !isQuiet(i)).length,
    unfinished: recent.filter(isUnfinished).length,
    quick: (state.activities ?? []).filter((a) => a.kind === "check" && isToday(a.at)).length,
    tokens: state.usage
      .filter((u) => isToday(u.at) && u.provider !== "Demo")
      .reduce((n, u) => n + (u.inputTokens || 0) + (u.outputTokens || 0), 0),
  };
}

/** One AI check in the runs list: every try it took (a retry is another usage record), its tokens summed. */
export interface UsageGroup {
  key: string;
  /** The check's start, or the record's time when no check matches. */
  at: string;
  check: Investigation | null;
  records: Usage[];
  inputTokens: number;
  outputTokens: number;
  /** null when no try was priced. */
  estimatedUsd: number | null;
}
/** Usage records grouped by the check they paid for, newest check first; records with no matching check stay alone. */
export function groupUsageByCheck(usage: Usage[], investigations: Investigation[]): UsageGroup[] {
  const groups = new Map<string, UsageGroup>();
  for (const u of usage) {
    const check = checkForUsage(u, investigations);
    const key = check ? `c-${check.id}` : `u-${u.at}-${u.model}`;
    let g = groups.get(key);
    if (!g) {
      g = { key, at: check?.at ?? u.at, check, records: [], inputTokens: 0, outputTokens: 0, estimatedUsd: null };
      groups.set(key, g);
    }
    g.records.push(u);
    g.inputTokens += u.inputTokens || 0;
    g.outputTokens += u.outputTokens || 0;
    if (u.estimatedUsd != null) g.estimatedUsd = (g.estimatedUsd ?? 0) + u.estimatedUsd;
  }
  return [...groups.values()].sort((a, b) => Date.parse(b.at) - Date.parse(a.at));
}

/** When Predbat's tools were last used successfully by a check, from its steps. */
export function mcpLastUsed(investigations: Investigation[]): string | null {
  let latest: string | null = null;
  for (const i of investigations) {
    const used =
      (i.stepDetails ?? []).some((s) => s.kind === "mcp" && !/not available/.test(s.label)) ||
      (i.steps ?? []).some((s) => /^mcp:/.test(s) && /\(retrieved/.test(s));
    if (used && (!latest || Date.parse(i.at) > Date.parse(latest))) latest = i.at;
  }
  return latest;
}
