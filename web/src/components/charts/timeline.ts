/**
 * The data model behind the energy timeline: measured history and Predbat's plan merged into one row per slot, energy
 * normalised to kWh per half-hour, every missing value classified, and consecutive plan actions grouped into blocks.
 * Pure functions, unit-tested in timeline.test.ts.
 */
import { planAction, slotAction, rateEstimated, rateTypeLabel, type PlanAction } from "../../lib/planActions";
import { approximate, classify, type SlotState } from "./gaps";
import { perHalfHour } from "./scale";
import { kwh, pence } from "../../lib/format";

/** A plan or history slot as the server sends it; every detail field is optional so older plans still draw. */
export interface TimelineSlot {
  time: string;
  durationMinutes: number;
  loadForecast?: number | null;
  pvForecast?: number | null;
  loadActual?: number | null;
  pvActual?: number | null;
  homeActual?: number | null;
  evActual?: number | null;
  loadActualMethod?: string | null;
  pvActualMethod?: string | null;
  carKwh?: number | null;
  socForecast?: number | null;
  socForecastEnd?: number | null;
  socActual?: number | null;
  socActualStart?: number | null;
  importRate?: number | null;
  exportRate?: number | null;
  importRateType?: string | null;
  exportRateType?: string | null;
  rateEstimated?: boolean | null;
  action?: string;
  actionKey?: string | null;
  actionId?: string | null;
  actionLabel?: string | null;
  rawAction?: string | null;
  targetPercent?: number | null;
  reasonText?: string | null;
  splitTime?: string | null;
  secondaryAction?: string | null;
  override?: string | null;
  cost?: number | null;
}

const MIN = 60000;
const startOf = (s: TimelineSlot) => Date.parse(s.time);
const endOf = (s: TimelineSlot) => startOf(s) + (s.durationMinutes || 30) * MIN;
const sum = (a: number | null | undefined, b: number | null | undefined) =>
  a == null && b == null ? null : (a ?? 0) + (b ?? 0);

/**
 * Elapsed history slots followed by the plan. The plan usually starts part-way through a half-hour (Predbat replans every
 * five minutes, so its first slot is 10:25–10:30): that slot is folded into the half-hour it belongs to, which keeps the
 * measured part from history and takes the newest forecast, action and prices from the plan. Nothing is dropped and the
 * half-hour in progress is never a hole.
 */
export function mergeTimeline(history: TimelineSlot[], plan: TimelineSlot[]): TimelineSlot[] {
  const sorted = [...plan].sort((a, b) => startOf(a) - startOf(b));
  if (!sorted.length) return [...history].sort((a, b) => startOf(a) - startOf(b));
  const planStart = startOf(sorted[0]);
  const past = history.filter((h) => startOf(h) < planStart).sort((a, b) => startOf(a) - startOf(b));
  const last = past.at(-1);
  if (!last || endOf(last) <= planStart) return [...past, ...sorted];
  // The history slot in progress overlaps the plan's first slot(s): merge them into it.
  const lastEnd = endOf(last);
  const inside = sorted.filter((p) => startOf(p) < lastEnd);
  const after = sorted.filter((p) => startOf(p) >= lastEnd);
  const elapsed = (planStart - startOf(last)) / ((last.durationMinutes || 30) * MIN);
  const newest = inside[0];
  const merged: TimelineSlot = {
    ...last,
    ...Object.fromEntries(Object.entries(newest).filter(([, v]) => v != null)),
    time: last.time,
    durationMinutes: last.durationMinutes,
    loadActual: last.loadActual,
    pvActual: last.pvActual,
    homeActual: last.homeActual,
    evActual: last.evActual,
    loadActualMethod: last.loadActualMethod,
    pvActualMethod: last.pvActualMethod,
    socActual: last.socActual,
    socActualStart: last.socActualStart,
    socForecast: last.socForecast ?? newest.socForecast,
    socForecastEnd: inside.at(-1)!.socForecastEnd ?? last.socForecastEnd,
    loadForecast: sum(
      (last.loadForecast ?? 0) * elapsed,
      inside.reduce((t, p) => t + (p.loadForecast ?? 0), 0),
    ),
    pvForecast: sum(
      (last.pvForecast ?? 0) * elapsed,
      inside.reduce((t, p) => t + (p.pvForecast ?? 0), 0),
    ),
    carKwh: inside.some((p) => p.carKwh != null) ? inside.reduce((t, p) => t + (p.carKwh ?? 0), 0) : last.carKwh,
    cost: sum(last.cost, null),
  };
  return [...past.slice(0, -1), merged, ...after];
}

