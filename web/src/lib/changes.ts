import type { Experiment, Revision, Setting, SettingEvent } from "../types";
import { displayValue, isTunable, sameValue } from "./settings";
import { humanise } from "./labels";
import { dayLabel, localDate, when } from "./time";

/**
 * The Changes timeline: every settings change (made in Predbat, through Joule, by an approved suggestion or undone) and
 * every change to Predbat's own controls (updates, manual overrides, mode), as plain sentences grouped by day.
 * Only changes to tunable settings can be undone or restored; Predbat's own controls are information.
 */

export type ChangeIcon =
  "first" | "predbat" | "you" | "approved" | "auto" | "undo" | "restore" | "software" | "override" | "control" | "list";

export interface ChangeLine {
  key: string;
  name: string;
  before: string;
  after: string;
}

export interface ChangeEntry {
  id: string;
  at: string;
  icon: ChangeIcon;
  /** The sentence, e.g. "You changed House load scaling 1.08 → 1.00 via Joule". */
  title: string;
  /** Extra context, e.g. the suggestion's title; raw server text, render through PlainText. */
  detail?: string;
  /** Who made it, in words: "In Predbat", "Through Joule". */
  via: string;
  lines: ChangeLine[];
  revision?: Revision;
  event?: SettingEvent;
  /** Trials started by this change. */
  trials: Experiment[];
  /** Undo is possible for this entry (tunable changes still in place, not already undone). */
  undoable: boolean;
  /** Why Undo isn't offered on a tunable change (already undone, changed again later). */
  undoNote?: string;
  /** "Restore settings to this point" is offered (tunable versions only). */
  restorable: boolean;
}

export interface ChangeDay {
  date: string;
  label: string;
  entries: ChangeEntry[];
}

const nameOf = (settings: Map<string, Setting>, key: string) => settings.get(key)?.name ?? humanise(key);
const value = (settings: Map<string, Setting>, key: string, v: string) =>
  displayValue(settings.get(key) ?? { type: "text", unit: "" }, v);

function lineFor(settings: Map<string, Setting>, c: { key: string; before: string; after: string }): ChangeLine {
  return {
    key: c.key,
    name: nameOf(settings, c.key),
    before: value(settings, c.key, c.before),
    after: value(settings, c.key, c.after),
  };
}

/** "House load scaling 1.08 → 1.00", "House load scaling and 2 more". */
function describe(lines: ChangeLine[]) {
  if (lines.length === 1) return `${lines[0].name} ${lines[0].before} → ${lines[0].after}`;
  if (lines.length === 2) return `${lines[0].name} and ${lines[1].name}`;
  return `${lines[0].name} and ${lines.length - 1} more settings`;
}

function revisionEntry(
  r: Revision,
  all: Revision[],
  settings: Map<string, Setting>,
  experiments: Experiment[],
): ChangeEntry | null {
  const known = (key: string) => settings.get(key) ?? { key };
  const tunable = r.changes.filter((c) => isTunable(known(c.key) as Setting));
  const lines = tunable.map((c) => lineFor(settings, c));
  const trials = experiments.filter((e) => e.revisionId === r.id && !e.title.startsWith("Not a tunable"));
  const first = r.id === all[0]?.id;
  const base = { id: `revision-${r.id}`, at: r.at, lines, revision: r, trials, undoable: false, restorable: true };
  if (!tunable.length) {
    if (first) return { ...base, icon: "first", title: "First settings snapshot", via: "Saved by Joule" };
    // Every change was to one of Predbat's own controls: those are shown as their own events.
    if (r.changes.length) return null;
    return { ...base, icon: "list", title: "Predbat added or removed settings", via: "In Predbat", restorable: false };
  }
  const undone = all.some((x) => x.reverts === r.id);
  const laterEdit = tunable.find((c) => {
    const current = settings.get(c.key)?.value;
    return (
      (current !== undefined && !sameValue(current, c.after)) ||
      all.some((x) => x.id > r.id && x.changes.some((y) => y.key === c.key))
    );
  });
  const undoNote = undone
    ? "Already undone"
    : laterEdit
      ? `${nameOf(settings, laterEdit.key)} was changed again later`
      : undefined;
  const entry = { ...base, undoable: !undone && !laterEdit, undoNote };
  const what = describe(lines);
  if (r.reverts != null) {
    const automatic = r.source === "Automatic rollback";
    return {
      ...entry,
      icon: "undo",
      title: `${automatic ? "Joule undid" : "You undid"} a change: ${what}`,
      via: automatic ? "Automatically" : "Through Joule",
    };
  }
  if (/^Restored settings from version (\d+)/.test(r.reason)) {
    const id = Number(/version (\d+)/.exec(r.reason)![1]);
    const target = all.find((x) => x.id === id);
    return {
      ...entry,
      icon: "restore",
      title: `You restored settings${target ? ` from ${when(target.at)}` : ""}: ${what}`,
      via: "Through Joule",
    };
  }
  switch (r.source) {
    case "Predbat":
      return {
        ...entry,
        icon: "predbat",
        title: lines.length === 1 ? `${what} (changed in Predbat)` : `${what} changed in Predbat`,
        via: "In Predbat",
      };
    case "Approved by you":
      return {
        ...entry,
        icon: "approved",
        title: `You approved a suggestion: ${what}`,
        detail: r.reason,
        via: "Through Joule",
      };
    case "Auto":
      return {
        ...entry,
        icon: "auto",
        title: `Joule changed ${what} automatically`,
        detail: r.reason,
        via: "Automatically, within your limits",
      };
    default:
      return { ...entry, icon: "you", title: `You changed ${what} via Joule`, via: "Through Joule" };
  }
}

