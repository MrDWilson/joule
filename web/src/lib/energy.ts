/**
 * The Energy page's model: what each figure says, its status in plain words, notes about the period and the health of each
 * sensor. Pure functions, so every wording rule is unit-tested (energy.test.ts) and the page only lays things out.
 *
 * Rules (decisions.json): the headline net cost is import cost minus export credit, with "Paid £X · Earned £Y" under it and
 * "≈" plus the meter's name when either side is under 95 % priced; "% measured" never appears at 98 % or above; a meter's own
 * day total is the check ("Matches Home Assistant"); expected idle (solar overnight, a charger between sessions) is not a fault.
 */
import type { EnergyGap, EnergyMetric, EnergySummary, LatestReading, TelemetryStatus } from "../completion-types";
import type { Tone } from "../components/ui/Chip";
import { gbp, kwh, number, pence, percent } from "./format";
import { ago, clock, dayLabel, dayTime, localDate } from "./time";
import { metricLabel, sourceLabel } from "./labels";

/** The energy meters, in the order of the tiles and the daily bars. */
export const ENERGY_METRICS = ["load", "pv", "grid_import", "grid_export", "battery_charge", "battery_discharge", "ev"];
/** Every sensor, in a fixed order (so rows never shuffle between loads). */
export const SENSOR_ORDER = [
  ...ENERGY_METRICS,
  "soc",
  "import_tariff",
  "export_tariff",
  "standing_charge",
  "intelligent_slots",
];
/** The compact chips under the two headline figures. */
export const CHIP_METRICS = ["pv", "grid_import", "grid_export", "battery_charge", "battery_discharge", "ev"];

/** Each metric's series colour (the same as the charts: charts/theme.ts). */
export const metricColor: Record<string, string> = {
  load: "var(--load)",
  home: "var(--load)",
  pv: "var(--solar)",
  grid_import: "var(--series-grid, #d55181)",
  grid_export: "var(--series-grid, #d55181)",
  battery_charge: "var(--battery)",
  battery_discharge: "var(--battery)",
  ev: "var(--ev)",
};

/** The meter's name in a sentence: "Export meter offline 00:00–02:47". */
const meterNames: Record<string, string> = {
  load: "Home-use meter",
  pv: "Solar meter",
  grid_import: "Import meter",
  grid_export: "Export meter",
  battery_charge: "Battery charge meter",
  battery_discharge: "Battery discharge meter",
  ev: "Car charger meter",
};
export const meterName = (metric: string) => meterNames[metric] ?? `${metricLabel(metric)} meter`;

const MIN_GAP_MS = 5 * 60000;
const finite = (n: unknown): n is number => typeof n === "number" && Number.isFinite(n);
const span = (g: { from: string; to: string }) => Date.parse(g.to) - Date.parse(g.from);

interface Options {
  timeZone?: string;
  now?: number;
  /** The period spans several days, so times need their day. */
  multiDay?: boolean;
}

/** "00:00–02:47", with the day when the range isn't within one day of the period ("Sat 3 Oct, 23:10 – Sun 4 Oct, 01:00"). */
export function gapRange(from: string, to: string, options: Options & { withDay?: boolean } = {}) {
  const { timeZone } = options;
  const a = new Date(from),
    b = new Date(to);
  const sameDay = dayLabel(a, { timeZone }) === dayLabel(new Date(b.getTime() - 1), { timeZone });
  const endText = (() => {
    const t = clock(b, { timeZone });
    return t === "00:00" && b > a ? "24:00" : t;
  })();
  if (!options.withDay && sameDay) return `${clock(a, { timeZone })}–${endText}`;
  if (sameDay) return `${dayLabel(a, { timeZone })}, ${clock(a, { timeZone })}–${endText}`;
  return `${dayLabel(a, { timeZone })}, ${clock(a, { timeZone })} – ${dayLabel(b, { timeZone })}, ${clock(b, { timeZone })}`;
}

/** Gaps long enough to mention (five minutes or more), longest first. */
export function notableGaps(m: EnergyMetric | null | undefined): EnergyGap[] {
  return (m?.gaps ?? []).filter((g) => span(g) >= MIN_GAP_MS).sort((a, b) => span(b) - span(a));
}

const gapWords: Record<string, string> = {
  offline: "offline",
  idle: "reported nothing",
  not_found: "not found",
  invalid: "gave unreadable readings",
  no_samples: "not read",
  reset: "reset unexpectedly",
  source_changed: "changed sensor",
};
export const gapWord = (reason: string) => gapWords[reason] ?? "not read";

