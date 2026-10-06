import type { ObservedMeterTrends } from "../completion-types";
import { endOfZonedDay, zonedDay } from "../lib/comparison";
import { zonedDateMidnight } from "../lib/period";
import { kw, percent } from "../lib/format";
import { halfHourBins, peakBin, seriesBins, type Bin } from "./charts/bins";
import { BRIDGE_MAX_MS } from "./charts/gaps";
import { useLayoutEffect, useRef, useState } from "react";
import { linePath, stepArea, stepPath, type Point, type Step } from "./charts/paths";
import { linear, percentile } from "./charts/scale";
import { wallOf } from "./charts/timeTicks";
import type { TimelineSlot } from "./charts/timeline";

/** Height before the box is measured; then the drawing matches whatever height CSS gives it (40px in Today's tiles). */
const DEFAULT_H = 48;
const PAD = 4;

/**
 * The sparkline box's measured width and height, so the drawing spans the tile's content box exactly. The SVG also
 * stretches (preserveAspectRatio none, strokes that don't scale) to cover any moment before a resize is measured.
 */
function useBox(fallbackWidth: number) {
  const ref = useRef<HTMLDivElement>(null);
  const [box, setBox] = useState({ width: fallbackWidth, height: DEFAULT_H });
  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    const read = () => {
      const r = el.getBoundingClientRect();
      if (r.width <= 0) return;
      const next = { width: Math.round(r.width), height: Math.round(r.height) || DEFAULT_H };
      setBox((b) => (b.width === next.width && b.height === next.height ? b : next));
    };
    read();
    if (typeof ResizeObserver === "undefined") return;
    const observer = new ResizeObserver(read);
    observer.observe(el);
    return () => observer.disconnect();
  }, []);
  return [ref, box.width, box.height] as const;
}

/**
 * Tile sparkline for today, midnight to midnight: half-hour average power measured so far (solid, from the meter
 * intervals), Predbat's forecast for the rest of the day as a faint dashed tail, and a dot at now. The scale tops out at
 * the 98th percentile so one spike can't flatten the day; anything above it is clipped with a small cap mark. The caption
 * quotes the real peak half-hour.
 */
