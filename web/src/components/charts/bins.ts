/**
 * Half-hour averages from raw meter intervals, for the tile sparklines.
 *
 * Home Assistant sometimes skips an update, so one 5-minute interval reads 0 and the next reads double: drawn raw that is a
 * picket fence with fake 15 kW peaks. Averaging each half-hour removes it, and the peak quoted is the highest half-hour
 * average ("peak 7.6 kW (half-hour, 00:30)"), never a single poll.
 */
export interface MeterInterval {
  start: string;
  end: string;
  averageKw: number | null;
  status: string;
}

export interface Bin {
  start: number;
  end: number;
  /** Average power over the measured part of the half-hour, kW; null when under half of it was measured. */
  kw: number | null;
  /** Some of the energy was proved by the counter but its timing shared out (≈). */
  approx: boolean;
  state: "value" | "missing" | "none";
}

const HALF = 1800000;
/** Statuses that carry energy: observed, spread (≈, timing estimated) and idle (a known zero). */
const usable = (status: string) => status === "observed" || status === "spread" || status === "idle";

export function halfHourBins(intervals: MeterInterval[], from: number, to: number, now: number): Bin[] {
  const parsed = intervals
    .map((i) => ({
      start: Date.parse(i.start),
      end: Date.parse(i.end),
      kw: i.status === "idle" ? 0 : i.averageKw,
      status: i.status,
    }))
    .filter((i) => usable(i.status) && i.end > i.start && i.kw != null && Number.isFinite(i.kw) && i.kw >= 0);
  const bins: Bin[] = [];
  for (let b = from; b < to; b += HALF) {
    const end = b + HALF;
    if (b >= now) {
      bins.push({ start: b, end, kw: null, approx: false, state: "none" });
      continue;
    }
    const until = Math.min(end, now);
    let covered = 0,
      energy = 0,
      approx = false;
    for (const i of parsed) {
      const overlap = Math.min(i.end, until) - Math.max(i.start, b);
      if (overlap <= 0) continue;
      covered += overlap;
      energy += i.kw! * overlap;
      if (i.status === "spread") approx = true;
    }
    const span = until - b;
    const inProgress = end > now;
    if (covered >= span * 0.5 && covered > 0)
      bins.push({ start: b, end, kw: energy / covered, approx, state: "value" });
    else bins.push({ start: b, end, kw: null, approx: false, state: inProgress ? "none" : "missing" });
  }
  return bins;
}

/**
 * Half-hour bins from one of the server's half-hour power series (the derived "home" series has no raw intervals): measured
 * and idle steps are values, estimated steps are ≈ values, missing steps are gaps and pending steps are not yet drawn. Null
 * when the series is not on a half-hour grid.
 */
export function seriesBins(
  series: { start: string; stepMinutes: number; kw: (number | null)[]; status: string[] },
  from: number,
  to: number,
  now: number,
): Bin[] | null {
  if (series.stepMinutes !== 30) return null;
  const start = Date.parse(series.start);
  if (!Number.isFinite(start)) return null;
  const bins: Bin[] = [];
  for (let b = from; b < to; b += HALF) {
    const end = b + HALF;
    const k = (b - start) / HALF;
    const i = Number.isInteger(k) && k >= 0 && k < series.kw.length ? k : -1;
    const status = i >= 0 ? series.status[i] : "pending";
    const kw = i >= 0 ? series.kw[i] : null;
    const usableValue = kw != null && Number.isFinite(kw) && kw >= 0;
    if (b >= now || status === "pending") bins.push({ start: b, end, kw: null, approx: false, state: "none" });
    else if (usableValue && (status === "measured" || status === "idle" || status === "estimated"))
      bins.push({ start: b, end, kw: status === "idle" ? 0 : kw, approx: status === "estimated", state: "value" });
    else bins.push({ start: b, end, kw: null, approx: false, state: end > now ? "none" : "missing" });
  }
  return bins;
}

/** The highest half-hour average, excluding ≈ bins (their timing is estimated, so their peak is not real). */
export function peakBin(bins: Bin[]) {
  return bins.reduce<Bin | null>(
    (best, b) => (b.kw != null && !b.approx && (!best || b.kw > best.kw!) ? b : best),
    null,
  );
}