// ------------------------------------------------------------------ headline figures

export interface NetCost {
  /** "Net cost", or "Net earnings" when export earned more than import cost. */
  label: string;
  /** "£1.10", "≈ £1.10" or "—". */
  text: string;
  value: number | null;
  approximate: boolean;
  earnings: boolean;
  /** "Paid £1.80 · Earned £0.70" pieces. */
  paid: string | null;
  earned: string | null;
  /** Why the figure is approximate, naming the meter: "Export meter offline 00:00–02:47". */
  note: string | null;
  /** The standing charge for the period, whether or not it is in `value`. */
  standing: StandingChargeView | null;
}

/** The standing charge for a window, as the pages show it. */
export interface StandingChargeView {
  /** £ for the window. */
  amount: number;
  /** "£0.54". */
  text: string;
  /** "£0.54/day". */
  rate: string;
  /** In the headline net cost (the owner's choice; the default). */
  included: boolean;
  source: "sensor" | "manual";
  assumed: boolean;
}

/** The window's standing charge, or null when Joule doesn't know the rate. */
export function standingCharge(s: EnergySummary | null | undefined): StandingChargeView | null {
  const amount = s?.standingChargeGbp,
    rate = s?.standingChargePencePerDay;
  if (!finite(amount) || !finite(rate)) return null;
  return {
    amount,
    text: gbp(amount),
    rate: `${gbp(rate / 100)}/day`,
    included: s?.standingChargeIncluded !== false,
    source: s?.standingChargeSource === "manual" ? "manual" : "sensor",
    assumed: !!s?.standingChargeAssumed,
  };
}

/** The energy net cost (import cost − export credit), plus the standing charge when the owner includes it. */
export function headlineNet(s: EnergySummary | null | undefined): number | null {
  if (!s) return null;
  const paid = s.importCostGbp,
    earned = s.exportCreditGbp;
  const energy =
    s.netCostGbp !== undefined
      ? s.netCostGbp
      : finite(paid) || finite(earned)
        ? (paid ?? 0) - (earned ?? 0)
        : s.observedNetCostGbp;
  if (!finite(energy)) return null;
  const standing = standingCharge(s);
  return standing?.included ? energy + standing.amount : energy;
}

/** One line for the foot of a page: what the costs include. */
export function standingChargeNote(s: EnergySummary | null | undefined) {
  const standing = standingCharge(s);
  if (!standing) return "Costs leave out the standing charge: set it in Setup › Sensors.";
  return standing.included
    ? `Costs include the standing charge (${standing.rate}).`
    : `Costs leave out the standing charge (${standing.rate}), as chosen in Setup › Sensors.`;
}

/** The share of either side priced below which the headline net cost reads "≈". */
export const PRICED_ENOUGH = 0.95;

export function netCost(s: EnergySummary | null | undefined, options: Options = {}): NetCost {
  const paid = s?.importCostGbp ?? null,
    earned = s?.exportCreditGbp ?? null;
  let value: number | null;
  if (!s) value = null;
  else if (s.netCostGbp !== undefined) value = s.netCostGbp;
  else value = finite(paid) && finite(earned) ? paid - earned : s.observedNetCostGbp;
  const standing = standingCharge(s);
  if (finite(value) && standing?.included) value += standing.amount;
  const importCov = s?.importCostCoverage ?? s?.costCoverageFraction ?? 0,
    exportCov = s?.exportCostCoverage ?? s?.costCoverageFraction ?? 0;
  const gross = Math.abs(paid ?? 0) + Math.abs(earned ?? 0);
  const estimated = (s?.estimatedCostGbp ?? 0) > 0.05 * Math.max(0.01, gross);
  const weakSide = importCov < PRICED_ENOUGH ? "grid_import" : exportCov < PRICED_ENOUGH ? "grid_export" : null;
  const approximate = finite(value) && (!!weakSide || estimated);
  let note: string | null = null;
  if (finite(value) && weakSide && s) {
    const gap = notableGaps(s.metrics[weakSide])[0];
    note = gap
      ? `${meterName(weakSide)} ${gapWord(gap.reason)} ${gapRange(gap.from, gap.to, options)}`
      : `${meterName(weakSide)} only partly priced`;
  } else if (approximate) note = "Part of this is estimated from a short outage";
  const earnings = finite(value) && value < -0.005;
  const amount = finite(value) ? gbp(Math.abs(value)) : "—";
  return {
    label: earnings ? "Net earnings" : "Net cost",
    text: finite(value) ? `${approximate ? "≈ " : ""}${amount}` : "—",
    value: finite(value) ? value : null,
    approximate,
    earnings,
    paid: finite(paid) ? gbp(paid) : null,
    earned: finite(earned) ? gbp(earned) : null,
    note,
    standing,
  };
}

