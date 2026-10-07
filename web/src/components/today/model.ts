/**
 * The Today page's story, as pure functions: what the battery is doing now, how today is going, how last night's cheap
 * window went, what the AI last found and whether the plan on screen is current. Every sentence is built here from the
 * figures, never by a model, so the same data always reads the same way. Unit-tested in model.test.ts.
 */
import type { EnergyMetric, EnergySummary, LatestReading } from "../../completion-types";
import type { Investigation, Usage } from "../../types";
import { gbp, kw, kwh, number, percent } from "../../lib/format";
import { clock, localDate } from "../../lib/time";
import { metricLabel } from "../../lib/labels";
import { standingCharge, type StandingChargeView } from "../../lib/energy";
import { investigationVerdict, type VerdictView } from "../InvestigationText";
import { slotAction } from "../../lib/planActions";
import { dayWord, type ActionView, type PlanSlotLike, type PlanWindow } from "../plan/windows";
import type { FlagCause } from "../plan/history";

const MIN = 60000;
const HOUR = 3600000;
const finite = (v: number | null | undefined): v is number => typeof v === "number" && Number.isFinite(v);
const startOf = (s: { time: string }) => Date.parse(s.time);
const endOf = (s: { time: string; durationMinutes: number }) => startOf(s) + (s.durationMinutes || 30) * MIN;

/** Fields the server adds to a latest reading (stream B); optional so older servers still read. */
export type Reading = LatestReading & {
  lastObservedValue?: number | null;
  lastObservedAt?: string | null;
  stale?: boolean;
  expected?: boolean;
  reason?: string | null;
};
/** A metric with the server's accounting detail (stream B). */
export type MetricDetail = EnergyMetric & {
  state?: string;
  gaps?: { from: string; to: string; reason: string; knownKwh?: number | null }[];
  estimatedKwh?: number | null;
  profile?: string | null;
};
export type SummaryDetail = EnergySummary & {
  importCostEstimated?: boolean;
  exportCostEstimated?: boolean;
  costGaps?: { metric: string; from: string; to: string; reason: string }[];
};

// ------------------------------------------------------------------ now

/** The slot covering `now`, if the plan has one. */
export function slotAt<T extends { time: string; durationMinutes: number }>(slots: T[], now: number): T | undefined {
  return slots.find((s) => startOf(s) <= now && endOf(s) > now);
}

/** What the battery is doing, in the present tense: "Powering your home", "Charging from the grid". */
export function presentAction(action: ActionView) {
  if (action.id !== action.key && action.id !== "unknown" && action.label) return action.label;
  switch (action.key) {
    case "demand":
    case "no-charge":
      return "Powering your home";
    case "charge":
      return "Charging from the grid";
    case "freeze-charge":
      return "Holding the battery level";
    case "hold-charge":
      return "Holding at the charge target";
    case "export":
      return "Exporting to the grid";
    case "freeze-export":
      return "Exporting solar, battery held";
    case "hold-export":
      return "Export paused at the minimum level";
    case "charge-export":
      return "Charging, then exporting";
    default:
      return action.label;
  }
}

export type Direction = "charging" | "discharging" | "steady";
export interface BatteryNow {
  /** The level to show: the latest reading, or the last good one when the sensor is offline. */
  value: number | null;
  time: string | null;
  /** True when the value is not a current reading (sensor offline, or older than an hour): show it greyed. */
  stale: boolean;
  direction: Direction | null;
  /** Where the direction came from: the measured level, or Predbat's plan for this half-hour. */
  directionSource: "measured" | "plan" | null;
  /** Predbat's planned rate for this half-hour, kW (positive charging), when it agrees with the direction. */
  kw: number | null;
}

/**
 * The battery right now. The direction comes from the measured level over the last half-hour or so when it moved by a
 * point or more; otherwise from Predbat's plan for the current slot.
 */