export function ObservedTrend({
  data,
  metric,
  error,
  timeZone,
  plan,
  now: nowProp,
}: {
  data: ObservedMeterTrends | null;
  metric: "load" | "pv";
  error: string;
  timeZone: string;
  /** The current plan: its forecast for the rest of today draws the dashed tail. */
  plan?: TimelineSlot[];
  now?: number;
}) {
  const [ref, width, H] = useBox(240);
  const now = nowProp ?? Date.now();
  const today = zonedDay(new Date(now), timeZone);
  const from = zonedDateMidnight(today, timeZone).getTime(),
    to = endOfZonedDay(today, timeZone).getTime();
  // Home use excludes the car when the load meter includes it: the server's derived "home" series, so a car charge never
  // shows as the house's peak. Without that series the load meter is home use as measured.
  const homeSeries = metric === "load" ? data?.series?.find((x) => x.metric === "home") : undefined;
  const fromSeries = homeSeries ? seriesBins(homeSeries, from, to, now) : null;
  const intervals = (data?.intervals || []).filter((p) => p.metric === metric);
  const bins = fromSeries ?? halfHourBins(intervals, from, to, now);
  const measured = bins.filter((b) => b.state === "value");
  const tail = forecastTail(plan, metric, now, to);
  const peak = peakBin(bins);
  const values = [...measured.map((b) => b.kw!), ...tail.map((t) => t.kw)];
  const cap = Math.max(percentile(values, 98) ?? 0, 0.05);
  const x = linear([from, to], [1, Math.max(2, width - 1)]);
  const y = linear([0, cap], [H - PAD, PAD]);
  const clip = (v: number) => y(Math.min(v, cap));
  const steps: (Step | null)[] = bins.map((b) =>
    b.kw != null ? { x0: x(b.start), x1: x(Math.min(b.end, now)), y: clip(b.kw) } : null,
  );
  const tailSteps: Step[] = tail.map((t) => ({ x0: x(t.start), x1: x(t.end), y: clip(t.kw) }));
  const { bridges, washes } = gapMarks(bins, now, x, clip);
  const capped = measured.filter((b) => b.kw! > cap);
  const last = [...bins].reverse().find((b) => b.kw != null);
  const label = `${metric === "pv" ? "Solar" : fromSeries ? "Home (excl. car)" : "Home"} power today, half-hour averages`;
  const peakText = peak ? `peak ${kw(peak.kw)} (half-hour, ${wallOf(peak.start, timeZone).time})` : "";
  const hasData = measured.length > 0;
  return (
    <div title="Average power per half-hour from midnight; the dashed tail is Predbat's forecast for the rest of today.">
      <div className="metric-trend" ref={ref}>
        {hasData ? (
          <svg
            width={width}
            height={H}
            viewBox={`0 0 ${width} ${H}`}
            preserveAspectRatio="none"
            role="img"
            aria-label={`${label}${peakText ? `; ${peakText}` : ""}`}
            data-gap-bands={washes.length}
            data-bridged-gaps={bridges.length}
            data-cap={cap}
          >
            {washes.map((w, i) => (
              <rect
                key={i}
                className="trend-gap"
                x={w.x0}
                width={Math.max(1, w.x1 - w.x0)}
                y={PAD}
                height={H - 2 * PAD}
              />
            ))}
            <path className="trend-area" d={stepArea(steps, H - PAD)} />
            {tailSteps.length > 0 && (
              <path className="trend-forecast" vectorEffect="non-scaling-stroke" d={stepPath(tailSteps)} />
            )}
            {bridges.map((d, i) => (
              <path key={i} className="trend-bridge" vectorEffect="non-scaling-stroke" d={d} />
            ))}
            <path className="trend-line" vectorEffect="non-scaling-stroke" d={stepPath(steps)} />
            {capped.map((b) => (
              <line
                key={b.start}
                className="trend-cap"
                vectorEffect="non-scaling-stroke"
                x1={x(b.start)}
                x2={x(b.end)}
                y1={PAD - 1}
                y2={PAD - 1}
              />
            ))}
            {last && last.end >= now - BRIDGE_MAX_MS && (
              <circle className="trend-now" cx={x(Math.min(last.end, now))} cy={clip(last.kw!)} r={4} />
            )}
          </svg>
        ) : (
          <span>{error ? "Trend unavailable" : data ? "No readings yet today" : "Loading today's trend…"}</span>
        )}
      </div>
      <small className="trend-caption">
        Today{hasData && peakText && <span className="trend-max">{peakText}</span>}
        {data?.truncated && <span>Showing the latest {data.limit} readings only</span>}
      </small>
    </div>
  );
}

/** The plan's forecast power (kW) for the rest of today, from now (or the current slot) to midnight. */
function forecastTail(plan: TimelineSlot[] | undefined, metric: "load" | "pv", now: number, to: number) {
  return (plan ?? [])
    .map((s) => {
      const start = Date.parse(s.time),
        minutes = s.durationMinutes || 30,
        end = start + minutes * 60000;
      const kwh = metric === "pv" ? s.pvForecast : s.loadForecast;
      return { start: Math.max(start, now), end: Math.min(end, to), kw: kwh != null ? (kwh * 60) / minutes : null };
    })
    .filter(
      (t): t is { start: number; end: number; kw: number } => t.kw != null && Number.isFinite(t.kw) && t.end > t.start,
    )
    .sort((a, b) => a.start - b.start);
}

/** Short gaps (up to an hour) between measured bins are bridged faintly; longer ones get a light wash. */
function gapMarks(bins: Bin[], now: number, x: (t: number) => number, y: (v: number) => number) {
  const bridges: string[] = [],
    washes: { x0: number; x1: number }[] = [];
  for (let i = 0; i < bins.length;) {
    if (bins[i].state !== "missing") {
      i++;
      continue;
    }
    let j = i;
    while (j + 1 < bins.length && bins[j + 1].state === "missing") j++;
    const before = bins[i - 1],
      after = bins[j + 1];
    const span = bins[j].end - bins[i].start;
    if (before?.kw != null && after?.kw != null && span <= BRIDGE_MAX_MS)
      bridges.push(`M${x(before.end)},${y(before.kw)} L${x(after.start)},${y(after.kw)}`);
    else washes.push({ x0: x(bins[i].start), x1: x(Math.min(bins[j].end, now)) });
    i = j + 1;
  }
  return { bridges, washes };
}