export interface HomeUse {
  value: number | null;
  text: string;
  /** "16.9 kWh including the car" when the house meter includes the car's charging. */
  note: string | null;
  metric: EnergyMetric | null;
}
/** Home use without the car (what Predbat forecasts), with the house meter's own figure underneath when they differ. */
export function homeUse(s: EnergySummary | null | undefined): HomeUse {
  const load = s?.metrics.load ?? null;
  const home = s?.home && s.loadIncludesEv ? s.home : null;
  const metric = home ?? load;
  const value = metric?.energyKwh ?? null;
  const ev = s?.metrics.ev?.energyKwh ?? 0;
  // Built from the two figures as shown (12.3 + 4.6 = 16.9), so the rounded numbers on the page always add up.
  const shown = (n: number) => Math.round(n * 10) / 10;
  const note =
    home && finite(value) && finite(load?.energyKwh) && ev > 0.05
      ? `${kwh(shown(value) + shown(ev))} including the car`
      : null;
  return { value, text: kwh(value), note, metric };
}

// ------------------------------------------------------------------ per-metric status

export interface MetricStatus {
  label: string;
  tone: Tone;
  /** The fuller explanation for the chip's title. */
  title: string;
}
const ESTIMATED_SHARE = 0.05;

/**
 * True when enough of the figure is estimated to say "≈": a short outage (up to 2 h) whose energy the meter proved was spread
 * across it. A partial figure is not "≈": a longer outage is left out, so the figure is a floor, and its status chip says
 * which hours are missing.
 */
export function isApproximate(m: EnergyMetric | null | undefined) {
  if (!m || !finite(m.energyKwh)) return false;
  if (m.state === "estimated" && finite(m.estimatedKwh))
    return Math.abs(m.estimatedKwh) > ESTIMATED_SHARE * Math.max(0.1, Math.abs(m.energyKwh));
  return false;
}

/**
 * A status chip for a figure only when something is worth saying: "Missing 00:00–02:47", "≈ estimated", "No records",
 * "Sensor offline". Complete figures (including solar asleep overnight) say nothing; "% measured" only below 98 % and only
 * for servers that don't send a state.
 */
export function metricStatus(
  metric: string,
  m: EnergyMetric | null | undefined,
  options: Options = {},
): MetricStatus | null {
  if (!m) return { label: "Not set up", tone: "neutral", title: `${meterName(metric)} isn't mapped yet.` };
  const gaps = notableGaps(m);
  const describe = (g: EnergyGap) =>
    `${gapWord(g.reason)} ${gapRange(g.from, g.to, { ...options, withDay: !!options.multiDay })}`;
  const gapList = gaps
    .slice(0, 4)
    .map((g) => `${meterName(metric)} ${describe(g)}.`)
    .join(" ");
  if (!finite(m.energyKwh)) {
    if (m.state === "no_records")
      return { label: "No records", tone: "neutral", title: "There are no readings for these dates." };
    const offline = gaps.find((g) => g.reason === "offline");
    return {
      label: offline ? "Sensor offline" : "Not measured",
      tone: "warn",
      title: gapList || `${meterName(metric)} has no readings for this period.`,
    };
  }
  if (m.state === "partial") {
    const g = gaps[0];
    const ranges = new Set(gaps.map((x) => gapRange(x.from, x.to, options)));
    const days = new Set(gaps.map((x) => dayLabel(new Date(x.from), options)));
    const where = !g
      ? null
      : gaps.length === 1
        ? gapRange(g.from, g.to, { ...options, withDay: !!options.multiDay })
        : ranges.size === 1 && days.size === gaps.length
          ? `${[...ranges][0]} on ${gaps.length} days`
          : null;
    return {
      label: where ? `Missing ${where}` : g ? `Missing ${gaps.length} stretches` : "Partly measured",
      tone: "warn",
      title: `${gapList || `${meterName(metric)} missed part of this period.`} That time counts as missing, never as zero.`,
    };
  }
  if (isApproximate(m))
    return {
      label: "≈ estimated",
      tone: "neutral",
      title: `${kwh(m.estimatedKwh)} of this was spread across a short outage: the meter proved it, but not exactly when.`,
    };
  if (!m.state && m.coverageFraction < 0.98)
    return {
      label: `${percent(m.coverageFraction, { fraction: true, round: "floor" })} measured`,
      tone: "warn",
      title: "Part of this period has no readings. That time counts as missing, never as zero.",
    };
  return null;
}