export function batteryNow({
  reading,
  recent,
  plan,
  now,
}: {
  reading: Reading | null | undefined;
  recent: PlanSlotLike[];
  plan: PlanSlotLike[];
  now: number;
}): BatteryNow {
  const live = reading && finite(reading.value) ? reading.value : null;
  const liveAt = reading?.time ? Date.parse(reading.time) : NaN;
  const fresh = live != null && Number.isFinite(liveAt) && now - liveAt <= HOUR;
  const value = live ?? (finite(reading?.lastObservedValue) ? reading!.lastObservedValue! : null);
  const time = live != null ? (reading?.time ?? null) : (reading?.lastObservedAt ?? null);
  // Measured points from the last hour.
  const points: { t: number; v: number }[] = [];
  for (const s of recent) {
    if (finite(s.socActualStart)) points.push({ t: startOf(s), v: s.socActualStart });
    if (finite(s.socActual)) points.push({ t: endOf(s), v: s.socActual });
  }
  if (fresh) points.push({ t: liveAt, v: live! });
  const latest = points.filter((p) => p.t <= now + MIN).sort((a, b) => b.t - a.t)[0];
  const earlier = latest
    ? points
        .filter((p) => p.t <= latest.t - 20 * MIN && p.t >= latest.t - 75 * MIN)
        .sort((a, b) => b.t - a.t)
        .at(-1)
    : undefined;
  let direction: Direction | null = null,
    source: BatteryNow["directionSource"] = null;
  if (fresh && latest && earlier && now - latest.t <= HOUR) {
    const d = latest.v - earlier.v;
    direction = d >= 1 ? "charging" : d <= -1 ? "discharging" : "steady";
    source = "measured";
  }
  const current = slotAt(plan, now);
  // The sign of the planned change: Predbat's kWh when it sent one (0.05 kWh ≈ a point), else the level change in points.
  const planned = current
    ? finite(current.socChangeKwh)
      ? current.socChangeKwh / 0.05
      : finite(current.socForecastEnd) && finite(current.socForecast)
        ? current.socForecastEnd - current.socForecast
        : null
    : null;
  const plannedDirection: Direction | null =
    planned == null ? null : planned >= 1 ? "charging" : planned <= -1 ? "discharging" : "steady";
  if ((!direction || direction === "steady") && plannedDirection && plannedDirection !== "steady" && fresh) {
    direction = plannedDirection;
    source = "plan";
  }
  const kw =
    current && finite(current.socChangeKwh) && direction && direction !== "steady" && plannedDirection === direction
      ? current.socChangeKwh / ((current.durationMinutes || 30) / 60)
      : null;
  return { value, time, stale: value != null && !fresh, direction, directionSource: source, kw };
}

/** "Discharging · about 1.0 kW", "Charging · about 4.8 kW", "Holding steady". */
export function directionText(b: BatteryNow) {
  if (!b.direction) return "";
  const rate = b.kw != null && Math.abs(b.kw) >= 0.05 ? ` · about ${kw(Math.abs(b.kw))}` : "";
  return b.direction === "charging"
    ? `Charging${rate}`
    : b.direction === "discharging"
      ? `Discharging${rate}`
      : "Holding steady";
}

export interface PriceNow {
  import: number | null;
  export: number | null;
  /** The import price is among the cheapest of the next 24 hours. */
  cheap: boolean;
  /** The cheapest import price still to come in the next 24 hours, and when it starts. */
  cheapest: { rate: number; at: number } | null;
}
/**
 * The current prices: the tariff sensors when they are fresh, otherwise Predbat's plan for this slot. During a saving
 * session (export) or a free session (import) the plan's rate wins: the tariff sensor keeps showing the normal rate while
 * Predbat plans with the session's price.
 */
