/**
 * Day-by-day energy as one diverging stacked bar per day: energy in above zero (battery discharge, solar, grid import),
 * energy out below (home use excluding the car, battery charge, the car, grid export), stacked in the order the palette was
 * validated for. Partly measured days are honest about it: below 95 % a segment is drawn faint and hatched (the share
 * measured is in the tooltip and the table, not over the bar), below 50 % it is left out ("n/a"). The previous period is a thin average line, drawn only when it
 * has data. Net cost has its own small neutral chart: energy and money never share an axis.
 *
 * A single day is not a bar chart: with an `api` it becomes that day's half-hour profile, otherwise nothing (the figures
 * above already say it).
 */
import { useEffect, useId, useMemo, useRef, useState } from "react";
import type { Api, EnergySummary } from "../../completion-types";
import { gbp, kwh, percent } from "../../lib/format";
import { ChartFigure, ChartTable, FloatingTip, LegendChips, TipRow, useHiddenSeries, useWidth, type Chip } from "./kit";
import { barPath } from "./paths";
import { divergingTicks, fittedTicks, linear, tickText } from "./scale";
import { encodings, earlierInk, ink, series, type SeriesKey } from "./theme";
import { wallOf } from "./timeTicks";
import { EnergyTimeline } from "./EnergyTimeline";
import type { HistorySlot, TimelineSlot } from "./timeline";
export { coverage, energy, money } from "./chartUtils";

type Segment = { key: SeriesKey; metric: string; label: string; side: 1 | -1 };
/** Stacking order from zero outward; validated neighbour by neighbour (see theme.ts). */
const segments: Segment[] = [
  { key: "battery", metric: "battery_discharge", label: "Battery discharge", side: 1 },
  { key: "solar", metric: "pv", label: "Solar", side: 1 },
  { key: "grid", metric: "grid_import", label: "Grid import", side: 1 },
  { key: "home", metric: "home", label: "Home use", side: -1 },
  { key: "battery", metric: "battery_charge", label: "Battery charge", side: -1 },
  { key: "ev", metric: "ev", label: "Car", side: -1 },
  { key: "grid", metric: "grid_export", label: "Grid export", side: -1 },
];
const PARTIAL = 0.95,
  OMIT = 0.5;

interface DayValue {
  kwh: number | null;
  coverage: number;
}
interface DayRow {
  from: string;
  label: string;
  short: string;
  values: Record<string, DayValue>;
  cost: number | null;
  costCoverage: number;
  paid: number | null;
  earned: number | null;
}

/**
 * Home use excluding the car, decided per day by the server's own figures: its derived `home` metric when present;
 * otherwise load minus car only when the day says (or, for an older server, the caller says) the load meter includes the
 * car; otherwise the load as measured, which then never contained the car.
 */
function homeOf(d: EnergySummary, load: DayValue, ev: DayValue, loadIncludesEv: boolean): DayValue {
  if (d.home) return { kwh: d.home.energyKwh, coverage: d.home.coverageFraction };
  const includes = d.loadIncludesEv ?? loadIncludesEv;
  return includes && ev.kwh != null && load.kwh != null
    ? { kwh: Math.max(0, load.kwh - ev.kwh), coverage: Math.min(load.coverage, ev.coverage) }
    : load;
}

/**
 * The day's net cost: import cost minus export credit, each side with its own coverage, so the share priced is the weaker
 * side's. The stricter matched-period figure is only a fallback for a server that predates `netCostGbp`.
 */
function netCostOf(d: EnergySummary): { cost: number | null; coverage: number } {
  if (d.netCostGbp === undefined) return { cost: d.observedNetCostGbp, coverage: d.costCoverageFraction };
  return { cost: d.netCostGbp, coverage: Math.min(d.importCostCoverage ?? 0, d.exportCostCoverage ?? 0) };
}

/** One day's figures, with home use excluding the car only when the load meter includes it. */
export function dayRow(d: EnergySummary, timeZone: string, loadIncludesEv = true): DayRow {
  const m = (k: string): DayValue => ({
    kwh: d.metrics[k]?.energyKwh ?? null,
    coverage: d.metrics[k]?.coverageFraction ?? 0,
  });
  const home = homeOf(d, m("load"), m("ev"), loadIncludesEv);
  const net = netCostOf(d);
  const at = Date.parse(d.from) + 12 * 3600000;
  const w = wallOf(at, timeZone);
  return {
    from: d.from,
    label: w.day,
    short: w.dayShort,
    values: Object.fromEntries(segments.map((s) => [s.metric, s.metric === "home" ? home : m(s.metric)])),
    cost: net.cost,
    costCoverage: net.coverage,
    paid: d.importCostGbp,
    earned: d.exportCreditGbp,
  };
}