export interface SeriesPoint {
  /** Measured value, kWh per half-hour (0 for a known zero); null when not drawn. */
  actual: number | null;
  /** Measured value in the slot, kWh, for the tooltip. */
  actualRaw: number | null;
  state: SlotState;
  /** ≈: the counter proved the energy but its timing was shared out. */
  approx: boolean;
  forecast: number | null;
  forecastRaw: number | null;
}

export type Tag = "free" | "saving" | "iog";
export const tagLabels: Record<Tag, string> = { free: "Free session", saving: "Saving session", iog: "IOG slot" };

export interface Row {
  index: number;
  start: number;
  end: number;
  minutes: number;
  phase: "past" | "current" | "future";
  slot: TimelineSlot;
  home: SeriesPoint;
  solar: SeriesPoint;
  ev: SeriesPoint;
  soc: {
    start: number | null;
    end: number | null;
    state: SlotState;
    forecastStart: number | null;
    forecastEnd: number | null;
  };
  price: { import: number | null; export: number | null; estimated: boolean; note?: string };
  action: PlanAction & { reason?: string; override?: string };
  tags: Tag[];
  cost: number | null;
  /** The earlier period's home use for the same local clock times, kWh per half-hour (compare). */
  earlier?: number | null;
  earlierRaw?: number | null;
}

export interface BuildOptions {
  now: number;
  /** Octopus Intelligent dispatch windows, when known. */
  dispatches?: { start: number; end: number }[];
}

const finite = (v: number | null | undefined): v is number => typeof v === "number" && Number.isFinite(v);

/** True when the car is part of the picture: measured car energy, or planned car charging. */
export function hasCar(slots: TimelineSlot[]) {
  return slots.some((s) => (finite(s.evActual) && s.evActual > 0.005) || (finite(s.carKwh) && s.carKwh > 0.005));
}