/** "≈ 6.3 kWh" when partly estimated, otherwise "6.3 kWh" (a partial figure's chip names the missing hours); "—" when missing. */
export function metricValue(m: EnergyMetric | null | undefined) {
  if (!m || !finite(m.energyKwh)) return "—";
  return `${isApproximate(m) ? "≈ " : ""}${kwh(m.energyKwh)}`;
}

export interface Reconciliation {
  /** Meters whose own day total matches the figure (within 0.05 kWh). */
  matched: string[];
  /** Meters whose total differs, with why: { metric, meter: "36.4 kWh", reason: "readings began 14:59" }. */
  differ: { metric: string; meter: string; measured: string; reason: string }[];
}
/** For a single day: does each daily meter's own total match the figure shown? */
export function reconcile(s: EnergySummary | null | undefined, options: Options = {}): Reconciliation {
  const out: Reconciliation = { matched: [], differ: [] };
  if (!s) return out;
  for (const metric of ENERGY_METRICS) {
    const m = s.metrics[metric];
    if (!m || !finite(m.counterDayTotalKwh) || m.reconciled == null) continue;
    if (m.reconciled) {
      out.matched.push(metric);
      continue;
    }
    const began =
      m.coverageFrom && Date.parse(m.coverageFrom) - Date.parse(s.from) > MIN_GAP_MS ? m.coverageFrom : null;
    const gap = notableGaps(m)[0];
    const reason = began
      ? `readings began ${clock(began, options)}`
      : gap
        ? `${gapWord(gap.reason)} ${gapRange(gap.from, gap.to, options)}`
        : "the meter and the readings disagree";
    out.differ.push({ metric, meter: kwh(m.counterDayTotalKwh), measured: kwh(m.energyKwh), reason });
  }
  return out;
}

/**
 * Why the Home use figure needs a second look, in one line, or null: the missing hours ("Missing 11:00–13:30 on 7 days"), a
 * short outage spread across ("Part of this is estimated from a short outage") and, for a single day, the house meter's own
 * total when it disagrees ("House meter says 18.2 kWh"). Home use has no chip of its own, so this sits under the figure.
 */
export function homeStatus(s: EnergySummary | null | undefined, options: Options & { singleDay?: boolean } = {}) {
  if (!s) return null;
  const { metric } = homeUse(s);
  if (!metric || !finite(metric.energyKwh)) return null;
  const bits: string[] = [];
  const own = metricStatus("load", metric, options);
  // The derived home figure may carry no gaps of its own; the house meter's gaps then name the hours.
  const load = metric !== s.metrics.load ? metricStatus("load", s.metrics.load, options) : null;
  const status = own && /^(Partly measured|Missing \d+ stretches)$/.test(own.label) && load ? load : own;
  if (status)
    bits.push(status.label === "≈ estimated" ? "Part of this is estimated from a short outage" : status.label);
  if (options.singleDay) {
    const differ = reconcile(s, options).differ.find((d) => d.metric === "load");
    if (differ) bits.push(`House meter says ${differ.meter}`);
  }
  return bits.length ? bits.join(" · ") : null;
}

// ------------------------------------------------------------------ comparisons

export interface Change {
  up: boolean;
  /** "Same" when the change is too small to matter, otherwise the size and its word: "£5.99 better", "6.0 kWh more". */
  text: string;
  /** The size alone: "£5.99", "6.0 kWh". */
  amount: string;
  /** Money reads "better" or "worse"; energy reads "more" or "less". Empty when the same. */
  word: string;
  /** " (+12%)", or "" when a percentage would mislead (a tiny earlier figure, a change over 200 %, a sign flip). */
  pct: string;
  /** good / bad / neutral, from which direction is good news for this figure. */
  tone: "good" | "bad" | "neutral";
  /** The full sentence for assistive tech and titles: "1.2 kWh more than the previous 7 days (12.3 kWh)". */
  title: string;
}
const betterWhen: Record<string, "higher" | "lower"> = {
  cost: "lower",
  pv: "higher",
  grid_import: "lower",
  grid_export: "higher",
};

/**
 * The change against the earlier period, or null when either side is missing, under 95 % measured, or too small to matter.
 * Never a sentence per tile about why not: the page says once when there's nothing to compare with.
 */