/**
 * The previous period's average daily home use (without the car: home is the first segment below zero, so the line meets
 * the home bar's edge) and solar, from its days that were at least half measured; null when it has no such day, so the
 * chart never claims a comparison it can't draw.
 */
export function previousAverages(previous: EnergySummary[] | undefined, timeZone: string, loadIncludesEv = true) {
  const rows = (previous ?? []).filter(hasEnergy).map((d) => dayRow(d, timeZone, loadIncludesEv));
  const average = (pick: (r: DayRow) => DayValue[]) => {
    const usable = rows.filter((r) => pick(r).every((v) => v.kwh != null && v.coverage >= OMIT));
    return usable.length
      ? usable.reduce((t, r) => t + pick(r).reduce((a, v) => a + (v.kwh ?? 0), 0), 0) / usable.length
      : null;
  };
  const use = average((r) => [r.values.home]),
    solar = average((r) => [r.values.pv]);
  return use == null && solar == null ? null : { use, solar };
}

/**
 * A day's label under its bar: the full day when there's room, "Tue 29" when narrow, "Tue" over "29" when narrower still
 * (stack), and on a month only every nth day, counted back from the latest so the newest day is always labelled.
 */
function DayTick({
  x,
  y,
  label,
  short,
  band,
  index,
  count,
  stack = false,
}: {
  x: number;
  y: number;
  label: string;
  short: string;
  band: number;
  index: number;
  count: number;
  stack?: boolean;
}) {
  const stacked = stack && band < 52;
  const width = stacked ? 30 : band < 64 ? 48 : 64;
  const every = Math.max(1, Math.ceil(width / band));
  if ((count - 1 - index) % every !== 0) return null;
  if (stacked) {
    const [weekday, day] = short.split(" ");
    return (
      <text className="tl-tick" x={x} y={y - 4} textAnchor="middle">
        <tspan x={x}>{weekday}</tspan>
        <tspan x={x} dy={12}>
          {day}
        </tspan>
      </text>
    );
  }
  return (
    <text className="tl-tick" x={x} y={y} textAnchor="middle">
      {band < 64 ? short : label}
    </text>
  );
}

const hasEnergy = (d: EnergySummary) =>
  Object.values(d.metrics).some((m) => m.energyKwh != null && m.coverageFraction > 0);

export function DailyChart({
  days,
  timeZone = "UTC",
  previous,
  previousLabel = "the previous period",
  api,
  loadIncludesEv = true,
}: {
  days: EnergySummary[];
  timeZone?: string;
  previous?: EnergySummary[];
  previousLabel?: string;
  /** Lets a single day show its half-hour profile. */
  api?: Api;
  loadIncludesEv?: boolean;
}) {
  if (!days.some(hasEnergy))
    return <p className="muted">A chart will appear when this period has measured readings.</p>;
  if (days.length === 1) return api ? <DayProfile api={api} day={days[0]} timeZone={timeZone} /> : null;
  return (
    <DailyBars
      days={days}
      timeZone={timeZone}
      previous={previous}
      previousLabel={previousLabel}
      loadIncludesEv={loadIncludesEv}
    />
  );
}