export function priceNow({
  importReading,
  exportReading,
  plan,
  now,
}: {
  importReading?: Reading | null;
  exportReading?: Reading | null;
  plan: PlanSlotLike[];
  now: number;
}): PriceNow {
  const fresh = (r?: Reading | null) =>
    r && finite(r.value) && r.time && now - Date.parse(r.time) <= HOUR ? r.value : null;
  const current = slotAt(plan, now);
  const planImport = finite(current?.importRate) ? current!.importRate! : null;
  const planExport = finite(current?.exportRate) ? current!.exportRate! : null;
  // A saving session pays for what is exported (Predbat's plan lifts the import rate only to model the reward), so only a
  // free session changes the import price shown.
  const sessionImport = planImport != null && planImport <= 0;
  const sessionExport = current?.exportRateType === "saving";
  const imp = (sessionImport ? planImport : null) ?? fresh(importReading) ?? planImport;
  const exp = (sessionExport ? planExport : null) ?? fresh(exportReading) ?? planExport;
  const ahead = plan.filter((s) => endOf(s) > now && startOf(s) < now + 24 * HOUR && finite(s.importRate));
  const min = ahead.length ? Math.min(...ahead.map((s) => s.importRate!)) : null;
  const max = ahead.length ? Math.max(...ahead.map((s) => s.importRate!)) : null;
  const cheapestSlot = ahead.find((s) => s.importRate === min && startOf(s) > now) ?? null;
  return {
    import: imp,
    export: exp,
    cheap: imp != null && min != null && max != null && max - min >= 2 && imp <= min + 0.5,
    cheapest:
      cheapestSlot && min != null && imp != null && imp - min >= 2
        ? { rate: min, at: Math.max(startOf(cheapestSlot), now) }
        : null,
  };
}

/** The window to mention as "Next": the first one after the current that charges, exports or holds; else simply the next. */
export function nextWindow(windows: PlanWindow[], now: number) {
  const ahead = windows.filter((w) => w.start > now && w.start < now + 24 * HOUR);
  return ahead.find((w) => w.key !== "demand" && w.key !== "no-charge") ?? ahead[0] ?? null;
}

/**
 * The battery tile's look ahead: "Falls to 4% by 23:30 · charges to 100% tonight", or "Charging to 100% by 05:30".
 */
export function batteryOutlook(windows: PlanWindow[], plan: PlanSlotLike[], now: number, timeZone?: string) {
  const current = windows.find((w) => w.phase === "current");
  if (current && (current.key === "charge" || current.key === "charge-export") && current.soc.end != null)
    return `Charging to ${percent(current.target ?? current.soc.end)} by ${clock(current.end, { timeZone })}`;
  const charge = windows.find((w) => w.start > now && (w.key === "charge" || w.key === "charge-export"));
  const before = plan.filter(
    (s) => endOf(s) > now && (!charge || startOf(s) <= charge.start) && finite(s.socForecastEnd ?? s.socForecast),
  );
  const level = (s: PlanSlotLike) => (finite(s.socForecastEnd) ? s.socForecastEnd : s.socForecast!) as number;
  const low = before.length ? before.reduce((a, b) => (level(b) < level(a) ? b : a)) : null;
  const lowText = low
    ? `Falls to ${percent(level(low))} by ${clock(finite(low.socForecastEnd) ? endOf(low) : startOf(low), { timeZone })}`
    : "";
  if (!charge) return lowText ? `${lowText} (lowest in the plan)` : "";
  const when = dayWord(charge.start, now, timeZone).toLowerCase();
  const to = charge.target ?? charge.soc.end;
  return [lowText, to != null ? `charges to ${percent(to)} ${when}` : ""].filter(Boolean).join(" · ");
}

// ------------------------------------------------------------------ today's figures

export interface CostView {
  /** "£1.05", "≈ £1.05" or "£4.25" (earnings). */
  value: string;
  label: "Net cost today" | "Net earnings today";
  earning: boolean;
  approx: boolean;
  paid: number | null;
  earned: number | null;
  /** "Paid £1.83 · Earned £0.77". */
  breakdown: string;
  /** Which meter is short and when, when a side is below 95%. */
  note: string | null;
  /** Today's standing charge so far, whether or not it is in `value`. */
  standing: StandingChargeView | null;
}
/**
 * Net cost = what you paid for imports minus what you earned from exports, each with its own coverage, plus the standing
 * charge so far when the owner includes it (the default once Joule knows it).
 */