/**
 * Predbat's version in one format everywhere: "9.3.5" (no "v", no release title). Null when there is none.
 * "v9.3.5 IOG started-dispatch fix" → "9.3.5".
 */
export function predbatVersion(v: string | null | undefined): string | null {
  const m = /v?(\d+(?:\.\d+)+)/i.exec(v ?? "");
  return m ? m[1] : v?.trim() ? v.trim() : null;
}

/**
 * An event in words: Predbat's manual target syntax ("Sat 09:00=100.0" → "Sat 09:00 at 100%"), versions without the "v"
 * ("Predbat updated to 9.3.5") and values with their unit ("Battery reserve changed from 0% to 100%").
 */
function eventTitle(ev: SettingEvent, settings: Map<string, Setting>) {
  let title = ev.title || `${ev.name} changed`;
  if (/^manual_soc/.test(ev.key))
    title = title.replace(/(\d{1,2}:\d{2})=(\d+(?:\.\d+)?)/g, (_, t: string, v: string) => `${t} at ${Number(v)}%`);
  title = title.replace(/\b(updated|changed) to v(\d+(?:\.\d+)+)/, "$1 to $2");
  const setting = settings.get(ev.key);
  if (setting?.unit)
    title = title.replace(
      /changed from (-?\d+(?:\.\d+)?) to (-?\d+(?:\.\d+)?)$/,
      (_, a: string, b: string) => `changed from ${displayValue(setting, a)} to ${displayValue(setting, b)}`,
    );
  return title;
}

const eventIcon = (kind: string): ChangeIcon =>
  kind === "software" ? "software" : kind === "control" ? "control" : "override";

/** Every change, newest first. */
export function buildTimeline(state: {
  revisions: Revision[];
  settings: Setting[];
  experiments: Experiment[];
  settingEvents?: SettingEvent[];
}): ChangeEntry[] {
  const settings = new Map(state.settings.map((s) => [s.key, s]));
  const revisions = [...state.revisions].sort((a, b) => a.id - b.id);
  const entries: ChangeEntry[] = [];
  for (const r of revisions) {
    const e = revisionEntry(r, revisions, settings, state.experiments);
    if (e) entries.push(e);
  }
  for (const ev of state.settingEvents ?? []) {
    entries.push({
      id: `event-${ev.id}`,
      at: ev.at,
      icon: eventIcon(ev.kind),
      title: eventTitle(ev, settings),
      via: "In Predbat",
      lines: [],
      event: ev,
      trials: [],
      undoable: false,
      restorable: false,
    });
  }
  return entries.sort((a, b) => Date.parse(b.at) - Date.parse(a.at) || b.id.localeCompare(a.id));
}

/** Entries grouped by local day: "Today", "Yesterday", "Sat 3 Oct". */
export function groupByDay(entries: ChangeEntry[], options: { timeZone?: string; now?: number } = {}): ChangeDay[] {
  const days: ChangeDay[] = [];
  for (const e of entries) {
    const date = localDate(e.at, options);
    let day = days.find((d) => d.date === date);
    if (!day) days.push((day = { date, label: dayLabel(e.at, { ...options, relative: true }), entries: [] }));
    day.entries.push(e);
  }
  return days;
}