function DailyBars({
  days,
  timeZone,
  previous,
  previousLabel,
  loadIncludesEv,
}: {
  days: EnergySummary[];
  timeZone: string;
  previous?: EnergySummary[];
  previousLabel: string;
  loadIncludesEv: boolean;
}) {
  const id = useId().replace(/:/g, "");
  const [box, width] = useWidth<HTMLDivElement>(720);
  const compact = width < 600;
  const rows = useMemo(() => days.map((d) => dayRow(d, timeZone, loadIncludesEv)), [days, timeZone, loadIncludesEv]);
  const [hidden, toggle] = useHiddenSeries("daily");
  const [active, setActive] = useState<number | null>(null);
  const car = rows.some((r) => (r.values.ev.kwh ?? 0) > 0.05);
  const shown = segments.filter((s) => !hidden.includes(s.key) && (s.key !== "ev" || car));
  const drawn = (r: DayRow, s: Segment) => {
    const v = r.values[s.metric];
    return v.kwh != null && v.kwh > 0 && v.coverage >= OMIT ? v : null;
  };
  const averages = previousAverages(previous, timeZone, loadIncludesEv);
  const compare = !!averages;
  const prevUse = averages?.use ?? null,
    prevSolar = averages?.solar ?? null;

  const totals = rows.map((r) => {
    let up = 0,
      down = 0;
    for (const s of shown) {
      const v = drawn(r, s);
      if (v) s.side > 0 ? (up += v.kwh!) : (down += v.kwh!);
    }
    return { up, down };
  });
  const ticks = divergingTicks(
    -Math.max(...totals.map((t) => t.down), compare && prevUse ? prevUse : 0),
    Math.max(...totals.map((t) => t.up), compare && prevSolar ? prevSolar : 0),
    4,
  );
  const gl = compact ? 34 : 42,
    gr = 8,
    top = 30,
    plotH = compact ? 190 : 230,
    height = top + plotH + 40;
  const plotW = Math.max(40, width - gl - gr);
  const band = plotW / rows.length;
  const barW = Math.max(6, Math.min(56, band * 0.5));
  const y = linear([ticks.min, ticks.max], [top + plotH, top]);
  const cx = (i: number) => gl + band * (i + 0.5);
  const chips: Chip[] = [
    { key: "solar", label: series.solar.label, color: series.solar.color, shape: "block" },
    { key: "home", label: "Home", color: series.home.color, shape: "block" },
    ...(car ? [{ key: "ev", label: series.ev.label, color: series.ev.color, shape: "block" as const }] : []),
    { key: "battery", label: series.battery.label, color: series.battery.color, shape: "block" },
    { key: "grid", label: series.grid.label, color: series.grid.color, shape: "block" },
  ];
  const summary = describeDays(rows, car);
  const activeRow = active != null ? rows[active] : null;
  return (
    <div className="daily-chart">
      <ChartFigure
        id={`${id}-energy`}
        title="Energy each day · kWh"
        summary={summary}
        toolbar={
          <div className="tl-toolbar">
            <LegendChips chips={chips} hidden={hidden} onToggle={toggle} label="Series shown" />
            {compare && (
              <span className="tl-note">
                <i className="chart-key earlier" style={{ color: earlierInk }} aria-hidden="true" />
                Thin lines: daily averages for {previousLabel}
              </span>
            )}
          </div>
        }
        table={<DailyTable rows={rows} car={car} compact={compact} />}
        data={{ compare: compare ? "yes" : "no", days: rows.length }}
        className="chart-daily-energy"
      >
        <div className="mini-chart" ref={box} onPointerLeave={() => setActive(null)}>
          <svg
            className="daily-svg"
            width={width}
            height={height}
            viewBox={`0 0 ${width} ${height}`}
            aria-hidden="true"
            focusable="false"
          >
            <defs>
              <pattern
                id={`${id}-hatch`}
                width={6}
                height={6}
                patternUnits="userSpaceOnUse"
                patternTransform="rotate(45)"
              >
                <line x1={0} y1={0} x2={0} y2={6} stroke="var(--bg-1)" strokeWidth={1.5} strokeOpacity={0.8} />
              </pattern>
            </defs>
            {ticks.ticks.map((t) => (
              <g key={t}>
                <line className={t === 0 ? "daily-zero" : "tl-grid"} x1={gl} x2={gl + plotW} y1={y(t)} y2={y(t)} />
                {t !== 0 && (
                  <text className="tl-tick" x={gl - 6} y={y(t) + 4} textAnchor="end">
                    {tickText(Math.abs(t), ticks.step)}
                  </text>
                )}
              </g>
            ))}
            {/* Either side of the zero line, in the axis gutter: up is energy in, down is energy out. */}
            {ticks.max > 0 && (
              <text className="tl-lane-label daily-side" x={gl - 6} y={y(0) - 5} textAnchor="end">
                In ↑
              </text>
            )}
            {ticks.min < 0 && (
              <text className="tl-lane-label daily-side" x={gl - 6} y={y(0) + 14} textAnchor="end">
                Out ↓
              </text>
            )}
            {activeRow && <rect className="tl-hover" x={gl + band * active!} width={band} y={top} height={plotH} />}
            {rows.map((r, i) => {
              let up = 0,
                down = 0;
              const marks = shown.map((s) => {
                const v = drawn(r, s);
                if (!v) return null;
                const base = s.side > 0 ? up : -down;
                if (s.side > 0) up += v.kwh!;
                else down += v.kwh!;
                const end = s.side > 0 ? up : -down;
                const outer =
                  (s.side > 0 &&
                    !shown.some((o) => o.side > 0 && shown.indexOf(o) > shown.indexOf(s) && drawn(r, o))) ||
                  (s.side < 0 && !shown.some((o) => o.side < 0 && shown.indexOf(o) > shown.indexOf(s) && drawn(r, o)));
                // A 2 px surface gap between touching segments.
                const y0 = y(base) - (base === 0 ? 0 : 2 * s.side),
                  y1 = y(end);
                const partial = v.coverage < PARTIAL;
                const d = outer ? barPath(cx(i) - barW / 2, barW, y0, y1) : rectPath(cx(i) - barW / 2, barW, y0, y1);
                return (
                  <g key={s.metric} data-metric={s.metric}>
                    <path
                      className="daily-bar"
                      d={d}
                      style={{ fill: series[s.key].color, opacity: partial ? encodings.partial : 1 }}
                    />
                    {partial && <path d={d} fill={`url(#${id}-hatch)`} />}
                  </g>
                );
              });
              const any = marks.some(Boolean);
              const minCoverage = Math.min(...shown.map((s) => drawn(r, s)?.coverage ?? 1));
              return (
                <g key={r.from} data-day={r.from} data-partial={any && minCoverage < PARTIAL ? "yes" : undefined}>
                  {marks}
                  {!any && (
                    <text className="daily-na" x={cx(i)} y={y(0) - 6} textAnchor="middle">
                      n/a
                    </text>
                  )}
                  <DayTick
                    x={cx(i)}
                    y={top + plotH + 30}
                    label={r.label}
                    short={r.short}
                    band={band}
                    index={i}
                    count={rows.length}
                    stack
                  />
                </g>
              );
            })}
            {compare && prevSolar != null && !hidden.includes("solar") && (
              <g className="daily-avg" data-average="solar">
                <line x1={gl} x2={gl + plotW} y1={y(prevSolar)} y2={y(prevSolar)} />
              </g>
            )}
            {compare && prevUse != null && !hidden.includes("home") && (
              <g className="daily-avg" data-average="use">
                <line x1={gl} x2={gl + plotW} y1={y(-prevUse)} y2={y(-prevUse)} />
              </g>
            )}
            {rows.map((r, i) => (
              <rect
                key={r.from}
                className="daily-hit"
                x={gl + band * i}
                width={band}
                y={top}
                height={plotH}
                onPointerEnter={() => setActive(i)}
                onPointerDown={() => setActive(i)}
              />
            ))}
          </svg>
          {activeRow && (
            <FloatingTip x={cx(active!)} top={top} width={width}>
              <DayTip
                row={activeRow}
                car={car}
                prevUse={compare ? prevUse : null}
                prevSolar={compare ? prevSolar : null}
                previousLabel={previousLabel}
              />
            </FloatingTip>
          )}
        </div>
      </ChartFigure>
      <CostBars rows={rows} />
    </div>
  );
}