export function costView(summary: SummaryDetail | null | undefined, timeZone?: string): CostView | null {
  if (!summary) return null;
  const paid = finite(summary.importCostGbp) ? summary.importCostGbp : null;
  const earned = finite(summary.exportCreditGbp) ? summary.exportCreditGbp : null;
  const energy = finite(summary.netCostGbp)
    ? summary.netCostGbp
    : paid != null || earned != null
      ? (paid ?? 0) - (earned ?? 0)
      : // An older server sends only the matched-period figure.
        finite(summary.observedNetCostGbp)
        ? summary.observedNetCostGbp
        : null;
  const standing = standingCharge(summary);
  const net = energy != null && standing?.included ? energy + standing.amount : energy;
  const importCover = summary.importCostCoverage ?? summary.costCoverageFraction ?? 0;
  const exportCover = summary.exportCostCoverage ?? summary.costCoverageFraction ?? 0;
  const approx =
    net != null &&
    (importCover < 0.95 || exportCover < 0.95 || !!summary.importCostEstimated || !!summary.exportCostEstimated);
  const earning = net != null && net < -0.005;
  const short = [
    importCover < 0.95
      ? coverageNote("grid_import", summary.metrics?.grid_import as MetricDetail | undefined, timeZone)
      : null,
    exportCover < 0.95
      ? coverageNote("grid_export", summary.metrics?.grid_export as MetricDetail | undefined, timeZone)
      : null,
  ].filter(Boolean);
  return {
    value: net == null ? "—" : `${approx ? "≈ " : ""}${gbp(Math.abs(net))}`,
    label: earning ? "Net earnings today" : "Net cost today",
    earning,
    approx,
    paid,
    earned,
    breakdown: `Paid ${gbp(paid)} · Earned ${gbp(earned)}`,
    note: short.length ? short.join("; ") : null,
    standing,
  };
}

export interface GridView {
  /** Bought from the grid so far today, kWh, and what it cost. */
  importKwh: number | null;
  importGbp: number | null;
  /** The average price paid per kWh bought (pence), when both are known and enough was bought to make it meaningful. */
  averagePence: number | null;
  /** Sold to the grid, kWh, and what it earned. */
  exportKwh: number | null;
  exportGbp: number | null;
  /** "Exported 2.1 kWh · earned £0.32", or "Nothing exported yet". */
  exported: string;
  /** Which meter is short and when, below 95% coverage. */
  note: string | null;
}

/** The Grid tile: what was bought and sold so far today, in kWh and pounds. */
export function gridView(summary: SummaryDetail | null | undefined, timeZone?: string): GridView | null {
  if (!summary) return null;
  const imp = summary.metrics?.grid_import as MetricDetail | undefined,
    exp = summary.metrics?.grid_export as MetricDetail | undefined;
  const importKwh = finite(imp?.energyKwh) ? imp!.energyKwh : null,
    exportKwh = finite(exp?.energyKwh) ? exp!.energyKwh : null;
  if (importKwh == null && exportKwh == null) return null;
  const exportGbp = finite(summary.exportCreditGbp) ? summary.exportCreditGbp : null;
  const exported =
    exportKwh == null
      ? "Export not measured"
      : exportKwh < 0.05
        ? "Nothing exported yet"
        : `Exported ${kwh(exportKwh)}${exportGbp != null ? ` · earned ${gbp(exportGbp)}` : ""}`;
  const notes = [coverageNote("grid_import", imp, timeZone), coverageNote("grid_export", exp, timeZone)].filter(
    Boolean,
  );
  const importGbp = finite(summary.importCostGbp) ? summary.importCostGbp : null;
  return {
    importKwh,
    importGbp,
    averagePence: importGbp != null && importKwh != null && importKwh >= 0.1 ? (importGbp * 100) / importKwh : null,
    exportKwh,
    exportGbp,
    exported,
    note: notes.length ? notes.join("; ") : null,
  };
}

const gapReasons: Record<string, string> = {
  offline: "offline",
  idle: "not reporting",
  not_found: "missing in Home Assistant",
  invalid: "unreadable",
  no_samples: "no readings",
  reset: "reset unexpectedly",
  source_changed: "changed sensor",
};
/**
 * "Export meter offline 00:00–02:47" for a meter below 98% coverage, naming the longest gaps. Null when coverage is 98%
 * or better (the UI never mentions coverage then).
 */