export function change(
  key: string,
  current: number | null | undefined,
  previous: number | null | undefined,
  label: string,
  options: { currentCoverage?: number; previousCoverage?: number; money?: boolean } = {},
): Change | null {
  if (!finite(current) || !finite(previous)) return null;
  if ((options.currentCoverage ?? 1) < 0.95 || (options.previousCoverage ?? 1) < 0.95) return null;
  const diff = current - previous;
  const scale = Math.max(Math.abs(current), Math.abs(previous));
  const fmt = (n: number) => (options.money ? gbp(n) : kwh(n));
  if (scale < 1e-9 || Math.abs(diff) < Math.max(scale * 0.01, options.money ? 0.005 : 0.05))
    return {
      up: false,
      text: "Same",
      amount: fmt(0),
      word: "",
      pct: "",
      tone: "neutral",
      title: `About the same as ${label} (${fmt(previous)})`,
    };
  const up = diff > 0;
  const better = betterWhen[key];
  const tone = !better ? "neutral" : up === (better === "higher") ? "good" : "bad";
  // Money says whether it went your way ("£5.99 better"), never a bare "▼ £5.99" that reads as a loss.
  const word = options.money ? (tone === "bad" ? "worse" : "better") : up ? "more" : "less";
  const share = (diff / previous) * 100;
  // A percentage of a tiny earlier figure (under £1 or 1 kWh), of a sign flip, or over 200 % says nothing useful.
  const pct =
    previous >= 1 && current >= 0 && Math.abs(share) <= 200 ? ` (${up ? "+" : "−"}${number(Math.abs(share), 0)}%)` : "";
  const amount = fmt(Math.abs(diff));
  return {
    up,
    text: `${amount} ${word}${pct}`,
    amount,
    word,
    pct,
    tone,
    title: `${amount} ${word} than ${label} (${fmt(previous)})`,
  };
}

// ------------------------------------------------------------------ notes about the period

/**
 * Specific notes for this period, built from the meters' own gaps: "Records start Fri 2 Oct, 14:59", "Export meter offline
 * 00:00–02:47 · 0.62 kWh recovered when it came back", "Import 01:00–02:00 couldn't be priced". Same-range gaps on several
 * meters are one note. At most `max` notes; the rest are counted.
 */
export function periodNotes(s: EnergySummary | null | undefined, options: Options & { max?: number } = {}) {
  if (!s) return { notes: [] as string[], more: 0 };
  const notes: string[] = [];
  const multiDay = Date.parse(s.to) - Date.parse(s.from) > 26 * 3600000;
  const starts = Object.values(s.metrics)
    .filter((m) => finite(m.energyKwh) && m.coverageFrom)
    .map((m) => Date.parse(m.coverageFrom!));
  const firstRecord = starts.length ? Math.min(...starts) : NaN;
  if (Number.isFinite(firstRecord) && firstRecord - Date.parse(s.from) > MIN_GAP_MS)
    notes.push(`Records start ${dayTime(firstRecord, options)}; earlier energy isn't counted.`);
  const groups = new Map<string, { metrics: string[]; gap: EnergyGap; known: number }>();
  for (const metric of ENERGY_METRICS)
    for (const g of notableGaps(s.metrics[metric])) {
      const key = `${g.from}|${g.to}|${g.reason}`;
      const group = groups.get(key) ?? { metrics: [], gap: g, known: 0 };
      group.metrics.push(metric);
      group.known += g.knownKwh ?? 0;
      groups.set(key, group);
    }
  const sorted = [...groups.values()].sort((a, b) => Date.parse(a.gap.from) - Date.parse(b.gap.from));
  // The same stretch on several days ("11:00–13:30 every day") is one note, not one per day.
  const recurring = new Map<string, { who: string; gap: EnergyGap; known: number; days: number }>();
  for (const { metrics, gap, known } of sorted) {
    const who =
      metrics.length === 1
        ? meterName(metrics[0])
        : `${metrics
            .map((m) => meterName(m).replace(/ meter$/, ""))
            .join(", ")
            .replace(/, ([^,]*)$/, " and $1")} meters`;
    const sameDay = dayLabel(new Date(gap.from), options) === dayLabel(new Date(Date.parse(gap.to) - 1), options);
    const key = sameDay
      ? `${who}|${gap.reason}|${gapRange(gap.from, gap.to, options)}`
      : `${who}|${gap.from}|${gap.to}`;
    const r = recurring.get(key) ?? { who, gap, known: 0, days: 0 };
    r.known += known;
    r.days += 1;
    recurring.set(key, r);
  }
  for (const { who, gap, known, days } of recurring.values()) {
    const range =
      days > 1
        ? `${gapRange(gap.from, gap.to, options)} on ${days} days`
        : gapRange(gap.from, gap.to, { ...options, withDay: multiDay });
    // The counter proves this energy flowed, but not when, so it stays out of the totals. Only one meter's kWh is meaningful.
    const recovered =
      known > 0.05 && !who.endsWith("meters")
        ? ` · ${kwh(known, { precision: "table" })} in that time isn't counted`
        : "";
    notes.push(`${who} ${gapWord(gap.reason)} ${range}${recovered}.`);
  }
  for (const c of s.costGaps ?? []) {
    if (c.reason === "no_samples" || span(c) < MIN_GAP_MS) continue;
    if (groups.has(`${c.from}|${c.to}|${c.reason}`)) continue;
    notes.push(
      `${metricLabel(c.metric)} ${gapRange(c.from, c.to, { ...options, withDay: multiDay })} couldn't be priced (no tariff reading).`,
    );
  }
  const max = options.max ?? 4;
  return { notes: notes.slice(0, max), more: Math.max(0, notes.length - max) };
}

