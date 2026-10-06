/**
 * "What happened": the last 24 hours of Predbat's plan of record (for each half-hour, the plan made just before it
 * started) against what the meters measured. One actual figure per quantity (≈ where the server estimated the timing),
 * battery start → end planned and measured, grid in and out, the car, and a flag where the outcome contradicts the plan.
 * Pure functions, unit-tested in history.test.ts.
 */
import type { PlanEvidence } from "../../completion-types";
import { kwh } from "../../lib/format";
import { clock } from "../../lib/time";
import { planWindows, type PlanSlotLike, type PlanWindow } from "./windows";

const MIN = 60000;
const finite = (v: number | null | undefined): v is number => typeof v === "number" && Number.isFinite(v);
const startOf = (s: { time: string }) => Date.parse(s.time);

export interface Measured {
  value: number | null;
  /** The server shared the energy out by time (the meter's readings straddle the half-hour): shown as ≈. */
  approx: boolean;
}
/**
 * Why the battery fell during a charge, when the half-hour says: "car" when the car took more than 0.5 kWh, "pause" when
 * Predbat itself planned the level to hold or fall in that half-hour. Null when nothing explains it.
 */
export type FlagCause = "car" | "pause" | null;
export const FELL_DURING_CHARGE = "Battery fell during a charge";

export interface HistoryRow {
  start: number;
  end: number;
  slot: PlanSlotLike;
  soc: { plannedStart: number | null; plannedEnd: number | null; start: number | null; end: number | null };
  home: { forecast: number | null; actual: Measured };
  solar: { forecast: number | null; actual: Measured };
  gridIn: number | null;
  gridOut: number | null;
  car: number | null;
  flags: string[];
  /** The cause of the "Battery fell during a charge" flag (null when that flag isn't raised or nothing explains it). */
  cause: FlagCause;
}
export interface HistoryWindow {
  window: PlanWindow;
  rows: HistoryRow[];
  soc: HistoryRow["soc"];
  home: { forecast: number | null; actual: Measured };
  solar: { forecast: number | null; actual: Measured };
  gridIn: number | null;
  gridOut: number | null;
  car: number | null;
  flags: string[];
}

type EvidenceSlot = PlanEvidence["slots"][number];
const metric = (e: EvidenceSlot | undefined, key: string) => {
  const m = e?.actual.metrics[key];
  return m && finite(m.energyKwh) && m.coverageFraction >= 0.9 ? m.energyKwh : null;
};
const measured = (value: number | null | undefined, method: string | null | undefined): Measured => ({
  value: finite(value) ? value : null,
  approx: finite(value) && method != null && method !== "measured",
});
const sum = (values: (number | null)[]) =>
  values.some(finite) ? values.reduce<number>((t, v) => t + (v ?? 0), 0) : null;
/** A window's total; ≈ only when the shared-out half-hours carry more than a tenth of its energy. */
const sumMeasured = (values: Measured[]): Measured => {
  const value =
    values.every((v) => finite(v.value)) && values.length ? values.reduce((t, v) => t + (v.value ?? 0), 0) : null;
  const approx = values.filter((v) => v.approx).reduce((t, v) => t + (v.value ?? 0), 0);
  return { value, approx: value != null && approx > 0.1 * Math.max(value, 0.01) };
};

/** Plain flags where the measured outcome contradicts the plan for a half-hour or a window. */
export function contradictions(key: string, soc: HistoryRow["soc"], gridIn: number | null, minutes: number): string[] {
  const flags: string[] = [];
  const moved = soc.start != null && soc.end != null ? soc.end - soc.start : null;
  if (moved != null) {
    if ((key === "charge" || key === "charge-export") && moved <= -3) flags.push(FELL_DURING_CHARGE);
    if ((key === "freeze-charge" || key === "hold-charge") && moved <= -3) flags.push("Battery fell while it was held");
    if (key === "export" && moved >= 3) flags.push("Battery rose during an export");
  }
  // A whole kilowatt-hour per hour from the grid while exporting is not a rounding error.
  if (key === "export" && gridIn != null && gridIn >= Math.max(0.3, minutes / 60))
    flags.push("Imported from the grid during an export");
  if (soc.plannedEnd != null && soc.end != null && Math.abs(soc.end - soc.plannedEnd) >= 10) {
    const off = Math.round(soc.end - soc.plannedEnd);
    flags.push(`Ended ${Math.abs(off)} points ${off < 0 ? "below" : "above"} plan`);
  }
  return flags;
}