const rectPath = (x: number, w: number, y0: number, y1: number) => {
  const a = Math.min(y0, y1),
    b = Math.max(y0, y1);
  return b - a < 0.01 ? "" : `M${x},${a} H${x + w} V${b} H${x} Z`;
};

const coverageNote = (v: DayValue) =>
  v.coverage < 0.98 ? ` · ${percent(v.coverage, { fraction: true, round: "floor" })} measured` : "";
function DayTip({
  row,
  car,
  prevUse,
  prevSolar,
  previousLabel,
}: {
  row: DayRow;
  car: boolean;
  prevUse: number | null;
  prevSolar: number | null;
  previousLabel: string;
}) {
  const line = (s: Segment) => {
    const v = row.values[s.metric];
    const omitted = v.kwh != null && v.coverage < OMIT;
    return (
      <TipRow
        key={s.metric}
        color={series[s.key].color}
        kind="block"
        name={s.label}
        value={
          v.kwh == null
            ? "no readings"
            : omitted
              ? `not shown${coverageNote(v)}`
              : `${kwh(v.kwh, { precision: "table" })}${coverageNote(v)}`
        }
        muted={v.kwh == null || omitted}
      />
    );
  };
  return (
    <div className="tl-tip">
      <p className="chart-tip-label">{row.label}</p>
      <p className="chart-tip-note">In</p>
      {segments.filter((s) => s.side > 0).map(line)}
      <p className="chart-tip-note">Out</p>
      {segments.filter((s) => s.side < 0 && (s.key !== "ev" || car)).map(line)}
      {prevUse != null && <p className="chart-tip-note">{`Average home use for ${previousLabel}: ${kwh(prevUse)}`}</p>}
      {prevSolar != null && <p className="chart-tip-note">{`Average solar for ${previousLabel}: ${kwh(prevSolar)}`}</p>}
    </div>
  );
}