export function coverageNote(metric: string, m: MetricDetail | null | undefined, timeZone?: string): string | null {
  if (!m || m.coverageFraction >= 0.98) return null;
  const name = meterName(metric);
  const gaps = (m.gaps ?? []).filter((g) => Date.parse(g.to) - Date.parse(g.from) >= 60000);
  if (!gaps.length)
    return m.coverageFraction <= 0
      ? `${name}: no readings yet`
      : `${name}: ${percent(m.coverageFraction, { fraction: true, round: "floor" })} measured`;
  const longest = [...gaps].sort(
    (a, b) => Date.parse(b.to) - Date.parse(b.from) - (Date.parse(a.to) - Date.parse(a.from)),
  )[0];
  const more = gaps.length > 1 ? ` and ${gaps.length - 1} more gap${gaps.length > 2 ? "s" : ""}` : "";
  return `${name} ${gapReasons[longest.reason] ?? "missing"} ${clock(longest.from, { timeZone })}–${clock(longest.to, { timeZone })}${more}`;
}
function meterName(metric: string) {
  const names: Record<string, string> = {
    grid_import: "Import meter",
    grid_export: "Export meter",
    pv: "Solar meter",
    load: "Home-use meter",
    ev: "Car charger",
    home: "Home-use meter",
  };
  return names[metric] ?? `${metricLabel(metric)} meter`;
}

/** "Yesterday by now: 42.0 kWh"; null when yesterday's figure isn't comparable (missing, or under 90% measured). */
export function yesterdayByNow(
  previous: { value: number | null | undefined; coverage?: number | null },
  format: (n: number) => string,
) {
  if (!finite(previous.value)) return null;
  if (previous.coverage != null && previous.coverage < 0.9) return null;
  return `Yesterday by now: ${format(previous.value)}`;
}

/** Home use without the car when the server derived it, else the load meter; with the car's share alongside. */
export function homeUse(summary: EnergySummary | null | undefined) {
  if (!summary) return null;
  const home = summary.home && finite(summary.home.energyKwh) ? summary.home : null;
  const load = summary.metrics?.load;
  const car = summary.metrics?.ev && finite(summary.metrics.ev.energyKwh) ? summary.metrics.ev.energyKwh : null;
  return {
    metric: (home ?? load ?? null) as MetricDetail | null,
    /** The car's charging, shown separately only when the home figure excludes it. */
    car: home && car != null && car >= 0.05 ? car : null,
    excludesCar: !!home,
  };
}

// ------------------------------------------------------------------ last night

export interface CheapWindow {
  start: number;
  end: number;
  rate: number;
  slots: PlanSlotLike[];
}
/**
 * The most recent finished cheap-import window of at least an hour in the measured history: the half-hours priced
 * within 0.5p of the cheapest import price there. Null when prices are flat (no cheap window to speak of).
 */
export function lastCheapWindow(recent: PlanSlotLike[], now: number): CheapWindow | null {
  const done = recent.filter((s) => endOf(s) <= now && finite(s.importRate)).sort((a, b) => startOf(a) - startOf(b));
  if (!done.length) return null;
  const rates = done.map((s) => s.importRate!);
  const min = Math.min(...rates),
    max = Math.max(...rates);
  if (max - min < 2) return null;
  const runs: PlanSlotLike[][] = [];
  for (const s of done) {
    if (s.importRate! > min + 0.5) continue;
    const last = runs.at(-1);
    if (last && Math.abs(endOf(last.at(-1)!) - startOf(s)) < MIN) last.push(s);
    else runs.push([s]);
  }
  // A window still in progress (its next half-hour is cheap too and not over yet) isn't "last night" yet: during it, the
  // card grades the one before.
  const inProgress = (run: PlanSlotLike[]) => {
    const next = recent.find((s) => Math.abs(startOf(s) - endOf(run.at(-1)!)) < MIN);
    return !!next && finite(next.importRate) && next.importRate <= min + 0.5 && endOf(next) > now;
  };
  const run = runs.filter((r) => endOf(r.at(-1)!) - startOf(r[0]) >= HOUR && !inProgress(r)).at(-1);
  if (!run) return null;
  return { start: startOf(run[0]), end: endOf(run.at(-1)!), rate: min, slots: run };
}