/**
 * Battery tile sparkline: the measured level over the last 12 hours (solid, each reading at its own time) and Predbat's
 * planned level for the next 12 (dashed), with a dot at the latest reading and a faint reserve line, on 0–100 %.
 */
export function SocTrend({
  slots,
  current,
  reserve,
  timeZone,
  now: nowProp,
}: {
  /** History followed by the plan (mergeTimeline). */
  slots: TimelineSlot[];
  current?: { value: number | null; time: string } | null;
  reserve?: number | null;
  timeZone: string;
  now?: number;
}) {
  const [ref, width, H] = useBox(240);
  const now = nowProp ?? Date.now();
  const from = now - 12 * 3600000,
    to = now + 12 * 3600000;
  const x = linear([from, to], [1, Math.max(2, width - 1)]);
  const y = linear([0, 100], [H - PAD, PAD]);
  const actual: (Point | null)[] = [];
  const forecast: Point[] = [];
  for (const s of [...slots].sort((a, b) => Date.parse(a.time) - Date.parse(b.time))) {
    const start = Date.parse(s.time),
      end = start + (s.durationMinutes || 30) * 60000;
    if (end <= from || start >= to) continue;
    if (end <= now) {
      if (s.socActualStart != null && start >= from) actual.push({ x: x(start), y: y(s.socActualStart) });
      if (s.socActual != null) actual.push({ x: x(end), y: y(s.socActual) });
      else if (s.socActualStart == null) actual.push(null);
    } else if (s.socForecast != null) {
      forecast.push({ x: x(Math.max(start, now)), y: y(s.socForecast) });
      if (s.socForecastEnd != null && end >= to) forecast.push({ x: x(Math.min(end, to)), y: y(s.socForecastEnd) });
    }
  }
  const t = current?.time ? Date.parse(current.time) : NaN;
  const dot =
    current?.value != null && Number.isFinite(t) && t > from ? { x: x(Math.min(t, now)), y: y(current.value) } : null;
  if (dot) actual.push(dot);
  const ahead = forecast.length ? forecast : [];
  const hasData = actual.some(Boolean) || ahead.length > 0;
  const next12 = slots.filter((s) => Date.parse(s.time) >= now && Date.parse(s.time) < to && s.socForecast != null);
  const low = next12.reduce<TimelineSlot | null>((m, s) => (!m || s.socForecast! < m.socForecast! ? s : m), null);
  return (
    <div title="Battery level: measured over the last 12 hours, Predbat's plan for the next 12 (dashed).">
      <div className="metric-trend battery-trend" ref={ref}>
        {hasData ? (
          <svg
            width={width}
            height={H}
            viewBox={`0 0 ${width} ${H}`}
            preserveAspectRatio="none"
            role="img"
            aria-label="Battery level, last 12 hours measured and next 12 hours planned"
          >
            {reserve != null && reserve > 0 && (
              <line
                className="trend-reserve"
                vectorEffect="non-scaling-stroke"
                x1={1}
                x2={width - 1}
                y1={y(reserve)}
                y2={y(reserve)}
              />
            )}
            <line
              className="trend-now-line"
              vectorEffect="non-scaling-stroke"
              x1={x(now)}
              x2={x(now)}
              y1={PAD}
              y2={H - PAD}
            />
            {ahead.length > 1 && (
              <path className="trend-forecast" vectorEffect="non-scaling-stroke" d={linePath(ahead)} />
            )}
            <path className="trend-line" vectorEffect="non-scaling-stroke" d={linePath(actual)} />
            {dot && <circle className="trend-now" cx={dot.x} cy={dot.y} r={4} />}
          </svg>
        ) : (
          <span>No battery readings or plan yet</span>
        )}
      </div>
      <small className="trend-caption">
        Last 12 h · next 12 h planned
        {low && (
          <span className="trend-max">{`lowest ${percent(low.socForecast)} at ${wallOf(Date.parse(low.time), timeZone).time}`}</span>
        )}
      </small>
    </div>
  );
}
