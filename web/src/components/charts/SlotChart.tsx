/**
 * A small per-slot chart on a time axis, for the impact preview and the second-forecast comparison: flat steps per slot,
 * dashed only for forecasts, nice ticks (around zero when values go both ways), local-hour ticks in the household's zone,
 * a hover band with a tooltip, and arrow-key scrubbing.
 */
import { useState, type KeyboardEvent, type ReactNode } from "react";
import { FloatingTip, useWidth } from "./kit";
import { stepArea, stepPath, type Step } from "./paths";
import { divergingTicks, linear, niceTicks, tickText } from "./scale";
import { tickPlacement, timeTicks } from "./timeTicks";
import { encodings } from "./theme";

export interface SlotLine {
  key: string;
  color: string;
  values: (number | null)[];
  dashed?: boolean;
  /** Fill between the line and zero (a delta). */
  area?: boolean;
  width?: number;
}

const decimals = (step: number) => (String(Math.round(step * 1e6) / 1e6).split(".")[1] ?? "").length;

export function SlotChart({
  starts,
  ends,
  lines,
  timeZone,
  height = 200,
  tooltip,
  readout,
  label,
  unit,
}: {
  starts: number[];
  ends: number[];
  lines: SlotLine[];
  timeZone?: string;
  height?: number;
  tooltip: (i: number) => ReactNode;
  /** One line describing slot i, for the keyboard slider. */
  readout: (i: number) => string;
  label: string;
  /** The value axis unit, written above the ticks ("kWh"). */
  unit?: string;
}) {
  const [box, width] = useWidth<HTMLDivElement>(640);
  const [active, setActive] = useState<number | null>(null);
  const compact = width < 600;
  const all = lines.flatMap((l) => l.values).filter((v): v is number => v != null && Number.isFinite(v));
  const lo = Math.min(0, ...all),
    hi = Math.max(0, ...all);
  const ticksFor = (count: number) =>
    lo < 0 ? divergingTicks(lo, hi, count) : { ...niceTicks(hi, count, 0.1), min: 0 };
  // Ticks like 0.025, 0.050 are hard to read: with one tick fewer the step is usually a two-decimal 0.05.
  const first = ticksFor(compact ? 3 : 4);
  const scale = decimals(first.step) > 2 ? ticksFor(compact ? 2 : 3) : first;
  const gl = compact ? 34 : 42,
    gr = 8,
    // Room above the top tick for the unit, so the two never overlap.
    top = unit ? 22 : 8,
    plotH = height - 40 - (unit ? 14 : 0);
  const plotW = Math.max(40, width - gl - gr);
  const d0 = starts[0] ?? 0,
    d1 = ends.at(-1) ?? d0 + 1;
  const x = linear([d0, d1 > d0 ? d1 : d0 + 1], [gl, gl + plotW]);
  const y = linear([scale.min, scale.max], [top + plotH, top]);
  const steps = (values: (number | null)[]): (Step | null)[] =>
    values.map((v, i) => (v == null || !Number.isFinite(v) ? null : { x0: x(starts[i]), x1: x(ends[i]), y: y(v) }));
  const ticks = timeTicks(d0, d1, timeZone, Math.max(2, Math.floor(plotW / (compact ? 62 : 80))));
  const indexAt = (px: number) => {
    const t = x.invert(px);
    const i = starts.findIndex((s, k) => s <= t && t < ends[k]);
    return i >= 0 ? i : t < d0 ? 0 : starts.length - 1;
  };
  const onKey = (e: KeyboardEvent<HTMLDivElement>) => {
    const current = active ?? 0;
    const next =
      e.key === "ArrowRight"
        ? Math.min(starts.length - 1, current + 1)
        : e.key === "ArrowLeft"
          ? Math.max(0, current - 1)
          : e.key === "Escape"
            ? null
            : undefined;
    if (next === undefined) return;
    e.preventDefault();
    setActive(next);
  };
  return (
    <div className="mini-chart" ref={box}>
      <svg width={width} height={height} viewBox={`0 0 ${width} ${height}`} aria-hidden="true" focusable="false">
        {unit && (
          <text className="tl-tick tl-unit" x={gl - 6} y={12} textAnchor="end">
            {unit}
          </text>
        )}
        {scale.ticks.map((t) => (
          <g key={t}>
            <line className={t === 0 ? "daily-zero" : "tl-grid"} x1={gl} x2={gl + plotW} y1={y(t)} y2={y(t)} />
            <text className="tl-tick" x={gl - 6} y={y(t) + 4} textAnchor="end">
              {tickText(t, scale.step)}
            </text>
          </g>
        ))}
        {active != null && (
          <rect
            className="tl-hover"
            x={x(starts[active])}
            width={Math.max(1, x(ends[active]) - x(starts[active]))}
            y={top}
            height={plotH}
          />
        )}
        {lines.map((l) =>
          l.area ? (
            <path
              key={`${l.key}-area`}
              className="tl-area"
              d={stepArea(steps(l.values), y(0))}
              style={{ fill: l.color, fillOpacity: 0.18 }}
            />
          ) : null,
        )}
        {lines.map((l) => (
          <path
            key={l.key}
            className="tl-series"
            data-series={l.key}
            d={stepPath(steps(l.values))}
            style={{
              stroke: l.color,
              strokeWidth: l.width ?? 2,
              strokeDasharray: l.dashed ? encodings.forecast.strokeDasharray : undefined,
            }}
          />
        ))}
        {ticks.map((t) => {
          const p = tickPlacement(x(t.t), t, width);
          return (
            <text key={t.t} className="tl-tick" x={p.x} y={top + plotH + 16} textAnchor={p.anchor}>
              <tspan x={p.x}>{t.time}</tspan>
              {t.day && (
                <tspan x={p.x} dy={14} className="tl-tick-day">
                  {t.day}
                </tspan>
              )}
            </text>
          );
        })}
      </svg>
      <div
        className="tl-surface"
        style={{ left: gl, width: plotW, top, height: plotH }}
        role="slider"
        tabIndex={0}
        aria-label={`${label} (left and right arrow keys)`}
        aria-valuemin={0}
        aria-valuemax={Math.max(0, starts.length - 1)}
        aria-valuenow={active ?? 0}
        aria-valuetext={active != null ? readout(active) : "No slot selected"}
        onPointerMove={(e) => setActive(indexAt(e.clientX - e.currentTarget.getBoundingClientRect().left + gl))}
        onPointerDown={(e) => setActive(indexAt(e.clientX - e.currentTarget.getBoundingClientRect().left + gl))}
        onPointerLeave={(e) => e.pointerType === "mouse" && setActive(null)}
        onKeyDown={onKey}
        onBlur={() => setActive(null)}
      />
      {active != null && (
        <FloatingTip x={(x(starts[active]) + x(ends[active])) / 2} top={top} width={width}>
          {tooltip(active)}
        </FloatingTip>
      )}
    </div>
  );
}