export interface LastNight {
  window: CheapWindow;
  socStart: number | null;
  socEnd: number | null;
  /** The highest measured level in the window and when it was reached. */
  peak: { value: number; at: number } | null;
  /** The level the plan aimed for by the end of the window (the plan in force the evening before, when known). */
  planned: number | null;
  plannedFrom: string | null;
  importKwh: number | null;
  importCost: number | null;
  /** Average import price over the window, pence per kWh. */
  averagePrice: number | null;
  batteryKwh: number | null;
  carKwh: number | null;
  exportKwh: number | null;
  exportCredit: number | null;
}
/** Measured levels for the window from the history slots, plus the window's meter totals when they have loaded. */
export function lastNight(
  window: CheapWindow,
  summary: SummaryDetail | null | undefined,
  planned?: { level: number | null; at: string | null } | null,
): LastNight {
  const first = window.slots[0],
    last = window.slots.at(-1)!;
  const socStart = finite(first.socActualStart) ? first.socActualStart : null;
  const socEnd = finite(last.socActual) ? last.socActual : null;
  let peak: LastNight["peak"] = null;
  for (const s of window.slots)
    if (finite(s.socActual) && (!peak || s.socActual > peak.value + 0.5)) peak = { value: s.socActual, at: endOf(s) };
  const metric = (k: string) => {
    const m = summary?.metrics?.[k];
    return m && finite(m.energyKwh) ? m.energyKwh : null;
  };
  const importKwh = metric("grid_import");
  const importCost = finite(summary?.importCostGbp) ? summary!.importCostGbp : null;
  const target = window.slots.map((s) => s.targetPercent).filter(finite);
  return {
    window,
    socStart,
    socEnd,
    peak,
    planned: planned?.level ?? (target.length ? Math.max(...target) : null),
    plannedFrom: planned?.at ?? null,
    importKwh,
    importCost,
    averagePrice: importKwh != null && importKwh > 0.1 && importCost != null ? (importCost * 100) / importKwh : null,
    batteryKwh: metric("battery_charge"),
    carKwh: metric("ev"),
    exportKwh: metric("grid_export"),
    exportCredit: finite(summary?.exportCreditGbp) ? summary!.exportCreditGbp : null,
  };
}

/** Why the battery fell during a charge, when the plan history knows (components/plan/history.ts): the car, or a pause. */
export type { FlagCause };
/** A history row's flag: plain text, or (from the plan history, components/plan/history.ts) text with its cause. */
export type HistoryFlag = string | { text: string; cause?: FlagCause };
export interface NightFlag {
  /** The sentence to show, with its cause when known ("Battery fell while the car was charging"). */
  text: string;
  /** Start of the half-hour it is about. */
  at: number;
  cause: FlagCause;
  /** A planned pause is information; anything else contradicted the plan. */
  tone: "warn" | "info";
}
/** A flag from last night's half-hours in plain words, naming its cause when known; with no cause the text as it was. */
export function nightFlag(flag: HistoryFlag, at: number): NightFlag {
  const raw = typeof flag === "string" ? flag : flag.text;
  const cause = typeof flag === "string" ? null : (flag.cause ?? null);
  const fell = /^Battery fell during a charge/i.test(raw);
  const text =
    fell && cause === "car"
      ? "Battery fell while the car was charging"
      : fell && cause === "pause"
        ? "Battery fell during a planned pause"
        : raw;
  return { text, at, cause, tone: cause === "pause" ? "info" : "warn" };
}

// ------------------------------------------------------------------ the one-line story

/**
 * "Net cost £1.05 so far (paid £1.83, earned £0.77) · battery filled with 13.6 kWh overnight at 6.7p · nothing needs
 * you", or "Up £0.72 today (paid £1.83, earned £2.55)" when exports have paid for more than imports cost. Built only from
 * figures that are there; parts with no data are left out. When something needs you the "Needs you" list right below
 * says so, so the sentence only mentions the calm case.
 */