/** Half-hour rows for the elapsed history slots, joined with the per-slot meter evidence when it has loaded. */
export function historyRows(recent: PlanSlotLike[], now: number, evidence?: PlanEvidence | null): HistoryRow[] {
  const byTime = new Map((evidence?.slots ?? []).map((e) => [Date.parse(e.time), e]));
  const all = [...recent].sort((a, b) => startOf(a) - startOf(b));
  const sorted = all.filter((s) => startOf(s) + (s.durationMinutes || 30) * MIN <= now);
  return sorted.map((slot) => {
    const start = startOf(slot),
      end = start + (slot.durationMinutes || 30) * MIN;
    // The next slot (possibly the one in progress) starts where this one was planned to end.
    const next = all.find((s) => Math.abs(startOf(s) - end) < MIN);
    const e = byTime.get(start);
    const soc = {
      plannedStart: finite(slot.socForecast) ? slot.socForecast : null,
      plannedEnd: finite(slot.socForecastEnd)
        ? slot.socForecastEnd
        : next && Math.abs(startOf(next) - end) < MIN && finite(next.socForecast)
          ? next.socForecast
          : null,
      start: finite(slot.socActualStart) ? slot.socActualStart : null,
      end: finite(slot.socActual) ? slot.socActual : null,
    };
    const gridIn = metric(e, "grid_import"),
      gridOut = metric(e, "grid_export");
    const car = finite(slot.evActual) ? slot.evActual : metric(e, "ev");
    const homeValue = finite(slot.homeActual) ? slot.homeActual : slot.loadActual;
    const key = (slot.actionKey && slot.actionKey !== "unknown" ? slot.actionKey : slot.action) ?? "";
    const flags = contradictions(key, soc, gridIn, (end - start) / MIN);
    const cause: FlagCause = !flags.includes(FELL_DURING_CHARGE)
      ? null
      : (car ?? 0) > 0.5
        ? "car"
        : soc.plannedStart != null && soc.plannedEnd != null && soc.plannedEnd <= soc.plannedStart + 0.5
          ? "pause"
          : null;
    return {
      start,
      end,
      slot,
      soc,
      home: {
        forecast: finite(slot.loadForecast) ? slot.loadForecast : null,
        actual: measured(homeValue, slot.loadActualMethod),
      },
      solar: {
        forecast: finite(slot.pvForecast) ? slot.pvForecast : null,
        actual: measured(slot.pvActual, slot.pvActualMethod),
      },
      gridIn,
      gridOut,
      car,
      flags,
      cause,
    };
  });
}

const ACTION_WORD: Record<string, string> = {
  charge: "charge",
  "charge-export": "charge",
  "freeze-charge": "hold",
  "hold-charge": "hold",
  export: "export",
};

/**
 * A half-hour's flag as a plain sentence that says what was measured: "During the 02:30 export, 1.2 kWh came from the
 * grid", "During the 03:00 charge, the battery fell 4 points while the car took 1.8 kWh". The "Ended … below plan" flag
 * is shown as the number beside the battery instead, so it has no sentence here.
 */
export function flagSentence(row: HistoryRow, flag: string, timeZone?: string) {
  const key = (row.slot.actionKey && row.slot.actionKey !== "unknown" ? row.slot.actionKey : row.slot.action) ?? "";
  const during = `During the ${clock(row.start, { timeZone })} ${ACTION_WORD[key] ?? "half-hour"}`;
  const moved = row.soc.start != null && row.soc.end != null ? Math.round(Math.abs(row.soc.end - row.soc.start)) : null;
  const points = moved != null ? ` ${moved} ${moved === 1 ? "point" : "points"}` : "";
  if (flag === "Imported from the grid during an export")
    return row.gridIn != null
      ? `${during}, ${kwh(row.gridIn)} came from the grid`
      : `${during}, energy came from the grid`;
  if (flag === FELL_DURING_CHARGE) {
    const why =
      row.cause === "car" && row.car != null
        ? ` while the car took ${kwh(row.car)}`
        : row.cause === "pause"
          ? "; Predbat had planned a pause here"
          : "";
    return `${during}, the battery fell${points}${why}`;
  }
  if (flag === "Battery fell while it was held") return `${during}, the battery fell${points}`;
  if (flag === "Battery rose during an export") return `${during}, the battery rose${points}`;
  return `${flag} (${clock(row.start, { timeZone })})`;
}

/** The same rows grouped into the plan's windows (same action, prices merged), newest window last. */
export function historyWindows(rows: HistoryRow[], now: number, reserve?: number | null): HistoryWindow[] {
  const windows = planWindows(
    rows.map((r) => r.slot),
    { now, reserve },
  );
  return windows.map((window) => {
    const inside = rows.filter((r) => r.start >= window.start && r.end <= window.end);
    const first = inside[0],
      last = inside.at(-1);
    const soc = {
      plannedStart: first?.soc.plannedStart ?? null,
      plannedEnd: last?.soc.plannedEnd ?? null,
      start: first?.soc.start ?? null,
      end: last?.soc.end ?? null,
    };
    const gridIn = sum(inside.map((r) => r.gridIn));
    const flags = [
      ...new Set([
        ...inside.flatMap((r) => r.flags.filter((f) => !f.startsWith("Ended"))),
        ...contradictions(window.key, soc, null, (window.end - window.start) / MIN).filter((f) =>
          f.startsWith("Ended"),
        ),
      ]),
    ];
    return {
      window,
      rows: inside,
      soc,
      home: {
        forecast: sum(inside.map((r) => r.home.forecast)),
        actual: sumMeasured(inside.map((r) => r.home.actual)),
      },
      solar: {
        forecast: sum(inside.map((r) => r.solar.forecast)),
        actual: sumMeasured(inside.map((r) => r.solar.actual)),
      },
      gridIn,
      gridOut: sum(inside.map((r) => r.gridOut)),
      car: sum(inside.map((r) => r.car)),
      flags,
    };
  });
}