export function buildRows(
  slots: TimelineSlot[],
  { now, dispatches = [] }: BuildOptions,
): { rows: Row[]; car: boolean } {
  const car = hasCar(slots);
  const rows = slots
    .map((slot) => ({ slot, start: startOf(slot), end: endOf(slot) }))
    .filter((r) => Number.isFinite(r.start))
    .sort((a, b) => a.start - b.start)
    .map(({ slot, start, end }, index): Row => {
      const minutes = (end - start) / MIN;
      const phase = end <= now ? "past" : start <= now ? "current" : "future";
      const point = (
        raw: number | null | undefined,
        method: string | null | undefined,
        forecastRaw: number | null | undefined,
        metric: string,
      ): SeriesPoint => {
        const state =
          phase === "past" ? classify({ value: raw, end, method, forecast: forecastRaw, metric }, now) : "none";
        const actualRaw = state === "value" ? (raw as number) : state === "zero" ? 0 : null;
        return {
          actual: perHalfHour(actualRaw, minutes),
          actualRaw,
          state,
          approx: state === "value" && approximate(method),
          forecast: perHalfHour(forecastRaw, minutes),
          forecastRaw: finite(forecastRaw) ? forecastRaw : null,
        };
      };
      // With a car in the picture the home line excludes it, so the car's charging never hides the house's own use. The
      // server sends homeActual (load minus car) exactly when the load meter includes the car and both meters cover the
      // slot. Without it the load is home use as measured: either the meter never contained the car (subtracting it again
      // would read far too low), or the slot has no car reading, where the whole load beats a blank.
      const homeRaw = car && finite(slot.homeActual) ? slot.homeActual : slot.loadActual;
      const socEnd = phase === "past" && finite(slot.socActual) ? slot.socActual : null;
      const action = slotAction({ ...slot, action: slot.action ?? "" });
      const tags: Tag[] = [];
      if (finite(slot.importRate) && slot.importRate <= 0) tags.push("free");
      if (slot.importRateType === "saving" || slot.exportRateType === "saving") tags.push("saving");
      if (dispatches.some((d) => d.start < end && d.end > start)) tags.push("iog");
      const estimated =
        !!slot.rateEstimated || rateEstimated(slot.importRateType) || rateEstimated(slot.exportRateType);
      return {
        index,
        start,
        end,
        minutes,
        phase,
        slot,
        home: point(homeRaw, slot.loadActualMethod, slot.loadForecast, "home"),
        solar: point(slot.pvActual, slot.pvActualMethod, slot.pvForecast, "solar"),
        // Without a car in the picture there is nothing to measure, so a missing car reading is never a gap.
        ev: car
          ? point(slot.evActual, null, slot.carKwh ?? (phase === "future" ? 0 : null), "ev")
          : { actual: null, actualRaw: null, state: "none", approx: false, forecast: null, forecastRaw: null },
        soc: {
          start: phase !== "future" && finite(slot.socActualStart) ? slot.socActualStart : null,
          end: socEnd,
          state:
            phase === "past" ? classify({ value: slot.socActual, end: end + 15 * MIN, metric: "soc" }, now) : "none",
          forecastStart: finite(slot.socForecast) ? slot.socForecast : null,
          forecastEnd: finite(slot.socForecastEnd) ? slot.socForecastEnd : null,
        },
        price: {
          import: finite(slot.importRate) ? slot.importRate : null,
          export: finite(slot.exportRate) ? slot.exportRate : null,
          estimated,
          note: estimated
            ? (rateTypeLabel(slot.importRateType) ?? rateTypeLabel(slot.exportRateType) ?? "Predbat's estimate")
            : slot.importRateType === "saving"
              ? rateTypeLabel("saving")
              : undefined,
        },
        action,
        tags,
        cost: finite(slot.cost) ? slot.cost : null,
      };
    });
  // A forecast end level is the next slot's start level when the server did not send one.
  rows.forEach((r, i) => {
    if (r.soc.forecastEnd == null) {
      const next = rows[i + 1];
      if (next && Math.abs(next.start - r.end) < MIN) r.soc.forecastEnd = next.soc.forecastStart;
    }
  });
  return { rows, car };
}

export interface Block {
  from: number;
  to: number;
  start: number;
  end: number;
  action: Row["action"];
  tags: Tag[];
}

/** Consecutive touching slots with the same action, target and tags read as one block on the action ribbon. */
export function actionBlocks(rows: Row[]): Block[] {
  const blocks: Block[] = [];
  const sig = (r: Row) =>
    `${r.action.key}|${r.action.id}|${r.action.label}|${r.action.target ?? ""}|${r.tags.join(",")}`;
  rows.forEach((r, i) => {
    const last = blocks.at(-1);
    if (last && sig(rows[last.to]) === sig(r) && Math.abs(rows[last.to].end - r.start) < MIN) {
      last.to = i;
      last.end = r.end;
    } else blocks.push({ from: i, to: i, start: r.start, end: r.end, action: r.action, tags: r.tags });
  });
  return blocks;
}

/** Measured history from /telemetry/history keyed by slot start, for the earlier-period overlay. */
export interface HistorySlot {
  time: string;
  durationMinutes: number;
  load?: number | null;
  home?: number | null;
  homeEstimate?: number | null;
  loadEstimate?: number | null;
  pv?: number | null;
  pvEstimate?: number | null;
  ev?: number | null;
}
/**
 * The earlier period's home use by slot start, read the same way as the main line: home without the car when the server
 * derived it, otherwise the load as measured (a load meter that excludes the car has no `home`, and needs none).
 */
export function historyByStart(slots: HistorySlot[], car: boolean) {
  const map = new Map<number, number | null>();
  for (const s of slots) {
    const home = s.home ?? s.homeEstimate ?? null;
    const v = car && finite(home) ? home : (s.load ?? s.loadEstimate ?? null);
    map.set(Date.parse(s.time), finite(v) ? v : null);
  }
  return map;
}

/** Fills each elapsed row's `earlier` from the earlier period, matching by the shifted local clock time. */
export function attachEarlier(
  rows: Row[],
  map: Map<number, number | null>,
  shift: (ms: number) => number,
  now: number,
) {
  for (const r of rows) {
    if (r.start >= now) {
      r.earlier = r.earlierRaw = undefined;
      continue;
    }
    const raw = map.get(shift(r.start));
    r.earlierRaw = raw ?? null;
    r.earlier = perHalfHour(raw, r.minutes);
  }
}