export function soFarSentence({
  cost,
  night,
  needs,
  now,
  timeZone,
}: {
  cost: CostView | null;
  night: LastNight | null;
  needs: number;
  /** With `now`, the cheap window only counts when it ended today, and a daytime one is named by its times. */
  now?: number;
  timeZone?: string;
}) {
  const parts: string[] = [];
  if (cost && cost.value !== "—") {
    const value = cost.value.replace("≈ ", "≈");
    const money = cost.earning ? `Up ${value} today` : `Net cost ${value} so far`;
    const standing = cost.standing?.included ? `, standing charge ${cost.standing.text}` : "";
    const detail =
      cost.paid != null || cost.earned != null
        ? ` (paid ${gbp(cost.paid)}, earned ${gbp(cost.earned)}${standing})`
        : "";
    parts.push(`${money}${detail}`);
  }
  const endedToday =
    !night || now == null || localDate(night.window.end - MIN, { timeZone }) === localDate(now, { timeZone });
  if (night && endedToday && night.batteryKwh != null && night.batteryKwh >= 0.5) {
    const startHour = Number(clock(night.window.start, { timeZone }).slice(0, 2));
    const when =
      startHour >= 18 || startHour < 6
        ? "overnight"
        : `${clock(night.window.start, { timeZone })}–${clock(night.window.end, { timeZone })}`;
    parts.push(
      `battery filled with ${kwh(night.batteryKwh)} ${when}${night.averagePrice != null ? ` at ${number(night.averagePrice, 1)}p` : ""}`,
    );
  }
  if (needs === 0) parts.push("nothing needs you");
  const text = parts.join(" · ");
  return text.charAt(0).toUpperCase() + text.slice(1);
}

// ------------------------------------------------------------------ AI

/**
 * The finding worth knowing: the completed problem or opportunity of the last 48 hours with the most money at stake (then
 * problems first, then newest), not dismissed and not a repeat. Mirrors the server's topFinding48h.
 */
export function topFinding(investigations: Investigation[], now: number) {
  return (
    investigations
      .filter(
        (i) =>
          Date.parse(i.at) >= now - 48 * HOUR &&
          i.status === "Completed" &&
          !i.dismissedAt &&
          !i.repeatOf &&
          (i.verdict === "problem" || i.verdict === "opportunity"),
      )
      .sort(
        (a, b) =>
          (b.impactPence ?? -Infinity) - (a.impactPence ?? -Infinity) ||
          Number(b.verdict === "problem") - Number(a.verdict === "problem") ||
          Date.parse(b.at) - Date.parse(a.at),
      )[0] ?? null
  );
}

export interface RunStrip {
  runs: { id: string; at: string; view: VerdictView }[];
  quiet: number;
  failed: number;
  findings: number;
  /**
   * AI checks counted against today's allowance, by the server's own rule (schedule.runsToday): usage records finished
   * today, home time, that completed or got an AI answer, Ask Joule questions included. Null without usage records.
   */
  used: number | null;
}
const FOUND: VerdictView[] = ["problem", "opportunity", "finding"];
/** The legend's groups, in the shared words for an AI check's outcome (as on Insights and AI checks). */
const LEGEND: { key: string; views: VerdictView[]; text: string }[] = [
  { key: "found", views: FOUND, text: "found something" },
  { key: "no_change", views: ["no_change"], text: "nothing new" },
  { key: "didnt_finish", views: ["didnt_finish"], text: "didn’t finish" },
  { key: "running", views: ["running"], text: "checking now" },
];
/**
 * The dots' legend in words, only for the outcomes present: "2 found something", "4 nothing new", "1 didn't finish".
 * `views` are the dot colours present in the group (a problem is amber, an opportunity green).
 */
export function stripLegend(strip: RunStrip) {
  return LEGEND.map(({ key, views, text }) => {
    const runs = strip.runs.filter((r) => views.includes(r.view));
    return {
      key,
      views: views.filter((v) => runs.some((r) => r.view === v)),
      count: runs.length,
      text: `${runs.length} ${text}`,
    };
  }).filter((l) => l.count > 0);
}

/** Whether a usage record counts against the daily allowance: as InvestigationScheduler.Counts on the server. */
const countsToday = (u: Pick<Usage, "status" | "inputTokens" | "outputTokens">) =>
  u.status === "Completed" || (u.inputTokens || 0) + (u.outputTokens || 0) > 0;

/**
 * Today's AI checks (household day), oldest first, with their verdicts; failed runs are "didn't finish", never problems.
 * A check belongs to the day it finished, as the server counts it (one that runs over midnight counts tomorrow); one
 * still running sits on the day it started. Ask Joule questions are AI checks too and are included.
 */
