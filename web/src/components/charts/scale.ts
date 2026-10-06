/** Scales, tick positions and the unit conversions shared by the charts. Pure functions, unit-tested. */
import { number } from "../../lib/format";

/** A linear map from a domain to a pixel range. */
export function linear([d0, d1]: [number, number], [r0, r1]: [number, number]) {
  const span = d1 - d0 || 1;
  const f = (v: number) => r0 + ((v - d0) / span) * (r1 - r0);
  f.invert = (px: number) => d0 + ((px - r0) / (r1 - r0 || 1)) * span;
  return f;
}

/** A "nice" step (1, 2, 2.5 or 5 × 10ⁿ) for roughly `count` intervals over `span`. */
export function niceStep(span: number, count: number) {
  if (!(span > 0) || !(count > 0)) return 1;
  const raw = span / count;
  const power = 10 ** Math.floor(Math.log10(raw));
  const unit = raw / power;
  const nice = unit <= 1 ? 1 : unit <= 2 ? 2 : unit <= 2.5 ? 2.5 : unit <= 5 ? 5 : 10;
  return nice * power;
}

/**
 * Clean ticks from 0 to just above the data: the top is dataMax × 1.1 rounded up to a nice step, so a peak never touches the
 * ceiling and the labels read 0, 1, 2, 3 rather than 0, 0.75, 1.5, 2.25.
 */
export function niceTicks(dataMax: number, count = 4, floor = 0) {
  const top = Math.max(dataMax * 1.1, floor > 0 ? floor : 0, 1e-9);
  const step = niceStep(top, count);
  const max = Math.ceil(top / step - 1e-9) * step;
  const ticks: number[] = [];
  for (let v = 0; v <= max + step / 1000; v += step) ticks.push(round(v));
  return { max: round(max), ticks, step };
}

/** Ticks around zero for values on both sides (daily energy in and out, cost deltas). */
export function divergingTicks(min: number, max: number, count = 4) {
  const lo = Math.min(0, min) * 1.1,
    hi = Math.max(0, max) * 1.1;
  // With values on both sides, step by the larger side so neither half wastes most of its height.
  const step =
    lo < 0 && hi > 0 ? niceStep(Math.max(hi, -lo), Math.max(2, count - 1)) : niceStep(Math.max(hi - lo, 1e-9), count);
  const top = Math.ceil(hi / step - 1e-9) * step,
    bottom = Math.floor(lo / step + 1e-9) * step;
  const ticks: number[] = [];
  for (let v = bottom; v <= top + step / 1000; v += step) ticks.push(round(v));
  return { min: round(bottom), max: round(top), ticks, step };
}

/**
 * Ticks that fit the data's own range, zero included: one step for the whole span, so a small side (a day that earned
 * 30p among days that cost £5) takes one step rather than half the chart. For small charts such as net cost per day.
 */
export function fittedTicks(min: number, max: number, count = 4) {
  const lo = Math.min(0, min) * 1.08,
    hi = Math.max(0, max) * 1.08;
  const step = niceStep(Math.max(hi - lo, 1e-9), count);
  const top = Math.ceil(hi / step - 1e-9) * step,
    bottom = Math.floor(lo / step + 1e-9) * step;
  const ticks: number[] = [];
  for (let v = bottom; v <= top + step / 1000; v += step) ticks.push(round(v));
  return { min: round(bottom), max: round(top), ticks, step };
}

const round = (v: number) => Math.round(v * 1e9) / 1e9;

/** Energy in a slot of any length as kWh per half-hour, the axis unit: a 5-minute 0.06 kWh slot reads 0.36. */
export function perHalfHour(kwh: number | null | undefined, minutes: number) {
  if (kwh == null || !Number.isFinite(kwh) || !(minutes > 0)) return null;
  return (kwh * 30) / minutes;
}

/** The p-th percentile (0–100) of finite values, linearly interpolated; null when there are none. */
export function percentile(values: number[], p: number) {
  const v = values.filter(Number.isFinite).sort((a, b) => a - b);
  if (!v.length) return null;
  const rank = (Math.min(100, Math.max(0, p)) / 100) * (v.length - 1);
  const lo = Math.floor(rank),
    hi = Math.ceil(rank);
  return v[lo] + (v[hi] - v[lo]) * (rank - lo);
}

/** Short tick text with as many decimals as the step needs: "0", "0.25", "0.5", "2"; a real minus sign. */
export function tickText(v: number, step: number) {
  if (v === 0) return "0";
  const decimals = (String(Math.round(step * 1e6) / 1e6).split(".")[1] ?? "").length;
  return number(v, Math.min(decimals, 3));
}
