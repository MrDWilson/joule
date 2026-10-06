/**
 * Formatters and time helpers that other components still import from here. New chart code uses theme.ts, kit.tsx,
 * scale.ts and timeTicks.ts; these delegate to lib/format.ts and the shared wall-clock helper so a figure reads the same
 * everywhere.
 */
import { coverageText, gbp, kwh } from "../../lib/format";
import { wallOf } from "./timeTicks";

/** "£1.10", or "Unavailable" when missing. */
export const money = (n: number | null | undefined) => (n == null ? "Unavailable" : gbp(n));
/** "12.35 kWh", or "Unavailable" when missing. */
export const energy = (n: number | null | undefined) => (n == null ? "Unavailable" : kwh(n, { precision: "table" }));
/** How much of a period was measured: "Fully measured", "89% measured" (rounded down, so a gap never reads as 100%) or "No readings". */
export const coverage = (n: number | undefined) => coverageText(n);
/** Wall-clock parts of an instant in the household's zone. */
export function localClock(timeZone?: string) {
  return (iso: string) => {
    const at = Date.parse(iso);
    if (!Number.isFinite(at)) return { hour: NaN, minute: NaN, time: "", day: "", dayShort: "" };
    const w = wallOf(at, timeZone);
    return { hour: w.hour, minute: w.minute, time: w.time, day: w.day, dayShort: w.dayShort };
  };
}