export function runStrip(
  investigations: Investigation[],
  now: number,
  timeZone?: string,
  usage?: Pick<Usage, "at" | "status" | "inputTokens" | "outputTokens">[],
): RunStrip {
  const today = localDate(now, { timeZone });
  const dayOf = (i: Investigation) => (i.status === "Running" ? i.at : (i.finishedAt ?? i.at));
  const runs = investigations
    .filter((i) => localDate(dayOf(i), { timeZone }) === today)
    .sort((a, b) => Date.parse(a.at) - Date.parse(b.at))
    .map((i) => ({ id: i.id, at: i.at, view: investigationVerdict(i) }));
  return {
    runs,
    quiet: runs.filter((r) => r.view === "no_change").length,
    failed: runs.filter((r) => r.view === "didnt_finish").length,
    findings: runs.filter((r) => FOUND.includes(r.view)).length,
    used: usage ? usage.filter((u) => countsToday(u) && localDate(u.at, { timeZone }) === today).length : null,
  };
}

// ------------------------------------------------------------------ freshness

/** Joule reads Predbat every 5 minutes; a plan read longer ago than twice that is out of date. */
export const COLLECTION_MINUTES = 5;
/** Predbat's own re-planning interval (calculate_plan_every, minutes) from the captured settings, else null. */
export function planEvery(settings: { key: string; value: string | number | null }[] | undefined) {
  const raw = settings?.find((s) => s.key === "calculate_plan_every")?.value;
  const v = typeof raw === "number" ? raw : raw != null ? Number.parseFloat(raw) : NaN;
  return Number.isFinite(v) && v > 0 ? v : null;
}

/**
 * "Plan last updated 3 h ago – Predbat unreachable" when the plan on screen is out of date; null while it is current.
 *
 * `collectedAt` is when Joule first read this plan, so it ages normally between Predbat's re-plans: the plan is only late
 * once it is older than Predbat's re-planning interval (calculate_plan_every, 10 minutes by default) plus two collections.
 * When reading Predbat fails, it is out of date as soon as the last good read is two collections old.
 */
export function planStaleness({
  collectedAt,
  lastCollection,
  collectionError,
  everyMinutes,
  now,
}: {
  collectedAt: string | null | undefined;
  /** When Joule last read Predbat successfully. */
  lastCollection?: string | null;
  collectionError?: string | null;
  /** Predbat's calculate_plan_every, minutes. */
  everyMinutes?: number | null;
  now: number;
}) {
  const t = collectedAt ? Date.parse(collectedAt) : NaN;
  if (!Number.isFinite(t)) return null;
  const minutes = Math.floor((now - t) / MIN);
  const read = lastCollection ? Date.parse(lastCollection) : NaN;
  const unread = Number.isFinite(read) ? Math.floor((now - read) / MIN) : minutes;
  const every = finite(everyMinutes) && everyMinutes > 0 ? everyMinutes : 10;
  const late = collectionError
    ? unread > 2 * COLLECTION_MINUTES && minutes > 2 * COLLECTION_MINUTES
    : minutes > Math.max(2 * COLLECTION_MINUTES, every + 2 * COLLECTION_MINUTES);
  if (!late) return null;
  const age =
    minutes < 60
      ? `${minutes} min ago`
      : minutes < 48 * 60
        ? `${Math.floor(minutes / 60)} h ago`
        : `${Math.floor(minutes / 1440)} days ago`;
  return {
    minutes,
    label: `Plan last updated ${age} – ${collectionError ? "Predbat unreachable" : "not refreshed since"}`,
  };
}

/** Plan age for the status strip; amber when older than three of Predbat's planning intervals. */
export function planAge(planAt: string | null | undefined, now: number, everyMinutes: number | null | undefined) {
  const t = planAt ? Date.parse(planAt) : NaN;
  if (!Number.isFinite(t)) return null;
  const minutes = Math.max(0, Math.floor((now - t) / MIN));
  const every = finite(everyMinutes) && everyMinutes > 0 ? everyMinutes : 10;
  return { minutes, old: minutes > 3 * every, every };
}

/** The action of a plan slot, for callers that hold a plain slot. */
export const actionOf = (s: PlanSlotLike) => slotAction({ ...s, action: s.action ?? "" });
