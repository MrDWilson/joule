import type { Payload, Setting, SettingKind } from "../types";
import { modeLabel } from "./copy";
import type { Tone } from "../components/ui/Chip";

/**
 * Predbat settings as people think about them: the ones you tune (grouped by Predbat's own documentation sections) and
 * Predbat's own controls (manual overrides, software, mode, diagnostics), which Joule shows as status and never edits.
 */

/** Tunable sections in the catalogue's order; anything else that is tunable goes in "Other". */
export const TUNABLE_SECTIONS = [
  "Battery",
  "Charging",
  "Export",
  "Forecast",
  "Planning",
  "Car & Octopus",
  "Freeze & inverter",
  "iBoost",
  "Other",
] as const;

/** Predbat's own controls, shown read-only under "Predbat status", in this order. */
export const STATUS_KINDS: SettingKind[] = ["control", "software", "override", "debug"];
export const statusKindLabel: Record<string, string> = {
  control: "Predbat control",
  software: "Predbat software",
  override: "Manual overrides",
  debug: "Notifications & diagnostics",
};

const KEY_KINDS: [RegExp, SettingKind][] = [
  [/^(update|auto_update|saverestore|expert_mode)$/, "software"],
  [/^(mode|set_read_only|active)$/, "control"],
  [/^(manual_|holiday_days)/, "override"],
  [/^(debug_|chat_)|_notify$|^plan_debug$|_today$/, "debug"],
];

/** The setting's kind: the server's classification, or Predbat's naming patterns for older servers. */
export function kindOf(s: Pick<Setting, "key" | "kind">): SettingKind {
  if (s.kind) return s.kind;
  for (const [pattern, kind] of KEY_KINDS) if (pattern.test(s.key)) return kind;
  return "tunable";
}
/** Only tunable settings can be edited, undone or restored through Joule ("active" is Predbat's calculating flag). */
export const isTunable = (s: Pick<Setting, "key" | "kind">) => s.key !== "active" && kindOf(s) === "tunable";

export function sectionOf(s: Setting): string {
  const section = s.section ?? s.category;
  return (TUNABLE_SECTIONS as readonly string[]).includes(section) ? section : "Other";
}

/**
 * A quiet note only where it means something, for catalogued tunable settings: "Change with care" (Medium) or "Always asks
 * you" (High: the AI never changes it by itself). Neutral, not an alarm: these are ordinary settings to change thoughtfully.
 */
export function riskBadge(s: Setting): { label: string; tone: Tone } | null {
  if (!isTunable(s)) return null;
  // Settings the catalogue doesn't know default to High on the server; that says nothing, so no badge.
  const catalogued = !!(s.documentationAnchor || s.description);
  if (!catalogued) return null;
  if (s.risk === "Medium") return { label: "Change with care", tone: "neutral" };
  if (s.risk === "High") return { label: "Always asks you", tone: "neutral" };
  return null;
}

const asNumber = (v: string | null | undefined) => (v == null || v.trim() === "" ? NaN : Number(v));
/** Values compare as numbers when both are numbers (so "1.0" equals "1"), otherwise case-insensitively. */
export function sameValue(a: string | null | undefined, b: string | null | undefined) {
  const x = asNumber(a),
    y = asNumber(b);
  if (Number.isFinite(x) && Number.isFinite(y)) return Math.abs(x - y) < 1e-9;
  return (a ?? "").trim().toLowerCase() === (b ?? "").trim().toLowerCase();
}

export const changedFromDefault = (s: Setting) =>
  s.default != null && s.default !== "" && !sameValue(s.value, s.default);

/**
 * A value the way people read it: on/off → On/Off, numbers with their unit ("10 %" → "10%"), clock times without seconds
 * ("07:00:00" → "07:00"), long text as is.
 */
export function displayValue(s: Pick<Setting, "type" | "unit">, value: string | null | undefined) {
  if (value == null || value === "") return "Not set";
  if (s.type === "boolean" || /^(on|off)$/i.test(value))
    return /^on$/i.test(value) ? "On" : /^off$/i.test(value) ? "Off" : value;
  if (/^\d{2}:\d{2}:\d{2}$/.test(value)) return value.slice(0, 5);
  const unit = s.unit ?? "";
  if (unit && Number.isFinite(asNumber(value))) return unit === "%" ? `${value}%` : `${value} ${unit}`;
  return value;
}