/** Net cost per day: neutral bars around zero (below zero is money earned), partly priced days hatched. */
function CostBars({ rows }: { rows: DayRow[] }) {
  const id = useId().replace(/:/g, "");
  const [box, width] = useWidth<HTMLDivElement>(720);
  const [active, setActive] = useState<number | null>(null);
  const values = rows.map((r) => (r.cost != null && r.costCoverage >= OMIT ? r.cost : null));
  if (!values.some((v) => v != null)) return null;
  const ticks = fittedTicks(Math.min(0, ...values.map((v) => v ?? 0)), Math.max(0, ...values.map((v) => v ?? 0)), 4);
  const compact = width < 600;
  const gl = compact ? 34 : 42,
    gr = 8,
    top = 8,
    plotH = 110,
    height = top + plotH + 26;
  const plotW = Math.max(40, width - gl - gr);
  const band = plotW / rows.length;
  const barW = Math.max(6, Math.min(56, band * 0.5));
  const y = linear([ticks.min, ticks.max], [top + plotH, top]);
  const total = values.reduce<number>((t, v) => t + (v ?? 0), 0);
  const priced = values.filter((v) => v != null).length;
  const summary = `Net cost over ${rows.length} days: ${gbp(total)}${priced < rows.length ? ` (${priced} of ${rows.length} days priced)` : ""}. Below zero means you earned more than you paid.`;
  const r = active != null ? rows[active] : null;
  return (
    <ChartFigure id={`${id}-cost`} title="Net cost each day · £" summary={summary} className="chart-daily-cost">
      <div className="mini-chart" ref={box} onPointerLeave={() => setActive(null)}>
        <svg
          className="daily-svg"
          width={width}
          height={height}
          viewBox={`0 0 ${width} ${height}`}
          aria-hidden="true"
          focusable="false"
        >
          <defs>
            <pattern
              id={`${id}-hatch`}
              width={6}
              height={6}
              patternUnits="userSpaceOnUse"
              patternTransform="rotate(45)"
            >
              <line x1={0} y1={0} x2={0} y2={6} stroke="var(--bg-1)" strokeWidth={1.5} strokeOpacity={0.8} />
            </pattern>
          </defs>
          {ticks.ticks.map((t) => (
            <g key={t}>
              <line className={t === 0 ? "daily-zero" : "tl-grid"} x1={gl} x2={gl + plotW} y1={y(t)} y2={y(t)} />
              <text className="tl-tick" x={gl - 6} y={y(t) + 4} textAnchor="end">
                {tickText(t, ticks.step)}
              </text>
            </g>
          ))}
          {r && <rect className="tl-hover" x={gl + band * active!} width={band} y={top} height={plotH} />}
          {rows.map((row, i) => {
            const v = values[i];
            const x = gl + band * (i + 0.5);
            const partial = row.costCoverage < PARTIAL;
            const d = v != null ? barPath(x - barW / 2, barW, y(0), y(v)) : "";
            return (
              <g key={row.from}>
                {d && (
                  <path className="daily-bar" d={d} style={{ fill: ink, opacity: partial ? encodings.partial : 0.6 }} />
                )}
                {d && partial && <path d={d} fill={`url(#${id}-hatch)`} />}
                {v == null && (
                  <text className="daily-na" x={x} y={y(0) - 6} textAnchor="middle">
                    n/a
                  </text>
                )}
                <DayTick
                  x={x}
                  y={top + plotH + 18}
                  label={row.label}
                  short={row.short}
                  band={band}
                  index={i}
                  count={rows.length}
                />
                <rect
                  className="daily-hit"
                  x={gl + band * i}
                  width={band}
                  y={top}
                  height={plotH}
                  onPointerEnter={() => setActive(i)}
                  onPointerDown={() => setActive(i)}
                />
              </g>
            );
          })}
        </svg>
        {r && (
          <FloatingTip x={gl + band * (active! + 0.5)} top={top} width={width}>
            <div className="tl-tip">
              <p className="chart-tip-label">{r.label}</p>
              <TipRow
                color={ink}
                kind="block"
                name={r.cost != null && r.cost < 0 ? "Net earnings" : "Net cost"}
                value={r.cost != null ? gbp(Math.abs(r.cost)) : "not priced"}
                muted={r.cost == null}
              />
              <TipRow color="transparent" name="Paid for imports" value={gbp(r.paid)} />
              <TipRow color="transparent" name="Earned from exports" value={gbp(r.earned)} />
              {r.costCoverage < 0.98 && (
                <p className="chart-tip-note">{`${percent(r.costCoverage, { fraction: true, round: "floor" })} of the day priced`}</p>
              )}
            </div>
          </FloatingTip>
        )}
      </div>
    </ChartFigure>
  );
}