// ------------------------------------------------------------------ sensors

export type SensorState = "live" | "idle" | "asleep" | "offline" | "not-found" | "not-set-up" | "unreadable" | "stale";
const sensorStates: Record<SensorState, { label: string; tone: Tone; ok: boolean }> = {
  live: { label: "Live", tone: "success", ok: true },
  idle: { label: "Idle", tone: "neutral", ok: true },
  asleep: { label: "Asleep", tone: "neutral", ok: true },
  offline: { label: "Offline", tone: "warn", ok: false },
  "not-found": { label: "Not found", tone: "danger", ok: false },
  "not-set-up": { label: "Not set up", tone: "neutral", ok: false },
  unreadable: { label: "Can't read", tone: "danger", ok: false },
  stale: { label: "Not updating", tone: "warn", ok: false },
};

export interface SensorRow {
  key: string;
  label: string;
  state: SensorState;
  stateLabel: string;
  tone: Tone;
  ok: boolean;
  /** "6.33 kWh", "84%", "31.73p/kWh", "Not charging", "None now". */
  value: string;
  /** A plain explanation when the state needs one ("Not charging (the charger reports unknown between sessions)."). */
  note: string | null;
  /** "changed 15:41", plus "· checked 20 min ago" only when this sensor was read at a different time from the rest. */
  when: string;
  entity: string | null;
  source: string | null;
  raw: string | null;
  profile: string | null;
}

const profileLabels: Record<string, string> = {
  daily_counter: "Daily total (resets at midnight)",
  session_counter: "Per charging session",
  solar_daily: "Daily solar total",
  lifetime_counter: "Lifetime total",
  price: "Price",
  state: "Live value",
};

/** What a working sensor's reading is, in place of "Live". */
const liveLabels: Record<string, string> = {
  daily_counter: "Daily total",
  solar_daily: "Daily total",
  session_counter: "This charging session",
  lifetime_counter: "Meter total",
  price: "Current rate",
  state: "Now",
};
const DAILY_PROFILES = ["daily_counter", "solar_daily"];

/**
 * A sensor's name. The house meter is not "Home use": that figure (above) leaves out the car and covers the period, while
 * the meter reads the whole house, car included when it is on the same circuit.
 */
export function sensorLabel(key: string, status: Pick<TelemetryStatus, "loadIncludesEv"> | null | undefined) {
  if (key === "load") return status?.loadIncludesEv ? "Home use incl. car" : "House meter";
  return metricLabel(key);
}

function readingValue(key: string, r: LatestReading) {
  const v = r.value;
  if (key === "intelligent_slots") {
    const on = /^(on|true|1)$/i.test(r.rawState ?? "");
    return on ? "Active now" : "None now";
  }
  if (!finite(v)) return null;
  if (key === "soc" || r.unit === "%") return percent(v);
  if (/p\/kWh/.test(r.unit)) return pence(v);
  if (r.unit === "p/day") return `${pence(v, { unit: "p" })}/day`;
  // An offline reading has no unit of its own; energy meters are stored in kWh.
  if (r.unit === "kWh" || (!r.unit && ENERGY_METRICS.includes(key))) return kwh(v, { precision: "table" });
  return `${number(v, 2)} ${r.unit}`.trim();
}