export type SettingsFilter = "common" | "changed" | "ai" | "all";
export const filterLabels: Record<SettingsFilter, string> = {
  common: "Commonly tuned",
  changed: "Changed from default",
  ai: "AI may change",
  all: "All",
};

export function matchesQuery(s: Setting, query: string) {
  const q = query.trim().toLowerCase();
  if (!q) return true;
  return [s.name, s.key, s.entityId, s.section, s.category, s.description, s.predbatName]
    .filter(Boolean)
    .some((x) => x!.toLowerCase().includes(q));
}

export function applyFilter(settings: Setting[], filter: SettingsFilter) {
  const tunable = settings.filter(isTunable);
  switch (filter) {
    case "common":
      return tunable.filter((s) => s.commonlyTuned);
    case "changed":
      return tunable.filter(changedFromDefault);
    case "ai":
      return tunable.filter((s) => s.autoAllowed);
    default:
      return tunable;
  }
}

/** Settings grouped by section in catalogue order; empty sections are left out. */
export function bySection(settings: Setting[]) {
  const groups = new Map<string, Setting[]>();
  for (const name of TUNABLE_SECTIONS) groups.set(name, []);
  for (const s of settings) groups.get(sectionOf(s))!.push(s);
  return [...groups.entries()]
    .filter(([, list]) => list.length)
    .map(([section, list]) => ({ section, settings: list }));
}

/**
 * A read-only status row's value and, when there is one, its detail: Predbat's version reads "9.3.5", with the release
 * title ("Bug fixes cloud inverters & Misc") underneath rather than squeezed into the value.
 */
export function statusValue(s: Pick<Setting, "key" | "type" | "unit">, value: string | null | undefined) {
  if (s.key === "update") {
    const m = /^v?(\d+(?:\.\d+)+)\s*(.*)$/i.exec(value ?? "");
    if (m) return { value: m[1], detail: m[2] || null };
  }
  return { value: displayValue(s, value), detail: null };
}

/** Predbat's own controls grouped by kind, for the read-only "Predbat status" section. */
export function statusGroups(settings: Setting[]) {
  return STATUS_KINDS.map((kind) => ({
    kind,
    label: statusKindLabel[kind],
    settings: settings.filter((s) => !isTunable(s) && (s.key === "active" ? "control" : kindOf(s)) === kind),
  })).filter((g) => g.settings.length);
}

/** The largest automatic step the server accepts: 10% of the setting's range (at least one step), or 0.1 without one. */
export function maxAutoStep(s: Pick<Setting, "min" | "max" | "step">) {
  if (s.min != null && s.max != null && s.max > s.min)
    return Math.max(s.step > 0 ? s.step : 0, Math.round((s.max - s.min) * 0.1 * 1e10) / 1e10);
  return 0.1;
}

/**
 * A setting's value over time from the saved versions (oldest first), keeping only the points where it changed.
 * `revisions` carry every tunable value at that time.
 */
export function valueHistory(key: string, revisions: { at: string; values: Record<string, string> }[]) {
  const points: { at: string; value: string }[] = [];
  for (const r of revisions) {
    const v = r.values[key];
    if (v === undefined) continue;
    if (!points.length || !sameValue(points[points.length - 1].value, v)) points.push({ at: r.at, value: v });
  }
  return points;
}

/** Why a change to Predbat (edit, undo, restore) can't be saved right now, in one line; empty when it can. */
export function editLock(data: Pick<Payload, "state" | "connection">) {
  const s = data.state,
    c = data.connection;
  if (!c.demo && !c.writesEnabled) return "Saving needs live writes turned on";
  if (s.mode === "Monitor") return `Editing is locked while Joule is in ${modeLabel("Monitor")}`;
  if (s.writeUncertain) return "Joule isn't sure its last change reached Predbat: re-read Predbat's settings first";
  if (s.pendingFileReload) return "Predbat's files were restored: confirm Predbat restarted first";
  return "";
}