function DailyTable({ rows, car, compact = false }: { rows: DayRow[]; car: boolean; compact?: boolean }) {
  const cell = (v: DayValue) =>
    v.kwh == null
      ? "—"
      : `${kwh(v.kwh, { precision: "table", unit: false })}${v.coverage < 0.98 ? ` (${percent(v.coverage, { fraction: true, round: "floor" })})` : ""}`;
  // Phones: short headings under one unit line, so the figures, not the headings, set the column widths.
  const head = (full: string, short: string) => (compact ? short : full);
  return (
    <ChartTable label="Energy each day">
      {compact && <caption className="tl-table-caption">Energy in kWh; net cost in £.</caption>}
      <thead>
        <tr>
          <th scope="col">Day</th>
          <th scope="col" className="num">
            {head("Home use, kWh", "Home")}
          </th>
          {car && (
            <th scope="col" className="num">
              {head("Car, kWh", "Car")}
            </th>
          )}
          <th scope="col" className="num">
            {head("Solar, kWh", "Solar")}
          </th>
          <th scope="col" className="num">
            {head("Grid import, kWh", "Import")}
          </th>
          <th scope="col" className="num">
            {head("Grid export, kWh", "Export")}
          </th>
          <th scope="col" className="num">
            {head("Battery charge, kWh", "Batt. in")}
          </th>
          <th scope="col" className="num">
            {head("Battery discharge, kWh", "Batt. out")}
          </th>
          <th scope="col" className="num">
            {head("Net cost, £", "Net £")}
          </th>
        </tr>
      </thead>
      <tbody>
        {rows.map((r) => (
          <tr key={r.from}>
            <td>{compact ? r.short : r.label}</td>
            <td className="num">{cell(r.values.home)}</td>
            {car && <td className="num">{cell(r.values.ev)}</td>}
            <td className="num">{cell(r.values.pv)}</td>
            <td className="num">{cell(r.values.grid_import)}</td>
            <td className="num">{cell(r.values.grid_export)}</td>
            <td className="num">{cell(r.values.battery_charge)}</td>
            <td className="num">{cell(r.values.battery_discharge)}</td>
            <td className="num">
              {gbp(r.cost)}
              {r.cost != null && r.costCoverage < 0.98
                ? ` (${percent(r.costCoverage, { fraction: true, round: "floor" })} priced)`
                : ""}
            </td>
          </tr>
        ))}
      </tbody>
    </ChartTable>
  );
}

/**
 * "7 days: home used 98.0 kWh, the car 23.0 kWh, solar made 60.0 kWh, 70.0 kWh imported, 12.0 kWh exported. Sat 3 Oct is
 * partly measured (hatched)." The missing hours themselves are named once, in the notes under the chart.
 */