const hoursText = (ms: number) => {
  const h = Math.round(ms / 3600000);
  return `${h} h`;
};
const kwhText = (v: number) => kwh(v);
const penceText = (v: number) => pence(v, { unit: "p" });

/**
 * One plain sentence describing what the timeline shows, used as the visible caption and the chart's accessible
 * description: what was measured, what Predbat plans next and where the battery goes.
 */
export function describeTimeline(
  rows: Row[],
  {
    now,
    wall,
    car,
    pastLabel,
    day,
    batteryOnly = false,
    socNow,
    describePlan,
    totals,
    asOfPlan = false,
  }: {
    now: number;
    /**
     * An earlier plan, not the live one: its battery levels are what it expected, so "now" is worded "This plan expected
     * 35% at 19:30" rather than "Battery 35% now".
     */
    asOfPlan?: boolean;
    /**
     * Describes the plan between two instants in the caller's own words ("Next 24 h: Predbat plans to charge to 100%
     * tonight 00:00–05:30 at 6.67p."), replacing the built-in window summary so the caption matches the page's windows.
     */
    describePlan?: (from: number, to: number) => string;
    /** The battery's measured level now, when fresh: "now" is the reading, not the plan's first forecast. */
    socNow?: number | null;
    wall: (ms: number) => string;
    car: boolean;
    /**
     * The local day of an instant, weekday first ("Tue 6"). Times on another day than now's read "tomorrow 05:30",
     * "yesterday 18:00" or "05:30 Tue".
     */
    day?: (ms: number) => string;
    /** Describe the battery level only (the battery outlook). */
    batteryOnly?: boolean;
    /** Replaces "Last 12 h" (e.g. "Mon 5 Oct"). */ pastLabel?: string;
    /**
     * The period's own totals (from the summary the page's headline uses), so the caption never shows a second figure for
     * the same quantity; a key left out is summed from the half-hours as before.
     */
    totals?: { home?: number | null; solar?: number | null; ev?: number | null };
  },
) {
  if (!rows.length) return "No readings or plan for this period yet.";
  /** "05:30", "tomorrow 05:30", "yesterday 18:00" or "05:30 Wed"; a range keeps the day together: "01:00–01:30 Wed". */
  const whenRange = (from: number, to?: number) => {
    const times = to == null ? wall(from) : `${wall(from)}–${wall(to)}`;
    if (!day || day(from) === day(now)) return times;
    if (day(from) === day(now + 24 * 60 * MIN)) return `tomorrow ${times}`;
    if (day(from) === day(now - 24 * 60 * MIN)) return `yesterday ${times}`;
    return `${times} ${day(from).split(" ")[0]}`;
  };
  const when = (ms: number) => whenRange(ms);
  const parts: string[] = [];
  const past = rows.filter((r) => r.phase === "past");
  if (past.length && !batteryOnly) {
    const span = past.at(-1)!.end - past[0].start;
    const total = (key: "home" | "solar" | "ev") => {
      if (totals && key in totals) return totals[key] ?? null;
      const measured = past.filter((r) => r[key].actualRaw != null);
      return measured.length ? measured.reduce((t, r) => t + (r[key].actualRaw ?? 0), 0) : null;
    };
    const home = total("home"),
      solar = total("solar"),
      ev = car ? total("ev") : null;
    const bits = [
      home != null ? `home used ${kwhText(home)}` : "",
      ev != null && ev > 0.05 ? `the car took ${kwhText(ev)}` : "",
      solar != null ? `solar made ${kwhText(solar)}` : "",
    ].filter(Boolean);
    if (bits.length) parts.push(`${pastLabel ?? `Last ${hoursText(span)}`}: ${bits.join(", ")}.`);
  }
  const future = rows.filter((r) => r.phase !== "past");
  if (future.length) {
    // Windows by kind of action only (a charge to 80% then to 100% is one charging window), in the glossary's own words.
    const windows: { key: string; start: number; end: number; from: number }[] = [];
    future.forEach((r, i) => {
      if (r.action.tone === "neutral") return;
      const last = windows.at(-1);
      if (last && last.key === r.action.key && Math.abs(last.end - r.start) < MIN) last.end = r.end;
      else windows.push({ key: r.action.key, start: r.start, end: r.end, from: i });
    });
    // The two longest windows, in time order: the overnight charge matters more than a ten-minute top-up.
    const main = [...windows]
      .sort((a, b) => b.end - b.start - (a.end - a.start) || a.start - b.start)
      .slice(0, 2)
      .sort((a, b) => a.start - b.start);
    const plans = main.map((w) => {
      const action = planAction(w.key);
      const rate = future[w.from].price[action.rate === "export" ? "export" : "import"];
      const label = action.label.charAt(0).toLowerCase() + action.label.slice(1);
      return `${label} ${whenRange(w.start, w.end)}${rate != null ? ` at ${penceText(rate)}` : ""}`;
    });
    const more = windows.length > 2 ? `, and ${windows.length - 2} more window${windows.length === 3 ? "" : "s"}` : "";
    const span = future.at(-1)!.end - Math.max(future[0].start, now);
    // Less than an hour ahead (a "Past 24 h" window ends in the half-hour in progress) is no plan to speak of.
    if (batteryOnly || span < 60 * MIN) {
      // Nothing to say about the plan.
    } else if (describePlan) {
      const text = describePlan(Math.max(future[0].start, now), future.at(-1)!.end);
      if (text) parts.push(text);
    } else
      parts.push(
        plans.length
          ? `Next ${hoursText(span)}: Predbat plans to ${plans.join("; ")}${more}.`
          : `Next ${hoursText(span)}: no charging or exporting planned.`,
      );
    // Lowest and highest are read from now on only: the half-hour in progress counts by its end level, at its end.
    const current = future.find((r) => r.start < now && r.soc.forecastStart != null);
    const ahead = future.filter((r) => r.start >= now && r.soc.forecastStart != null);
    const points: { level: number; at: number }[] = [];
    if (current?.soc.forecastEnd != null) points.push({ level: current.soc.forecastEnd, at: current.end });
    for (const r of ahead) points.push({ level: r.soc.forecastStart!, at: r.start });
    const first = current ?? ahead[0];
    if (first) {
      const startLevel = first.soc.forecastStart!;
      const head = asOfPlan
        ? `This plan expected ${Math.round(points[0]?.level ?? startLevel)}% at ${when(points[0]?.at ?? first.start)}`
        : `Battery ${Math.round(finite(socNow) ? socNow : startLevel)}% now`;
      if (ahead.length >= 2 && points.length >= 2) {
        const low = points.reduce((a, b) => (b.level < a.level ? b : a));
        const high = points.reduce((a, b) => (b.level > a.level ? b : a));
        // An earlier plan's "expected" point already names its own level: don't repeat it as the lowest or highest.
        const same = (p: { at: number }) => asOfPlan && p.at === points[0].at;
        const extremes = [
          same(low) ? "" : `lowest ${Math.round(low.level)}% at ${when(low.at)}`,
          same(high) ? "" : `highest ${Math.round(high.level)}% at ${when(high.at)}`,
        ].filter(Boolean);
        parts.push(`${[head, ...extremes].join(", ")}.`);
      } else parts.push(`${head}.`);
    }
  }
  return parts.join(" ");
}

/** The measured battery level when it was read within the last hour (and not in the future), else null. */
export function freshSoc(reading: { value: number | null; time: string } | null | undefined, now: number) {
  const t = reading?.time ? Date.parse(reading.time) : NaN;
  return reading && finite(reading.value) && Number.isFinite(t) && t <= now + MIN && now - t <= 60 * MIN
    ? reading.value
    : null;
}

/** Predbat's minimum battery reserve (set_reserve_min, %) from the captured settings, or null when unknown. */
export function reserveSetting(settings: { key: string; value: string | number | null }[] | undefined) {
  const raw = settings?.find((s) => s.key === "set_reserve_min")?.value;
  const value = typeof raw === "number" ? raw : raw != null ? Number.parseFloat(raw) : NaN;
  return Number.isFinite(value) && value >= 0 && value <= 100 ? value : null;
}
