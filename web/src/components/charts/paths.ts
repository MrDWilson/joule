/** SVG path builders for the hand-drawn charts. Every builder breaks the line at a null: a missing value is never drawn. */

export interface Step {
  /** Slot start and end, in pixels. */
  x0: number;
  x1: number;
  y: number;
}

const n = (v: number) => (Math.round(v * 100) / 100).toString();

/**
 * Per-slot energy as flat steps ("this half-hour used X"): each slot is a horizontal run across its own width, and touching
 * slots join with a vertical riser. A null, or a slot that does not start where the previous one ended, starts a new run.
 */
export function stepPath(steps: (Step | null)[]) {
  let d = "";
  let last: Step | null = null;
  for (const s of steps) {
    if (!s || !Number.isFinite(s.y)) {
      last = null;
      continue;
    }
    if (last && Math.abs(last.x1 - s.x0) < 0.5) d += ` V${n(s.y)} H${n(s.x1)}`;
    else d += `${d ? " " : ""}M${n(s.x0)},${n(s.y)} H${n(s.x1)}`;
    last = s;
  }
  return d;
}

/** The area between flat steps and a baseline (for the faint wash under a measured line). */
export function stepArea(steps: (Step | null)[], baseline: number) {
  const runs: Step[][] = [];
  let run: Step[] = [];
  let last: Step | null = null;
  for (const s of steps) {
    if (!s || !Number.isFinite(s.y)) {
      if (run.length) runs.push(run);
      run = [];
      last = null;
      continue;
    }
    if (last && Math.abs(last.x1 - s.x0) >= 0.5) {
      runs.push(run);
      run = [];
    }
    run.push(s);
    last = s;
  }
  if (run.length) runs.push(run);
  return runs
    .map((r) => {
      let d = `M${n(r[0].x0)},${n(baseline)} V${n(r[0].y)} H${n(r[0].x1)}`;
      for (const s of r.slice(1)) d += ` V${n(s.y)} H${n(s.x1)}`;
      return `${d} V${n(baseline)} Z`;
    })
    .join(" ");
}

export interface Point {
  x: number;
  y: number;
}

/** Straight segments between points, broken at nulls (battery level, which really is continuous). */
export function linePath(points: (Point | null)[]) {
  let d = "";
  let open = false;
  for (const p of points) {
    if (!p || !Number.isFinite(p.y) || !Number.isFinite(p.x)) {
      open = false;
      continue;
    }
    d += open ? ` L${n(p.x)},${n(p.y)}` : `${d ? " " : ""}M${n(p.x)},${n(p.y)}`;
    open = true;
  }
  return d;
}

/** The area under a broken line, down to a baseline. */
export function lineArea(points: (Point | null)[], baseline: number) {
  const runs: Point[][] = [[]];
  for (const p of points) {
    if (!p || !Number.isFinite(p.y)) {
      if (runs.at(-1)!.length) runs.push([]);
    } else runs.at(-1)!.push(p);
  }
  return runs
    .filter((r) => r.length > 1)
    .map(
      (r) =>
        `M${n(r[0].x)},${n(baseline)} ` +
        r.map((p) => `L${n(p.x)},${n(p.y)}`).join(" ") +
        ` L${n(r.at(-1)!.x)},${n(baseline)} Z`,
    )
    .join(" ");
}

/** A bar with 4 px rounded corners at its outer end only (square at the baseline), growing up or down from `base`. */
export function barPath(x: number, width: number, base: number, end: number, radius = 4) {
  const h = Math.abs(end - base);
  if (h < 0.01 || width <= 0) return "";
  const r = Math.min(radius, h, width / 2);
  const up = end < base;
  const l = n(x),
    rr = n(x + width);
  if (up)
    return `M${l},${n(base)} V${n(end + r)} Q${l},${n(end)} ${n(x + r)},${n(end)} H${n(x + width - r)} Q${rr},${n(end)} ${rr},${n(end + r)} V${n(base)} Z`;
  return `M${l},${n(base)} V${n(end - r)} Q${l},${n(end)} ${n(x + r)},${n(end)} H${n(x + width - r)} Q${rr},${n(end)} ${rr},${n(end - r)} V${n(base)} Z`;
}