/** One row per sensor, in a fixed order, with its state in plain words. */
export function sensorRows(status: TelemetryStatus | null | undefined, options: Options = {}): SensorRow[] {
  // The strip's headline already says when the sensors were checked; a row repeats it only when it differs.
  const lastPoll = status?.lastCollection ? Date.parse(status.lastCollection) : NaN;
  if (!status) return [];
  const keys = Array.from(
    new Set([
      ...Object.keys(status.entityMappings || {}),
      ...Object.keys(status.latestReadings || {}),
      ...(status.missingMappings || []),
    ]),
  ).sort((a, b) => {
    const ia = SENSOR_ORDER.indexOf(a),
      ib = SENSOR_ORDER.indexOf(b);
    return (ia < 0 ? 99 : ia) - (ib < 0 ? 99 : ib) || a.localeCompare(b);
  });
  const now = options.now ?? Date.now();
  const rows = keys.map((key) => {
    const r = status.latestReadings?.[key];
    const entity = status.entityMappings?.[key] || r?.entityId || null;
    const profile = r?.profile ?? status.profiles?.[key] ?? null;
    let state: SensorState;
    let value: string;
    let note: string | null = null;
    const last = r && finite(r.lastObservedValue) ? readingValue(key, { ...r, value: r.lastObservedValue }) : null;
    const lastAt = r?.lastObservedAt ? dayTime(r.lastObservedAt, { ...options, now }) : null;
    if (!r) {
      state = entity || status.demo ? "offline" : "not-set-up";
      value = "—";
      note = entity ? "No reading yet." : "Map this sensor in Setup › Sensors to include it.";
    } else if (r.status === "not_found") {
      state = "not-found";
      value = "—";
      note = "Home Assistant has no sensor with this name.";
    } else if (r.status === "unavailable") {
      state = "offline";
      value = last ?? "—";
      const since = r.lastObservedAt ? ` since ${dayTime(r.lastObservedAt, { ...options, now })}` : "";
      note = r.reason ?? `Offline${since}. That time counts as missing, never as zero.`;
      if (last && lastAt && !r.reason) note = `Offline${since} · last reading ${last}.`;
    } else if (r.status === "idle") {
      const solar = (r.profile ?? status.profiles?.[key]) === "solar_daily" || key === "pv";
      state = r.expected === false ? "offline" : solar ? "asleep" : "idle";
      value = key === "ev" ? "Not charging" : solar ? "No solar now" : (last ?? "No reading yet today");
      note = r.reason ?? (key === "ev" ? "Not charging (the charger reports unknown between sessions)." : null);
    } else if (r.status === "invalid" || r.status === "unsupported_unit") {
      state = "unreadable";
      value = last ?? "—";
      note =
        r.status === "unsupported_unit"
          ? `Joule can't use the unit ${r.rawUnit || "it reports"}.`
          : "The sensor's state isn't a number.";
    } else {
      state = r.stale ? "stale" : "live";
      value = readingValue(key, r) ?? (r.rawState ? r.rawState.slice(0, 60) : "Data received");
      // A daily counter's reading is today's running total, not the period's figure: say so ("20.40 kWh today").
      if (DAILY_PROFILES.includes(profile ?? "") && / kWh$/.test(value)) value = `${value} today`;
      if (r.stale) note = `No new reading since ${dayTime(r.sourceUpdatedAt ?? r.time, { ...options, now })}.`;
    }
    const s = sensorStates[state];
    // A working sensor says what its reading is ("Meter total", "Current rate") rather than a bare "Live".
    const stateLabel = state === "live" ? (profile && liveLabels[profile]) || s.label : s.label;
    const changed = r?.sourceUpdatedAt
      ? `changed ${dayTime(r.sourceUpdatedAt, { ...options, now }).replace(/^Today /, "")}`
      : null;
    // Readings within one poll (5 min) of the last collection count as the same check; the demo writes aligned times.
    const differs = r?.time && !(Math.abs(Date.parse(r.time) - lastPoll) < 10 * 60000);
    const checked = r?.time && differs ? `checked ${ago(r.time, { ...options, now })}` : null;
    return {
      key,
      label: sensorLabel(key, status),
      state,
      stateLabel,
      tone: s.tone,
      ok: s.ok,
      value,
      note,
      when: "",
      entity,
      source: r?.source ? sourceLabel(r.source) : null,
      raw: r ? `${r.rawState ?? ""}${r.rawUnit ? ` ${r.rawUnit}` : ""}`.trim() || null : null,
      profile: (r && profile && profileLabels[profile]) || null,
      changed,
      checked,
    };
  });
  // "changed 17:55" on row after row says nothing: a time shared by three or more sensors is left off; the odd one out keeps it.
  const shared = new Map<string, number>();
  for (const r of rows) if (r.changed) shared.set(r.changed, (shared.get(r.changed) ?? 0) + 1);
  return rows.map(({ changed, checked, ...row }) => ({
    ...row,
    when: [changed && (shared.get(changed) ?? 0) < 3 ? changed : null, checked].filter(Boolean).join(" · "),
  }));
}