export function describeDays(rows: DayRow[], car: boolean) {
  const sum = (metric: string) => {
    const vs = rows.map((r) => r.values[metric]).filter((v) => v.kwh != null && v.coverage >= OMIT);
    return vs.length ? vs.reduce((t, v) => t + v.kwh!, 0) : null;
  };
  const parts = [
    sum("home") != null ? `home used ${kwh(sum("home"))}` : "",
    car && sum("ev") != null ? `the car ${kwh(sum("ev"))}` : "",
    sum("pv") != null ? `solar made ${kwh(sum("pv"))}` : "",
    sum("grid_import") != null ? `${kwh(sum("grid_import"))} imported` : "",
    sum("grid_export") != null ? `${kwh(sum("grid_export"))} exported` : "",
  ].filter(Boolean);
  const partial = rows.filter((r) => {
    const c = Math.min(r.values.home.coverage, r.values.pv.coverage);
    return c < PARTIAL && (r.values.home.kwh != null || r.values.pv.kwh != null);
  });
  const note =
    partial.length === 0
      ? ""
      : partial.length === 1
        ? ` ${partial[0].label} is partly measured (hatched).`
        : partial.length === rows.length
          ? " Every day is partly measured (hatched)."
          : ` ${partial.length} days are partly measured (hatched).`;
  return `${rows.length} days: ${parts.join(", ")}.${note}`;
}

/** A single day as its half-hour profile (home, solar and the car), from the measured history. */
function DayProfile({ api, day, timeZone }: { api: Api; day: EnergySummary; timeZone: string }) {
  const [slots, setSlots] = useState<TimelineSlot[] | null>(null);
  const [error, setError] = useState("");
  const key = `${day.from}|${day.to}`;
  const shownDay = useRef(day.from);
  useEffect(() => {
    let active = true;
    // A refresh of the same day (today, as new readings arrive) keeps the chart on screen until the new slots land.
    if (shownDay.current !== day.from) setSlots(null);
    shownDay.current = day.from;
    setError("");
    const to = Math.min(Date.parse(day.to), Math.ceil(Date.now() / 1800000) * 1800000);
    const query = new URLSearchParams({ from: day.from, to: new Date(to).toISOString(), slotMinutes: "30" });
    api<{ slots: HistorySlot[] }>(`/telemetry/history?${query}`)
      .then((h) => {
        if (active) setSlots(h.slots.map(historySlot));
      })
      .catch((e: Error) => {
        if (active) setError(e.message);
      });
    return () => {
      active = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, key]);
  if (error) return <p className="callout">The half-hour profile is unavailable: {error}</p>;
  if (!slots)
    return (
      <p className="muted" role="status">
        Loading the half-hour profile…
      </p>
    );
  return (
    <EnergyTimeline
      slots={slots}
      timeZone={timeZone}
      preset="day"
      totals={dayTotals(day)}
      title={`${wallOf(Date.parse(day.from) + 12 * 3600000, timeZone).day} · every half-hour`}
      storageId="day-profile"
    />
  );
}

/**
 * The day's totals for the profile's caption, from the same summary as the headline figures: home use without the car when
 * the house meter includes it (as homeUse in lib/energy), solar and the car. The half-hours leave out the one in progress and
 * any ≈ timing, so their sum would be a second, slightly different number for the same thing.
 */
export function dayTotals(day: EnergySummary) {
  const value = (m: { energyKwh: number | null } | null | undefined) => m?.energyKwh ?? null;
  const home = day.home && day.loadIncludesEv ? day.home : day.metrics.load;
  return { home: value(home), solar: value(day.metrics.pv), ev: value(day.metrics.ev) };
}

/** A /telemetry/history slot as a timeline slot (measured values only). */
export function historySlot(
  s: HistorySlot & {
    loadStatus?: string;
    pvStatus?: string;
    homeStatus?: string;
    evStatus?: string;
    evEstimate?: number | null;
  },
): TimelineSlot {
  const method = (status?: string) => (status === "estimated" ? "estimated" : status === "idle" ? "idle" : "measured");
  return {
    time: s.time,
    durationMinutes: s.durationMinutes,
    loadActual: s.load ?? s.loadEstimate ?? null,
    pvActual: s.pv ?? s.pvEstimate ?? null,
    homeActual: s.home ?? s.homeEstimate ?? null,
    // A charging session whose timing is estimated (spread across a short outage) still counts, as it does for home use.
    evActual: s.ev ?? s.evEstimate ?? null,
    loadActualMethod: method(s.homeStatus ?? s.loadStatus),
    pvActualMethod: method(s.pvStatus),
    action: "",
  };
}