/** "All 11 sensors OK · checked 2 min ago", or "2 of 11 sensors need a look · checked 2 min ago". */
export function sensorHeadline(rows: SensorRow[], lastCollection: string | null | undefined, options: Options = {}) {
  const bad = rows.filter((r) => !r.ok).length;
  const checked = lastCollection ? ` · checked ${ago(lastCollection, options)}` : "";
  if (!rows.length) return "No sensors set up yet";
  if (!bad) return `All ${rows.length} sensors OK${checked}`;
  return `${bad} of ${rows.length} sensors need a look${checked}`;
}

// ------------------------------------------------------------------ reports

const isPartialDay = (r: { from: string; to: string }, timeZone?: string) => {
  const from = new Date(r.from),
    to = new Date(r.to);
  return (
    clock(from, { timeZone }) === "00:00" &&
    clock(to, { timeZone }) !== "00:00" &&
    dayLabel(from, { timeZone }) === dayLabel(to, { timeZone })
  );
};

/**
 * Mirrors ReportService.Title: "Daily report · Sun 4 Oct", "Weekly report · 28 Sep – 4 Oct", and a snapshot of part of a
 * day by its date and end time ("Report · Mon 5 Oct, to 09:53"), which stays true tomorrow (never "So far today").
 */
export function reportTitle(
  r: { kind: string; from: string; to: string; createdAt?: string },
  options: Options = {},
): string {
  const { timeZone } = options;
  const now = options.now ?? Date.now();
  const from = new Date(r.from),
    to = new Date(r.to);
  const atMidnight = (d: Date) => clock(d, { timeZone }) === "00:00";
  const fromDay = dayLabel(from, { timeZone, now });
  const lastDay = dayLabel(new Date(to.getTime() - 1), { timeZone, now });
  const short = (d: Date) => dayLabel(d, { timeZone, now }).replace(/^\w{3} /, "");
  const prefix = r.kind === "Daily" ? "Daily report" : r.kind === "Weekly" ? "Weekly report" : "Report";
  if (isPartialDay(r, timeZone)) return `Report · ${fromDay}, to ${clock(to, { timeZone })}`;
  if (atMidnight(from) && atMidnight(to))
    return fromDay === lastDay
      ? `${prefix} · ${fromDay}`
      : `${prefix} · ${short(from)} – ${short(new Date(to.getTime() - 1))}`;
  return `${prefix} · ${gapRange(r.from, r.to, { timeZone, withDay: true })}`;
}

/**
 * How a report came to be, under its title: "arrived Today 08:00" (delivered on schedule), "saved Today 09:53" (saved
 * from the figures) or "made Today 19:09" (a day filled in later, when daily reports were turned on).
 */
export function reportMeta(
  r: { kind: string; from: string; to: string; createdAt: string },
  hourLocal: number,
  options: Options = {},
) {
  const { timeZone } = options;
  const at = dayTime(r.createdAt, options);
  if (r.kind === "Custom" || isPartialDay(r, timeZone)) return `saved ${at}`;
  const hour = Number(clock(r.createdAt, { timeZone }).slice(0, 2));
  const onTime = hour === hourLocal && localDate(r.createdAt, { timeZone }) === localDate(r.to, { timeZone });
  return onTime ? `arrived ${at}` : `made ${at}`;
}

/** True when a saved report has nothing in it (no readings at all), so the list can leave it out. */
export function isEmptyReport(r: { summary: string; energySummary?: EnergySummary | null; textVersion?: number }) {
  if (/No meter readings for this period\.$/.test(r.summary)) return true;
  if ((r.textVersion ?? 0) < 2 && r.energySummary)
    return !Object.values(r.energySummary.metrics).some((m) => finite(m.energyKwh) && m.coverageFraction > 0);
  return (r.textVersion ?? 0) < 2 && /^No authoritative energy intervals/.test(r.summary);
}
