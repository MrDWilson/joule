/**
 * <EnergyTimeline>: the one chart for "what happened and what Predbat plans", on one shared time axis.
 *
 * Lanes, top to bottom: the plan (an action ribbon coloured by the glossary tone, with Free / Saving / IOG tags, over a thin
 * import-price heat strip), energy (home and solar, kWh per half-hour, measured solid in the past and forecast dashed
 * ahead), the grid (bought above its zero line, sold below, measured only: Joule doesn't forecast it), the car when it is
 * part of the picture (its own lane: its hue is too close to home's to sit beside it), and the battery level with the
 * reserve line. A "Now" line with a pill crosses every lane; hovering, touching or using the arrow
 * keys picks out one slot in every lane at once. On a phone, or any touch screen, the reading appears in a fixed strip above
 * the plot instead of a floating tooltip, so a finger never hides what it is reading.
 *
 * Presets keep each page calm: "today" (Overview: about 12 h back and 24 h ahead, at most 5 legend chips: home, solar,
 * grid, car, battery; the grid was added in October 2026 because what is bought is what the bill is made of; on a phone
 * the price takes a chip and the grid lane goes without one), "plan" (the
 * whole plan, price chip and the forecast-accuracy overlay, at most 5 chips; no grid lane, the plan page is about what
 * comes next), "battery" (the battery lane alone) and "day" (one measured day: energy, grid and the car).
 */
import { useEffect, useId, useMemo, useRef, useState, type KeyboardEvent, type PointerEvent } from "react";
import type { Api } from "../../completion-types";
import { Segmented } from "../ui/Segmented";
import { Switch } from "../ui";
import { shiftLocalDays } from "../../lib/comparison";
import { gbp, kwh, pence, percent } from "../../lib/format";
import {
  ChartFigure,
  ChartTable,
  FloatingTip,
  LegendChips,
  TipRow,
  useHiddenSeries,
  useStored,
  useWidth,
  type Chip,
} from "./kit";
import { analyseGaps, gapLabel, stateNote, type SlotState } from "./gaps";
import { linePath, lineArea, stepArea, stepPath, type Point, type Step } from "./paths";
import { linear, niceTicks, tickText } from "./scale";
import { midnights, slotLabel, tickPlacement, timeTicks, wallOf } from "./timeTicks";
import { encodings, series, toneColor } from "./theme";
import {
  actionBlocks,
  attachEarlier,
  buildRows,
  describeTimeline,
  freshSoc,
  historyByStart,
  tagLabels,
  type HistorySlot,
  type Row,
  type SeriesPoint,
  type TimelineSlot,
} from "./timeline";

export type TimelinePreset = "today" | "plan" | "battery" | "day";
type Lane = "plan" | "energy" | "grid" | "car" | "battery";
export type RangeKey = "around" | "past" | "next12" | "next48" | "all";
export type Compare = "off" | "day" | "week";

const HOUR = 3600000;
const COMPACT = 600;
/** Below this plot width the price range moves from the plan lane into its legend chip, clear of the Now pill. */
const PRICE_KEY_MIN_PLOT = 520;
/** Approximate advance of a 12px Inter character, for laying out SVG labels without measuring them. */
const CHAR = 6.6;

export interface EnergyTimelineProps {
  /** History followed by the plan; use mergeTimeline(history, plan) to build it. */
  slots: TimelineSlot[];
  timeZone?: string;
  preset?: TimelinePreset;
  /** "Now", for tests and the design kit; defaults to the current time. */
  now?: number;
  /** Enables "Compare with yesterday / last week" (measured history from /telemetry/history). */
  api?: Api;
  /** Predbat's minimum battery reserve (%), drawn as a line on the battery lane. */
  reserve?: number | null;
  /** The latest battery reading, plotted at its own time. */
  currentSoc?: { value: number | null; time: string } | null;
  /** Octopus Intelligent dispatch windows, tagged "IOG slot" on the ribbon. */
  dispatches?: { start: string; end: string }[];
  /** First stored reading: compare options with no data before it are disabled. */
  dataSince?: string | null;
  /** Names the chart: the id of the heading above it. */
  labelledBy?: string;
  /** A small title, when the chart has no heading of its own. */
  title?: string;
  /** Accessible name when neither labelledBy nor title is given. */
  label?: string;
  /** Distinguishes the remembered chip choices of charts that share a preset. */
  storageId?: string;
  /** Describes the plan ahead for the caption in the page's own words (see describeTimeline). */
  describePlan?: (from: number, to: number) => string;
  /** The period's own totals for the caption (the day profile passes the summary's), instead of summing half-hours. */
  totals?: { home?: number | null; solar?: number | null; ev?: number | null };
  /** An earlier plan: the caption words its levels as what it expected, not as "now". */
  asOfPlan?: boolean;
  /** The range to open on, instead of the preset's own (Plan opens an earlier plan on the whole of it). */
  defaultRange?: RangeKey;
}

const rangeOptions: Record<
  RangeKey,
  { label: string; short: string; window: (now: number, compact: boolean) => [number, number] }
> = {
  around: {
    label: "Around now",
    short: "Now",
    window: (now, compact) => [now - 12 * HOUR, now + (compact ? 12 : 24) * HOUR],
  },
  past: { label: "Past 24 h", short: "−24 h", window: (now) => [now - 24 * HOUR, now] },
  next12: { label: "Next 12 h", short: "+12 h", window: (now) => [now, now + 12 * HOUR] },
  next48: { label: "Next 48 h", short: "+48 h", window: (now) => [now, now + 48 * HOUR] },
  all: { label: "Whole plan", short: "All", window: () => [-Infinity, Infinity] },
};
const presetRanges: Record<TimelinePreset, RangeKey[]> = {
  today: ["around", "past", "next12", "next48"],
  plan: ["all", "around", "past", "next12", "next48"],
  battery: [],
  day: [],
};
const presetLanes: Record<TimelinePreset, Lane[]> = {
  today: ["plan", "energy", "grid", "car", "battery"],
  plan: ["plan", "energy", "car", "battery"],
  battery: ["battery"],
  day: ["energy", "grid", "car"],
};
const compareOptions: { value: Compare; label: string; days: number; noun: string }[] = [
  { value: "off", label: "Nothing", days: 0, noun: "" },
  { value: "day", label: "Yesterday", days: 1, noun: "yesterday" },
  { value: "week", label: "Last week", days: 7, noun: "last week" },
];

/** Short ribbon text for an action, when it fits: "Charge → 100%", "Export → 38%". */
const shortAction: Record<string, string> = {
  charge: "Charge",
  "freeze-charge": "Hold",
  "hold-charge": "Hold",
  export: "Export",
  "freeze-export": "Solar export",
  "charge-export": "Charge, export",
};

/** The ribbon's colours by name, for the key under it: one name per colour, as the plan badges use them. */
const toneName: Record<string, string> = {
  blue: "Charge",
  green: "Export",
  violet: "Hold",
  amber: "Solar export",
};

const kwhTable = (v: number | null | undefined) => (v == null ? "—" : kwh(v, { precision: "table", unit: false }));

export function EnergyTimeline({
  slots,
  timeZone,
  preset = "today",
  now: nowProp,
  api,
  reserve,
  currentSoc,
  dispatches,
  dataSince,
  labelledBy,
  title,
  label = "Energy timeline",
  storageId,
  describePlan,
  totals,
  asOfPlan = false,
  defaultRange: openOn,
}: EnergyTimelineProps) {
  const id = useId().replace(/:/g, "");
  const [box, width] = useWidth<HTMLDivElement>(720);
  const compact = width < COMPACT;
  const [clock, setClock] = useState(() => Date.now());
  useEffect(() => {
    if (nowProp != null) return;
    const t = window.setInterval(() => setClock(Date.now()), 60000);
    return () => window.clearInterval(t);
  }, [nowProp]);
  const now = nowProp ?? clock;
  const coarse = useMemo(() => typeof window !== "undefined" && !!window.matchMedia?.("(pointer: coarse)").matches, []);

  // ------------------------------------------------------------ data
  const dispatchMs = useMemo(
    () => (dispatches ?? []).map((d) => ({ start: Date.parse(d.start), end: Date.parse(d.end) })),
    [dispatches],
  );
  const built = useMemo(() => buildRows(slots, { now, dispatches: dispatchMs }), [slots, now, dispatchMs]);
  const { car } = built;
  const allRows = built.rows;
  const ranges = presetRanges[preset];
  const [rangeChoice, setRange] = useState<RangeKey | null>(null);
  const dataStart = allRows.length ? allRows[0].start : now,
    dataEnd = allRows.length ? allRows.at(-1)!.end : now;
  const windowFor = (key: RangeKey | undefined): [number, number] => {
    if (!key) return [dataStart, dataEnd];
    const [a, b] = rangeOptions[key].window(now, compact);
    return [Math.max(a, dataStart), Math.min(b, dataEnd)];
  };
  // A range that would be mostly empty, more than half of it beyond the plan's last slot (Next 48 h on a plan that
  // runs to midnight), is offered disabled with the plan's end as the reason.
  const beyondPlan = (key: RangeKey) => {
    // "Around now" and "Past 24 h" stand on the measured past, so a short plan never takes them away.
    if (key === "all" || key === "around" || key === "past" || !allRows.length) return false;
    const [a, b] = rangeOptions[key].window(now, compact);
    return b > dataEnd && b - Math.max(a, dataEnd) > 0.5 * (b - a);
  };
  const unusable = (key: RangeKey) => {
    const [a, b] = windowFor(key);
    return !(b > a) || beyondPlan(key);
  };
  const preferred: RangeKey | undefined =
    openOn && ranges.includes(openOn) ? openOn : preset === "plan" ? (compact ? "next12" : "all") : ranges[0];
  const range: RangeKey | undefined =
    rangeChoice && ranges.includes(rangeChoice) && !unusable(rangeChoice)
      ? rangeChoice
      : preferred && !unusable(preferred)
        ? preferred
        : (ranges.find((k) => k === "all") ?? ranges.find((k) => !unusable(k)) ?? preferred);
  const planEnd = wallOf(dataEnd, timeZone);
  const planEndText = `Plan runs to ${planEnd.dayShort.split(" ")[0]} ${planEnd.time}`;
  const [d0, d1] = windowFor(range);
  const rows = useMemo(() => allRows.filter((r) => r.end > d0 && r.start < d1), [allRows, d0, d1]);
  const [hidden, toggle] = useHiddenSeries(storageId ?? preset);
  const [compare, setCompare] = useStored<Compare>(
    "joule.chart.compare",
    "off",
    (v): v is Compare => v === "off" || v === "day" || v === "week",
  );
  const [accuracy, setAccuracy] = useStored<boolean>(
    "joule.chart.accuracy",
    false,
    (v): v is boolean => typeof v === "boolean",
  );
  const compareAllowed = !!api && (preset === "today" || preset === "plan");
  const elapsed = rows.filter((r) => r.start < now);
  // Comparing and forecast accuracy are about what has happened: a range of only the half-hour in progress and later
  // (Next 12 h) has nothing finished to compare.
  const hasPast = rows.some((r) => r.phase === "past");
  const since = dataSince ? Date.parse(dataSince) : NaN;
  const compareDisabled = (days: number) =>
    !hasPast || (Number.isFinite(since) && shiftLocalDays(elapsed.at(-1)!.end, -days, timeZone ?? "UTC") <= since);
  const activeCompare: Compare =
    compareAllowed && compare !== "off" && !compareDisabled(compareOptions.find((o) => o.value === compare)!.days)
      ? compare
      : "off";
  const earlier = useEarlier(api, activeCompare, elapsed, timeZone, car);
  const shift = useMemo(() => {
    const days = compareOptions.find((o) => o.value === activeCompare)!.days;
    return (ms: number) => shiftLocalDays(ms, -days, timeZone ?? "UTC");
  }, [activeCompare, timeZone]);
  const viewRows = useMemo(() => {
    const copy = rows.map((r) => ({ ...r }));
    if (earlier.map) attachEarlier(copy, earlier.map, shift, now);
    return copy;
  }, [rows, earlier.map, shift, now]);
  const showEarlier = activeCompare !== "off" && viewRows.some((r) => r.earlier != null);

  // ------------------------------------------------------------ lanes and layout
  const carInRange = car && rows.some((r) => (r.ev.actual ?? 0) > 0.05 || (r.ev.forecast ?? 0) > 0.05);
  const gridInRange = rows.some((r) => r.grid.importRaw != null || r.grid.exportRaw != null);
  const nowVisible = now > d0 && now < d1 && preset !== "day";
  const gl = compact ? 30 : 38,
    gr = compact ? 6 : 12;
  const plotW = Math.max(40, width - gl - gr);
  // On a narrow plot the price range lives in the Price legend chip (which can then hide the strip), not beside the
  // Now pill; the Plan page always has the chip.
  const priceInChip = plotW < PRICE_KEY_MIN_PLOT;
  const priceChip = presetLanes[preset].includes("plan") && (preset === "plan" || priceInChip);
  // Five chips at most: where the price needs a chip (a phone's Today), the grid lane is always drawn and named by its
  // own label instead of a chip, like the plan lane.
  const gridChip = !priceChip;
  const lanes = presetLanes[preset].filter((l) => {
    if (l === "car") return carInRange && !hidden.includes("ev");
    if (l === "grid") return gridInRange && (!gridChip || !hidden.includes("grid"));
    if (l === "battery") return !hidden.includes("battery") || preset === "battery";
    return true;
  });
  const showPrice = lanes.includes("plan") && !(priceChip && hidden.includes("price"));
  const x = linear([d0, d1 > d0 ? d1 : d0 + HOUR], [gl, gl + plotW]);
  // The ribbon's colours, named under it: only the actions in view, once each, in the order they first appear.
  const blocks = lanes.includes("plan") ? actionBlocks(viewRows) : [];
  const actionKey: { tone: string; label: string; color: string }[] = [];
  for (const b of blocks) {
    if (b.action.tone === "neutral" || actionKey.some((k) => k.tone === b.action.tone)) continue;
    const label = toneName[b.action.tone] ?? shortAction[b.action.key] ?? b.action.label;
    actionKey.push({ tone: b.action.tone, label, color: toneColor[b.action.tone] });
  }
  const KEY_ROW = actionKey.length ? 20 : 0;
  // A lane with ticks leaves 14px between its unit label and the top tick; the plan lane has none.
  const LABEL = 18,
    UNIT_LABEL = 26,
    GAP = 14;
  let y = nowVisible ? 24 : 4;
  const geom: Partial<Record<Lane, { top: number; bottom: number; label: number }>> = {};
  for (const lane of lanes) {
    const head = lane === "plan" ? LABEL : UNIT_LABEL;
    const plot =
      lane === "plan"
        ? 18 + (showPrice ? 9 : 0) + KEY_ROW
        : lane === "energy"
          ? preset === "day"
            ? compact
              ? 150
              : 190
            : compact
              ? 128
              : 172
          : lane === "car"
            ? 40
            : lane === "grid"
              ? compact
                ? 52
                : 64
              : preset === "battery"
                ? compact
                  ? 84
                  : 96
                : compact
                  ? 58
                  : 76;
    geom[lane] = { label: y + 12, top: y + head, bottom: y + head + plot };
    y += head + plot + GAP;
  }
  const axisTop = y - GAP + 6;
  const height = axisTop + 30;
  const top = Object.values(geom)[0]?.top ?? 0;
  const bottom = Object.values(geom).at(-1)?.bottom ?? height;

  // Energy scale: today's own values only, so an earlier spike never squashes them.
  const showHome = !hidden.includes("home"),
    showSolar = !hidden.includes("solar");
  const energyValues: number[] = [];
  for (const r of viewRows) {
    if (showHome) energyValues.push(r.home.actual ?? 0, r.phase !== "past" || accuracy ? (r.home.forecast ?? 0) : 0);
    if (showSolar) energyValues.push(r.solar.actual ?? 0, r.phase !== "past" || accuracy ? (r.solar.forecast ?? 0) : 0);
    if (showEarlier && showHome) energyValues.push(r.earlier ?? 0);
  }
  const energyTicks = niceTicks(Math.max(0, ...energyValues), compact ? 2 : 4, 0.2);
  const eg = geom.energy;
  const ey = eg ? linear([0, energyTicks.max], [eg.bottom, eg.top]) : null;
  const carTicks = niceTicks(Math.max(0, ...viewRows.flatMap((r) => [r.ev.actual ?? 0, r.ev.forecast ?? 0])), 1, 0.5);
  const cg = geom.car;
  const cy = cg ? linear([0, carTicks.max], [cg.bottom, cg.top]) : null;
  const bg = geom.battery;
  const by = bg ? linear([0, 100], [bg.bottom, bg.top]) : null;
  // Grid: bought above the zero line, sold below, each on its own labelled scale. Selling has the lower 30% of the lane, so
  // buying (what the bill is made of) leads, and a little export is still visible.
  const gg = geom.grid;
  const gridIn = niceTicks(Math.max(0, ...viewRows.map((r) => r.grid.import ?? 0)), 1, 0.5);
  const soldMax = Math.max(0, ...viewRows.map((r) => r.grid.export ?? 0));
  const gridOut = soldMax > 0.005 ? niceTicks(soldMax, 1, 0.5) : null;
  const gridZero = gg ? (gridOut ? gg.top + (gg.bottom - gg.top) * 0.7 : gg.bottom) : 0;
  const gyIn = gg ? linear([0, gridIn.max], [gridZero, gg.top]) : null;
  const gyOut = gg && gridOut ? linear([0, gridOut.max], [gridZero, gg.bottom]) : null;

  // ------------------------------------------------------------ marks
  const xs = (r: Row) => ({ x0: x(Math.max(r.start, d0)), x1: x(Math.min(r.end, d1)) });
  const steps = (pick: (r: Row) => number | null, scale: (v: number) => number, include: (r: Row) => boolean) =>
    viewRows.map((r): Step | null => {
      const v = include(r) ? pick(r) : null;
      return v == null ? null : { ...xs(r), y: scale(v) };
    });
  const gaps = (key: "home" | "solar" | "ev") =>
    analyseGaps(
      viewRows.map((r) => r[key].state),
      viewRows.map((r) => r.start),
      viewRows.map((r) => r.end),
    );
  const homeGaps = gaps("home"),
    solarGaps = gaps("solar"),
    evGaps = gaps("ev");
  const socGaps = analyseGaps(
    viewRows.map((r) => r.soc.state),
    viewRows.map((r) => r.start),
    viewRows.map((r) => r.end),
  );
  const bridge = (g: ReturnType<typeof analyseGaps>, pick: (r: Row) => number | null, scale: (v: number) => number) =>
    g.bridges.map(([a, b]) => {
      const ra = viewRows[a],
        rb = viewRows[b];
      return `M${xs(ra).x1},${scale(pick(ra) ?? 0)} L${xs(rb).x0},${scale(pick(rb) ?? 0)}`;
    });
  const wash = (g: ReturnType<typeof analyseGaps>, metric: string) =>
    g.bands.map(([a, b]) => ({
      x0: xs(viewRows[a]).x0,
      x1: xs(viewRows[b]).x1,
      label: gapLabel(metric, slotLabel(viewRows[a].start, viewRows[b].end, timeZone, false)),
      from: a,
      to: b,
    }));
  const washes = {
    home: showHome && lanes.includes("energy") ? wash(homeGaps, "home") : [],
    solar: showSolar && lanes.includes("energy") ? wash(solarGaps, "solar") : [],
    ev: lanes.includes("car") ? wash(evGaps, "ev") : [],
    soc: lanes.includes("battery") ? wash(socGaps, "soc") : [],
  };
  const bandFor = (i: number) =>
    [
      ...washes.home.filter((w) => i >= w.from && i <= w.to),
      ...washes.solar.filter((w) => i >= w.from && i <= w.to),
      ...washes.ev.filter((w) => i >= w.from && i <= w.to),
      ...washes.soc.filter((w) => i >= w.from && i <= w.to),
    ].map((w) => w.label);
  const prices = viewRows.map((r) => r.price.import).filter((p): p is number => p != null);
  const pMin = prices.length ? Math.min(...prices) : 0,
    pMax = prices.length ? Math.max(...prices) : 1;
  const priceShade = (p: number | null) =>
    p == null
      ? "transparent"
      : `color-mix(in oklab, var(--price-high, #9fb0bf) ${Math.round(((p - pMin) / (pMax - pMin || 1)) * 100)}%, var(--price-low, #17222d))`;
  const ticks = timeTicks(d0, d1, timeZone, Math.max(2, Math.min(9, Math.floor(plotW / (compact ? 62 : 78)))));
  const dayLines = midnights(d0, d1, timeZone);

  // Battery points: measured at the sample's own time, the forecast from now on.
  const socActual: (Point | null)[] = [];
  if (by) {
    viewRows.forEach((r, i) => {
      if (r.phase === "future") return;
      if (r.soc.state === "missing" && !socGaps.bridges.some(([a, b]) => i > a && i < b)) socActual.push(null);
      if (r.soc.start != null && r.start >= d0) socActual.push({ x: x(r.start), y: by(r.soc.start) });
      if (r.soc.end != null && r.end <= d1) socActual.push({ x: x(r.end), y: by(r.soc.end) });
    });
    const t = currentSoc?.time ? Date.parse(currentSoc.time) : NaN;
    if (
      currentSoc?.value != null &&
      Number.isFinite(t) &&
      t > d0 &&
      t <= Math.min(now, d1) &&
      t >= (lastX(socActual, x) ?? -Infinity)
    )
      socActual.push({ x: x(t), y: by(currentSoc.value) });
  }
  const socForecast: (Point | null)[] = [];
  if (by) {
    const ahead = viewRows.filter((r) => r.end > now && r.soc.forecastStart != null);
    ahead.forEach((r, i) => {
      const startAt = Math.max(r.start, now, d0);
      let level = r.soc.forecastStart!;
      if (r.start < now && r.soc.forecastEnd != null)
        level =
          r.soc.forecastStart! + ((r.soc.forecastEnd - r.soc.forecastStart!) * (now - r.start)) / (r.end - r.start);
      socForecast.push({ x: x(startAt), y: by(level) });
      const last = i === ahead.length - 1;
      if (last && r.soc.forecastEnd != null) socForecast.push({ x: x(Math.min(r.end, d1)), y: by(r.soc.forecastEnd) });
    });
  }
  const showBattery = !!by;

  // ------------------------------------------------------------ interaction
  const [active, setActive] = useState<number | null>(null);
  const [pointer, setPointer] = useState<"mouse" | "touch" | "key" | null>(null);
  // The battery outlook sits in a narrow side panel on desktop: a floating tooltip fits there; phones still get the strip.
  const strip = (compact && preset !== "battery") || coarse;
  const activeRow = active != null ? viewRows[active] : undefined;
  const indexAt = (px: number) => {
    const t = x.invert(px);
    const i = viewRows.findIndex((r) => r.start <= t && t < r.end);
    if (i >= 0) return i;
    return t < (viewRows[0]?.start ?? 0) ? 0 : viewRows.length - 1;
  };
  const surface = useRef<HTMLDivElement>(null);
  const onPointer = (e: PointerEvent<HTMLDivElement>) => {
    if (!viewRows.length) return;
    const rect = e.currentTarget.getBoundingClientRect();
    setPointer(e.pointerType === "touch" ? "touch" : "mouse");
    setActive(indexAt(e.clientX - rect.left + gl));
  };
  const onKey = (e: KeyboardEvent<HTMLDivElement>) => {
    if (!viewRows.length) return;
    const current =
      active ??
      Math.max(
        0,
        viewRows.findIndex((r) => r.phase === "current"),
      );
    const next =
      e.key === "ArrowRight"
        ? Math.min(viewRows.length - 1, current + 1)
        : e.key === "ArrowLeft"
          ? Math.max(0, current - 1)
          : e.key === "Home"
            ? 0
            : e.key === "End"
              ? viewRows.length - 1
              : e.key === "Escape"
                ? null
                : undefined;
    if (next === undefined) return;
    e.preventDefault();
    setPointer("key");
    setActive(next);
  };

  // ------------------------------------------------------------ legend, caption, table
  const chips: Chip[] = [];
  if (lanes.includes("energy")) {
    chips.push({ key: "home", label: series.home.label, color: series.home.color });
    chips.push({ key: "solar", label: series.solar.label, color: series.solar.color });
  }
  if (gridInRange && gridChip && presetLanes[preset].includes("grid"))
    chips.push({ key: "grid", label: series.grid.label, color: series.grid.color });
  if (carInRange && presetLanes[preset].includes("car"))
    chips.push({ key: "ev", label: series.ev.label, color: series.ev.color });
  if (presetLanes[preset].includes("battery") && preset !== "battery")
    chips.push({ key: "battery", label: series.battery.label, color: series.battery.color });
  if (priceChip && (prices.length || preset === "plan"))
    chips.push({
      key: "price",
      label:
        priceInChip && prices.length ? `Price ${pence(pMin, { unit: "p" })}–${pence(pMax, { unit: "p" })}` : "Price",
      color: "var(--price-high, #9fb0bf)",
      shape: "strip",
    });
  const wall = (ms: number) => wallOf(ms, timeZone).time;
  const summary = describeTimeline(viewRows, {
    now,
    wall,
    car,
    pastLabel: preset === "day" && viewRows.length ? wallOf(viewRows[0].start, timeZone).day : undefined,
    day: (ms) => wallOf(ms, timeZone).dayShort,
    batteryOnly: preset === "battery",
    socNow: freshSoc(currentSoc, now),
    describePlan,
    totals,
    asOfPlan,
  });
  const drawnSeries = [
    showHome && lanes.includes("energy") && "home",
    showSolar && lanes.includes("energy") && "solar",
    lanes.includes("grid") && "grid",
    lanes.includes("car") && "ev",
    showBattery && "battery",
  ].filter(Boolean) as string[];
  const compareNote =
    activeCompare === "off"
      ? ""
      : earlier.error
        ? `Comparison unavailable: ${earlier.error}`
        : earlier.loading
          ? "Loading the comparison…"
          : showEarlier
            ? `Dashed grey line: home use ${compareOptions.find((o) => o.value === activeCompare)!.noun}, same times of day`
            : `No readings from ${compareOptions.find((o) => o.value === activeCompare)!.noun} for these times`;

  const readout = activeRow ? readoutText(activeRow, timeZone, carInRange, showEarlier) : "";
  const toolbar =
    ranges.length || chips.length > 1 ? (
      <div className="tl-toolbar">
        {ranges.length > 0 && (
          <Segmented<RangeKey>
            label="Time range"
            size="sm"
            value={range!}
            onChange={(v) => {
              setRange(v);
              setActive(null);
            }}
            options={ranges.map((k) => {
              const past = beyondPlan(k);
              return {
                value: k,
                // Phones get the short labels ("−24 h", "+12 h") so all five fit; the full name stays accessible.
                label: compact ? rangeOptions[k].short : rangeOptions[k].label,
                ariaLabel: compact ? rangeOptions[k].label : undefined,
                disabled: unusable(k),
                title: past ? planEndText : undefined,
              };
            })}
          />
        )}
        <LegendChips chips={chips} hidden={hidden} onToggle={toggle} scroll={compact} />
      </div>
    ) : null;

  const table = (
    <TimelineTable
      rows={viewRows}
      timeZone={timeZone}
      car={carInRange}
      grid={gridInRange}
      energy={lanes.includes("energy")}
      compact={compact}
    />
  );
  const footer =
    compareAllowed || preset === "plan" ? (
      <div className="tl-options">
        {compareAllowed && (
          <div className="tl-compare">
            <span className="tl-compare-label" aria-hidden="true">
              Compare home use with
            </span>
            <Segmented<Compare>
              label="Compare home use with"
              size="sm"
              value={activeCompare}
              onChange={(v) => setCompare(v)}
              options={compareOptions.map((o) => ({
                value: o.value,
                label: o.label,
                disabled: o.days > 0 && compareDisabled(o.days),
                title: o.days > 0 && !hasPast ? "Pick a range that includes the past" : undefined,
              }))}
            />
          </div>
        )}
        {preset === "plan" && (
          <label className="tl-switch" title={hasPast ? undefined : "Pick a range that includes the past"}>
            <Switch
              checked={accuracy && hasPast}
              onCheckedChange={setAccuracy}
              label="Show forecast accuracy"
              disabled={!hasPast}
            />
            Show forecast accuracy
          </label>
        )}
        {compareNote && (
          <span className="tl-note" role={earlier.error ? "alert" : "status"}>
            {showEarlier && <i className="chart-key earlier" aria-hidden="true" />}
            {compareNote}
          </span>
        )}
      </div>
    ) : null;

  if (!allRows.length)
    return (
      <div className="chart-empty" role="status">
        Waiting for readings and a plan. The chart fills in as they arrive.
      </div>
    );

  const tipTop = (geom.energy ?? geom.battery ?? geom.plan)!.top;
  const activeX = activeRow ? (xs(activeRow).x0 + xs(activeRow).x1) / 2 : 0;
  // One name for the unit on every lane.
  const unit = compact ? "kWh / ½ h" : "kWh per half-hour";
  const energyUnit = compact ? unit : `Energy · ${unit}`;

  // The price key sits right-aligned on the plan lane's label row; when the Now pill would cover it, it moves to just
  // after "Plan"; when both places are covered (or the plot is narrow) the legend chip carries the range instead.
  const pillX = nowVisible ? clampPill(x(now), gl, gl + plotW) : null;
  const priceKeyText = `Price ${pence(pMin, { unit: "p" })}`,
    priceKeyMax = pence(pMax, { unit: "p" });
  const priceKeyWidth = (priceKeyText.length + priceKeyMax.length) * CHAR + 12 + 30;
  const clearOfPill = (from: number, to: number) => pillX == null || to < pillX - 22 || from > pillX + 22;
  const priceKey =
    showPrice && prices.length && !priceInChip && geom.plan
      ? clearOfPill(gl + plotW - priceKeyWidth, gl + plotW)
        ? { x: gl + plotW, anchor: "end" as const }
        : clearOfPill(gl + 40, gl + 40 + priceKeyWidth)
          ? { x: gl + 40, anchor: "start" as const }
          : null
      : null;

  // "Reserve 4%" goes where the battery line isn't: left of the plot above the line, else below it, else at the right.
  const reservePlace = (() => {
    if (!by || !bg || reserve == null || reserve <= 0) return null;
    const ry = by(reserve);
    const labelW = `Reserve ${percent(reserve)}`.length * CHAR + 8;
    const line = [...socActual, ...socForecast].filter((p): p is Point => !!p);
    const clear = (x0: number, x1: number, top: number, bottom: number) =>
      !line.some((p, i) => {
        const next = line[i + 1];
        const inX = (p.x >= x0 && p.x <= x1) || (next && p.x <= x0 && next.x >= x1);
        return inX && p.y >= top - 2 && p.y <= bottom + 2;
      });
    const above = { y: ry - 4, top: ry - 16, bottom: ry - 1 },
      below = { y: ry + 13, top: ry + 1, bottom: ry + 16 };
    const roomBelow = below.bottom <= bg.bottom - 1;
    const left = { x: gl + 4, anchor: "start" as const, x0: gl, x1: gl + 4 + labelW };
    const right = { x: gl + plotW - 4, anchor: "end" as const, x0: gl + plotW - 4 - labelW, x1: gl + plotW };
    for (const side of [left, right])
      for (const v of roomBelow ? [above, below] : [above])
        if (v.top >= bg.top && clear(side.x0, side.x1, v.top, v.bottom))
          return { x: side.x, y: v.y, anchor: side.anchor };
    return { x: left.x, y: above.y, anchor: left.anchor };
  })();
  return (
    <ChartFigure
      id={id}
      labelledBy={labelledBy}
      title={title}
      label={label}
      summary={summary}
      toolbar={toolbar}
      table={table}
      footer={footer}
      className={`tl tl-${preset}${compact ? " tl-compact" : ""}`}
      data={{
        preset,
        start: new Date(d0).toISOString(),
        end: new Date(d1).toISOString(),
        compare: showEarlier ? activeCompare : "off",
        "gap-bands": washes.home.length + washes.solar.length + washes.ev.length + washes.soc.length,
        bridges: homeGaps.bridges.length + solarGaps.bridges.length + socGaps.bridges.length,
        series: drawnSeries.join(" "),
        range: range ?? "all",
      }}
    >
      {strip && (
        <div className="tl-readout" aria-hidden="true">
          {activeRow ? (
            <>
              <strong>{readout.split("\n")[0]}</strong>
              <span>{readout.split("\n").slice(1).join(" · ")}</span>
            </>
          ) : (
            <span className="tl-readout-hint">Touch the chart to read a half-hour</span>
          )}
        </div>
      )}
      <div className="tl-plot" ref={box}>
        <svg
          width={width}
          height={height}
          viewBox={`0 0 ${width} ${height}`}
          className="tl-svg"
          aria-hidden="true"
          focusable="false"
        >
          <defs>
            <clipPath id={`${id}-clip`}>
              <rect x={gl} y={0} width={plotW} height={height} />
            </clipPath>
          </defs>
          {/* Day boundaries */}
          {dayLines.map((t) => (
            <line key={t} className="tl-day-line" x1={x(t)} x2={x(t)} y1={top} y2={bottom} />
          ))}
          {/* Hover band */}
          {activeRow && (
            <rect
              className="tl-hover"
              x={xs(activeRow).x0}
              width={Math.max(1, xs(activeRow).x1 - xs(activeRow).x0)}
              y={top - 4}
              height={bottom - top + 8}
            />
          )}

          {/* ---------------- plan lane */}
          {geom.plan && (
            <g className="tl-lane tl-lane-plan">
              <text className="tl-lane-label" x={gl} y={geom.plan.label}>
                Plan
              </text>
              {priceKey && (
                <text
                  className="tl-lane-label tl-price-key"
                  x={priceKey.x}
                  y={geom.plan.label}
                  textAnchor={priceKey.anchor}
                >
                  {priceKeyText}
                  <tspan className="tl-price-ramp" dx={6}>
                    ▁▃▅▇
                  </tspan>
                  <tspan dx={6}>{priceKeyMax}</tspan>
                </text>
              )}
              {actionKey.length > 0 && (
                <g className="tl-action-key" data-actions={actionKey.map((k) => k.label).join("|")}>
                  {(() => {
                    let kx = gl;
                    const ky = geom.plan.top + 18 + (showPrice ? 9 : 0) + 8;
                    return actionKey.map((k) => {
                      const w = 10 + 5 + k.label.length * CHAR;
                      if (kx + w > gl + plotW) return null;
                      const at = kx;
                      kx += w + 14;
                      return (
                        <g key={k.label}>
                          <rect x={at} y={ky} width={10} height={10} rx={3} style={{ fill: k.color }} />
                          <text className="tl-action-key-label" x={at + 15} y={ky + 9}>
                            {k.label}
                          </text>
                        </g>
                      );
                    });
                  })()}
                </g>
              )}
              <g clipPath={`url(#${id}-clip)`}>
                <rect className="tl-track" x={gl} y={geom.plan.top} width={plotW} height={18} rx={4} />
                {blocks.map((b, i) => {
                  const x0 = x(Math.max(b.start, d0)) + 1,
                    x1 = x(Math.min(b.end, d1)) - 1;
                  const w = Math.max(1, x1 - x0);
                  const neutral = b.action.tone === "neutral";
                  // Powering the home is the default: it shows as the empty track, so only real actions stand out.
                  if (neutral && !b.tags.length) return null;
                  const text = ribbonText(b.action, b.tags);
                  const fits = text && w > text.length * 6.6 + 10;
                  const past = b.end <= now;
                  return (
                    <g
                      key={i}
                      className={`tl-block tl-block-${b.action.tone}`}
                      data-action={b.action.key}
                      data-tags={b.tags.join(" ") || undefined}
                    >
                      <rect
                        x={x0}
                        y={geom.plan!.top}
                        width={w}
                        height={18}
                        rx={4}
                        className={neutral ? "tl-block-outline" : undefined}
                        style={neutral ? undefined : { fill: toneColor[b.action.tone], opacity: past ? 0.45 : 0.9 }}
                      />
                      {b.tags.length > 0 && (
                        <rect className="tl-tag-mark" x={x0} y={geom.plan!.top + 15} width={w} height={3} rx={1.5} />
                      )}
                      {fits && (
                        <text
                          className={`tl-block-text${neutral ? " neutral" : ""}`}
                          x={x0 + 6}
                          y={geom.plan!.top + 13}
                        >
                          {text}
                        </text>
                      )}
                    </g>
                  );
                })}
                {showPrice &&
                  viewRows.map((r) => (
                    <rect
                      key={r.start}
                      className="tl-price"
                      x={xs(r).x0}
                      y={geom.plan!.top + 21}
                      // A hairline between slots, so the strip reads as per-slot cells rather than a progress bar.
                      width={Math.max(0.5, xs(r).x1 - xs(r).x0 - (xs(r).x1 - xs(r).x0 > 4 ? 1 : 0))}
                      height={6}
                      rx={1}
                      style={{ fill: priceShade(r.price.import) }}
                    />
                  ))}
              </g>
            </g>
          )}

          {/* ---------------- energy lane */}
          {eg && ey && (
            <g className="tl-lane tl-lane-energy">
              <text className="tl-lane-label" x={gl} y={eg.label}>
                {energyUnit}
              </text>
              {nowVisible && x(now) - 8 - 60 > gl + energyUnit.length * CHAR + 8 && gl + plotW - x(now) > 80 && (
                <>
                  <text className="tl-lane-label tl-phase" x={x(now) - 8} y={eg.label} textAnchor="end">
                    measured
                  </text>
                  <text className="tl-lane-label tl-phase" x={x(now) + 8} y={eg.label}>
                    forecast
                  </text>
                </>
              )}
              {energyTicks.ticks.map((t) => (
                <g key={t}>
                  <line className="tl-grid" x1={gl} x2={gl + plotW} y1={ey(t)} y2={ey(t)} />
                  <text className="tl-tick" x={gl - 6} y={ey(t) + 4} textAnchor="end">
                    {tickText(t, energyTicks.step)}
                  </text>
                </g>
              ))}
              <g clipPath={`url(#${id}-clip)`}>
                {mergeWashes(washes.home, washes.solar).map((w, i) => (
                  <g key={i} className="tl-wash" data-series={w.series}>
                    <rect x={w.x0} width={Math.max(1, w.x1 - w.x0)} y={eg.top} height={eg.bottom - eg.top} />
                    {w.x1 - w.x0 > w.text.length * 6 + 8 && (
                      <text className="tl-wash-label" x={(w.x0 + w.x1) / 2} y={eg.top + 14} textAnchor="middle">
                        {w.text}
                      </text>
                    )}
                  </g>
                ))}
                {showSolar && (
                  <SeriesMarks
                    name="solar"
                    color={series.solar.color}
                    pick={(r) => r.solar}
                    steps={steps}
                    scale={ey}
                    baseline={eg.bottom}
                    bridges={bridge(solarGaps, (r) => r.solar.actual, ey)}
                    accuracy={accuracy && preset === "plan"}
                    area
                  />
                )}
                {showHome && (
                  <SeriesMarks
                    name="home"
                    color={series.home.color}
                    pick={(r) => r.home}
                    steps={steps}
                    scale={ey}
                    baseline={eg.bottom}
                    bridges={bridge(homeGaps, (r) => r.home.actual, ey)}
                    accuracy={accuracy && preset === "plan"}
                  />
                )}
                {/* The earlier period: dashed, above the fills and the home line, so it reads as a reference. */}
                {showEarlier && showHome && (
                  <path
                    className="tl-earlier"
                    data-series="earlier"
                    d={stepPath(
                      steps(
                        (r) => r.earlier ?? null,
                        ey,
                        (r) => r.start < now,
                      ),
                    )}
                  />
                )}
              </g>
            </g>
          )}

          {/* ---------------- grid lane */}
          {gg && gyIn && (
            <g className="tl-lane tl-lane-grid">
              <text className="tl-lane-label" x={gl} y={gg.label}>
                {`Grid · ${unit}`}
              </text>
              {gl + plotW - (`Grid · ${unit}`.length + 22) * CHAR > gl && (
                <text className="tl-lane-label tl-grid-key" x={gl + plotW} y={gg.label} textAnchor="end">
                  {gyOut ? "bought ↑ · sold ↓" : "bought"}
                </text>
              )}
              <line className="daily-zero" x1={gl} x2={gl + plotW} y1={gridZero} y2={gridZero} />
              <line className="tl-grid" x1={gl} x2={gl + plotW} y1={gyIn(gridIn.max)} y2={gyIn(gridIn.max)} />
              <text className="tl-tick" x={gl - 6} y={gyIn(gridIn.max) + 4} textAnchor="end">
                {tickText(gridIn.max, gridIn.step)}
              </text>
              <text className="tl-tick" x={gl - 6} y={gridZero + 4} textAnchor="end">
                0
              </text>
              {/* The sold side has its floor line but no tick: a label there crowds the zero and the next lane's scale.
                  The readout and the table give the figures. */}
              {gyOut && gridOut && (
                <line className="tl-grid" x1={gl} x2={gl + plotW} y1={gyOut(gridOut.max)} y2={gyOut(gridOut.max)} />
              )}
              <g clipPath={`url(#${id}-clip)`}>
                <path
                  className="tl-area"
                  d={stepArea(
                    steps(
                      (r) => r.grid.import,
                      gyIn,
                      (r) => r.phase === "past",
                    ),
                    gridZero,
                  )}
                  style={{ fill: series.grid.color, fillOpacity: 0.28 }}
                />
                <path
                  className="tl-series tl-measured"
                  data-series="grid"
                  d={stepPath(
                    steps(
                      (r) => r.grid.import,
                      gyIn,
                      (r) => r.phase === "past",
                    ),
                  )}
                  style={{ stroke: series.grid.color, strokeWidth: encodings.measured.strokeWidth }}
                />
                {gyOut && (
                  <path
                    className="tl-area tl-grid-sold"
                    data-series="grid"
                    d={stepArea(
                      steps(
                        (r) => r.grid.export,
                        gyOut,
                        (r) => r.phase === "past",
                      ),
                      gridZero,
                    )}
                    style={{ fill: series.grid.color, fillOpacity: 0.16 }}
                  />
                )}
                {gyOut && (
                  <path
                    className="tl-series tl-measured tl-grid-sold"
                    data-series="grid"
                    d={stepPath(
                      steps(
                        (r) => ((r.grid.export ?? 0) > 0.005 ? r.grid.export : null),
                        gyOut,
                        (r) => r.phase === "past",
                      ),
                    )}
                    style={{ stroke: series.grid.color, strokeOpacity: 0.55, strokeWidth: 1.5 }}
                  />
                )}
              </g>
            </g>
          )}

          {/* ---------------- car lane */}
          {cg && cy && (
            <g className="tl-lane tl-lane-car">
              <text className="tl-lane-label" x={gl} y={cg.label}>
                {`Car · ${unit}`}
              </text>
              <line className="tl-grid" x1={gl} x2={gl + plotW} y1={cg.bottom} y2={cg.bottom} />
              <line className="tl-grid" x1={gl} x2={gl + plotW} y1={cy(carTicks.max)} y2={cy(carTicks.max)} />
              <text className="tl-tick" x={gl - 6} y={cy(carTicks.max) + 4} textAnchor="end">
                {tickText(carTicks.max, carTicks.step)}
              </text>
              <g clipPath={`url(#${id}-clip)`}>
                {washes.ev.map((w, i) => (
                  <rect
                    key={i}
                    className="tl-wash"
                    data-series="ev"
                    x={w.x0}
                    width={Math.max(1, w.x1 - w.x0)}
                    y={cg.top}
                    height={cg.bottom - cg.top}
                  />
                ))}
                <SeriesMarks
                  name="ev"
                  color={series.ev.color}
                  pick={(r) => r.ev}
                  steps={steps}
                  scale={cy}
                  baseline={cg.bottom}
                  bridges={[]}
                  accuracy={false}
                  area
                  quietZero
                />
              </g>
            </g>
          )}

          {/* ---------------- battery lane */}
          {bg && by && (
            <g className="tl-lane tl-lane-battery">
              <text className="tl-lane-label" x={gl} y={bg.label}>
                Battery %
              </text>
              {[0, 50, 100].map((t) => (
                <g key={t}>
                  <line className="tl-grid" x1={gl} x2={gl + plotW} y1={by(t)} y2={by(t)} />
                  {(t !== 50 || !compact || preset === "battery") && (
                    <text className="tl-tick" x={gl - 6} y={by(t) + 4} textAnchor="end">
                      {t}
                    </text>
                  )}
                </g>
              ))}
              <g clipPath={`url(#${id}-clip)`}>
                {washes.soc.map((w, i) => (
                  <rect
                    key={i}
                    className="tl-wash"
                    data-series="battery"
                    x={w.x0}
                    width={Math.max(1, w.x1 - w.x0)}
                    y={bg.top}
                    height={bg.bottom - bg.top}
                  />
                ))}
                {reserve != null && reserve > 0 && (
                  <g className="tl-reserve">
                    <line x1={gl} x2={gl + plotW} y1={by(reserve)} y2={by(reserve)} />
                  </g>
                )}
                <path
                  className="tl-area"
                  d={lineArea(socActual, bg.bottom)}
                  style={{ fill: series.battery.color, fillOpacity: encodings.area }}
                />
                <path
                  className="tl-series tl-measured"
                  data-series="battery"
                  d={linePath(socActual)}
                  style={{ stroke: series.battery.color, strokeWidth: encodings.measured.strokeWidth }}
                />
                {socGaps.bridges.map(([a, b], i) => {
                  const ra = viewRows[a],
                    rb = viewRows[b];
                  const ya = ra.soc.end,
                    yb = rb.soc.start ?? rb.soc.end;
                  if (ya == null || yb == null) return null;
                  return (
                    <path
                      key={i}
                      className="tl-bridge"
                      d={`M${x(ra.end)},${by(ya)} L${x(rb.soc.start != null ? rb.start : rb.end)},${by(yb)}`}
                      style={{
                        stroke: series.battery.color,
                        strokeOpacity: encodings.bridge.strokeOpacity,
                        strokeWidth: 2,
                      }}
                    />
                  );
                })}
                <path
                  className="tl-series tl-forecast"
                  data-series="battery"
                  d={linePath(socForecast)}
                  style={{
                    stroke: series.battery.color,
                    strokeDasharray: encodings.forecast.strokeDasharray,
                    strokeWidth: 2,
                  }}
                />
                {socActual.length > 0 && lastPoint(socActual) && (
                  <circle
                    className="tl-dot"
                    cx={lastPoint(socActual)!.x}
                    cy={lastPoint(socActual)!.y}
                    r={4}
                    style={{ fill: series.battery.color }}
                  />
                )}
              </g>
            </g>
          )}

          {/* ---------------- now */}
          {nowVisible && (
            <g className="tl-now" data-now={new Date(now).toISOString()}>
              <line x1={x(now)} x2={x(now)} y1={18} y2={bottom} />
              <rect x={clampPill(x(now), gl, gl + plotW) - 18} y={2} width={36} height={16} rx={8} />
              <text x={clampPill(x(now), gl, gl + plotW)} y={14} textAnchor="middle">
                Now
              </text>
            </g>
          )}
          {/* The reserve's label, last so no line crosses it. */}
          {reservePlace && (
            <g className="tl-reserve">
              <text x={reservePlace.x} y={reservePlace.y} textAnchor={reservePlace.anchor}>
                {`Reserve ${percent(reserve)}`}
              </text>
            </g>
          )}

          {/* ---------------- time axis */}
          <g className="tl-axis">
            {ticks.map((t) => {
              // A label that would run off the right edge is right-aligned to it instead of being clipped.
              const { x: ax, anchor } = tickPlacement(x(t.t), t, width);
              return (
                <text key={t.t} className="tl-tick" x={ax} y={axisTop + 12} textAnchor={anchor}>
                  <tspan x={ax}>{t.time}</tspan>
                  {t.day && (
                    <tspan x={ax} dy={14} className="tl-tick-day">
                      {t.day}
                    </tspan>
                  )}
                </text>
              );
            })}
          </g>
        </svg>
        <div
          ref={surface}
          className="tl-surface"
          style={{ left: gl, width: plotW, top: Math.max(0, top - 6), height: bottom - top + 12 }}
          role="slider"
          tabIndex={0}
          aria-label="Read the timeline slot by slot (left and right arrow keys)"
          aria-valuemin={0}
          aria-valuemax={Math.max(0, viewRows.length - 1)}
          aria-valuenow={
            active ??
            Math.max(
              0,
              viewRows.findIndex((r) => r.phase === "current"),
            )
          }
          aria-valuetext={
            activeRow
              ? readoutText(activeRow, timeZone, carInRange, showEarlier, true).replace(/\n/g, ". ")
              : "No slot selected"
          }
          onPointerMove={(e) => {
            if (e.pointerType === "mouse" || e.buttons) onPointer(e);
          }}
          onPointerDown={onPointer}
          onPointerLeave={(e) => {
            if (e.pointerType === "mouse") setActive(null);
          }}
          onKeyDown={onKey}
          onBlur={() => pointer === "key" && setActive(null)}
        />
        {activeRow && !strip && (
          <FloatingTip x={activeX} top={tipTop} width={width}>
            <TimelineTip
              row={activeRow}
              timeZone={timeZone}
              car={carInRange}
              earlier={showEarlier ? compareOptions.find((o) => o.value === activeCompare)!.label : ""}
              notes={bandFor(active!)}
              energy={lanes.includes("energy")}
            />
          </FloatingTip>
        )}
      </div>
    </ChartFigure>
  );
}

type Wash = { x0: number; x1: number; label: string; from: number; to: number };
/** One wash per stretch of the energy lane: where home and solar are both offline it says so once. */
function mergeWashes(home: Wash[], solar: Wash[]) {
  const all = [...home.map((w) => ({ ...w, series: "home" })), ...solar.map((w) => ({ ...w, series: "solar" }))].sort(
    (a, b) => a.x0 - b.x0,
  );
  const out: { x0: number; x1: number; series: string; text: string }[] = [];
  for (const w of all) {
    const last = out.at(-1);
    if (last && w.x0 < last.x1 - 0.5) {
      last.x1 = Math.max(last.x1, w.x1);
      if (!last.series.split(" ").includes(w.series)) last.series += ` ${w.series}`;
    } else out.push({ x0: w.x0, x1: w.x1, series: w.series, text: "" });
  }
  for (const w of out)
    w.text = w.series.includes(" ")
      ? "No readings"
      : w.series === "home"
        ? "Home meter offline"
        : "Solar meter offline";
  return out;
}

function lastPoint(points: (Point | null)[]) {
  for (let i = points.length - 1; i >= 0; i--) if (points[i]) return points[i];
  return null;
}
function lastX(points: (Point | null)[], x: { invert: (px: number) => number }) {
  const p = lastPoint(points);
  return p ? x.invert(p.x) : null;
}
const clampPill = (px: number, lo: number, hi: number) => Math.min(hi - 18, Math.max(lo + 18, px));

function ribbonText(action: Row["action"], tags: Row["tags"]) {
  const base = shortAction[action.key] ?? "";
  const target = action.target != null && base ? ` → ${Math.round(action.target)}%` : "";
  const tag = tags.length
    ? `${base ? " · " : ""}${tags.map((t) => (t === "free" ? "Free" : t === "saving" ? "Saving" : "IOG")).join(" · ")}`
    : "";
  return `${base}${target}${tag}`;
}

/** Measured steps (solid), forecast steps (dashed), bridges and the optional forecast-accuracy overlay for one series. */
function SeriesMarks({
  name,
  color,
  pick,
  steps,
  scale,
  baseline,
  bridges,
  accuracy,
  area = false,
  quietZero = false,
}: {
  name: string;
  color: string;
  pick: (r: Row) => SeriesPoint;
  steps: (
    pick: (r: Row) => number | null,
    scale: (v: number) => number,
    include: (r: Row) => boolean,
  ) => (Step | null)[];
  scale: (v: number) => number;
  baseline: number;
  bridges: string[];
  accuracy: boolean;
  area?: boolean;
  /** Draw nothing for zero (the car between sessions): the lane's baseline already says "nothing". */
  quietZero?: boolean;
}) {
  const quiet = (v: number | null) => (quietZero && v != null && v <= 0.005 ? null : v);
  const measured = steps(
    (r) => quiet(pick(r).actual),
    scale,
    (r) => r.phase === "past",
  );
  const forecast = steps(
    (r) => quiet(pick(r).forecast),
    scale,
    (r) => r.phase !== "past",
  );
  const past = steps(
    (r) => pick(r).forecast,
    scale,
    (r) => r.phase === "past",
  );
  const hasMeasured = measured.some(Boolean),
    hasForecast = forecast.some(Boolean);
  return (
    <g data-series-group={name}>
      {area && hasMeasured && (
        <path
          className="tl-area"
          d={stepArea(measured, baseline)}
          style={{ fill: color, fillOpacity: encodings.area }}
        />
      )}
      {area && hasForecast && (
        <path
          className="tl-area"
          d={stepArea(forecast, baseline)}
          style={{ fill: color, fillOpacity: encodings.area / 2 }}
        />
      )}
      {accuracy && (
        <path
          className="tl-accuracy"
          data-series={`${name}-accuracy`}
          d={stepPath(past)}
          style={{
            stroke: color,
            strokeDasharray: encodings.forecast.strokeDasharray,
            strokeWidth: 1,
            strokeOpacity: 0.75,
          }}
        />
      )}
      {bridges.map((d, i) => (
        <path
          key={i}
          className="tl-bridge"
          d={d}
          style={{ stroke: color, strokeOpacity: encodings.bridge.strokeOpacity, strokeWidth: 2 }}
        />
      ))}
      {hasMeasured && (
        <path
          className="tl-series tl-measured"
          data-series={name}
          d={stepPath(measured)}
          style={{ stroke: color, strokeWidth: 2 }}
        />
      )}
      {hasForecast && (
        <path
          className="tl-series tl-forecast"
          data-series={name}
          d={stepPath(forecast)}
          style={{ stroke: color, strokeWidth: 2, strokeDasharray: encodings.forecast.strokeDasharray }}
        />
      )}
    </g>
  );
}

/**
 * Two short lines for the readout strip and the keyboard: "13:30–14:00 · Export solar", then
 * "Home 0.37 kWh · Battery 42% · Import 25.4p" (measured in the past, Predbat's forecast ahead). Solar is left out
 * when there was none and none was expected, so the second line stays one line on a phone.
 */
function readoutText(row: Row, timeZone: string | undefined, car: boolean, earlier: boolean, withDay = false) {
  const head = `${slotLabel(row.start, row.end, timeZone, withDay)} · ${row.action.label}${row.action.target != null ? ` → ${Math.round(row.action.target)}%` : ""}`;
  const past = row.phase === "past";
  const value = (p: SeriesPoint) => {
    if (past)
      return p.actualRaw != null
        ? `${p.approx ? "≈" : ""}${kwhTable(p.actualRaw)} kWh`
        : p.state === "awaiting"
          ? "waiting"
          : "no reading";
    return p.forecastRaw != null ? `${kwhTable(p.forecastRaw)} kWh` : "—";
  };
  const bits: string[] = [];
  const noSolar =
    (row.solar.actualRaw ?? 0) < 0.005 &&
    (row.solar.forecastRaw ?? 0) < 0.005 &&
    (!past || row.solar.actualRaw != null || row.solar.state === "zero");
  if (!noSolar) bits.push(`Solar ${value(row.solar)}`);
  bits.push(`Home ${value(row.home)}`);
  if (car) bits.push(`Car ${value(row.ev)}`);
  if (past && row.grid.importRaw != null) bits.push(`Grid in ${kwhTable(row.grid.importRaw)} kWh`);
  if (past && (row.grid.exportRaw ?? 0) > 0.005) bits.push(`out ${kwhTable(row.grid.exportRaw)} kWh`);
  if (earlier && row.earlierRaw != null) bits.push(`Earlier ${kwhTable(row.earlierRaw)} kWh`);
  const soc = past ? (row.soc.end ?? row.soc.start) : row.soc.forecastStart;
  if (soc != null) bits.push(`Battery ${percent(soc)}`);
  if (row.price.import != null)
    bits.push(`Import ${row.price.estimated ? "≈" : ""}${pence(row.price.import, { unit: "p" })}`);
  return `${head}\n${bits.join(" · ")}`;
}

/** The tooltip: a mini-table (Solar / Home / Car against Forecast / Actual / Earlier), then battery, price and cost. */
function TimelineTip({
  row,
  timeZone,
  car,
  earlier,
  notes,
  energy = true,
}: {
  row: Row;
  timeZone?: string;
  car: boolean;
  earlier: string;
  notes: string[];
  /** Show the energy mini-table (not on the battery outlook). */
  energy?: boolean;
}) {
  const actual = (p: SeriesPoint, metric: string) => {
    if (row.phase !== "past") return row.phase === "current" ? "in progress" : "—";
    if (p.actualRaw != null) return `${p.approx ? "≈" : ""}${kwhTable(p.actualRaw)}`;
    return p.state === "awaiting"
      ? "waiting"
      : p.state === "missing"
        ? "no reading"
        : stateNote(p.state, metric)
          ? "—"
          : "—";
  };
  const lines: { key: string; name: string; color: string; p: SeriesPoint }[] = [
    { key: "solar", name: "Solar", color: series.solar.color, p: row.solar },
    { key: "home", name: car ? "Home (excl. car)" : "Home", color: series.home.color, p: row.home },
  ];
  if (car) lines.push({ key: "ev", name: "Car", color: series.ev.color, p: row.ev });
  const asleep = row.phase === "past" && row.solar.state === "zero" ? stateNote("zero", "solar") : null;
  const soc =
    row.phase === "past"
      ? row.soc.start != null || row.soc.end != null
        ? `${row.soc.start != null ? percent(row.soc.start) : "?"} → ${row.soc.end != null ? percent(row.soc.end) : "?"}`
        : null
      : row.soc.forecastStart != null
        ? `${percent(row.soc.forecastStart)} → ${row.soc.forecastEnd != null ? percent(row.soc.forecastEnd) : "?"} planned`
        : null;
  const approx = lines.some((l) => l.p.approx);
  return (
    <div className="tl-tip">
      <p className="chart-tip-label">{slotLabel(row.start, row.end, timeZone)}</p>
      <p className="tl-tip-action">
        <i
          className={row.action.tone === "neutral" ? "neutral" : undefined}
          style={row.action.tone === "neutral" ? undefined : { background: toneColor[row.action.tone] }}
          aria-hidden="true"
        />
        <span>
          {row.action.label}
          {row.action.target != null && ` → ${Math.round(row.action.target)}%`}
          {row.tags.map((t) => (
            <em key={t} className="tl-tag">
              {tagLabels[t]}
            </em>
          ))}
        </span>
      </p>
      {row.action.reason && <p className="chart-tip-note tl-tip-reason">{row.action.reason}</p>}
      {energy && (
        <table className="tl-tip-table">
          <thead>
            <tr>
              <th />
              <th>Forecast</th>
              <th>Actual</th>
              {earlier && <th>{earlier}</th>}
            </tr>
          </thead>
          <tbody>
            {lines.map((l) => (
              <tr key={l.key}>
                <th scope="row">
                  <i className="chart-key" style={{ color: l.color }} aria-hidden="true" />
                  {l.name}
                </th>
                <td>{kwhTable(l.p.forecastRaw)}</td>
                <td className={l.p.actualRaw == null ? "chart-tip-missing" : undefined}>{actual(l.p, l.key)}</td>
                {earlier && <td>{l.key === "home" && row.earlierRaw != null ? kwhTable(row.earlierRaw) : "—"}</td>}
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {energy && <p className="tl-tip-unit">kWh in this slot</p>}
      {row.phase === "past" && (row.grid.importRaw != null || row.grid.exportRaw != null) && (
        <TipRow
          color={series.grid.color}
          name="Grid"
          value={`bought ${kwhTable(row.grid.importRaw)} · sold ${kwhTable(row.grid.exportRaw)} kWh`}
        />
      )}
      {soc && <TipRow color={series.battery.color} name="Battery" value={soc} />}
      {row.price.import != null && (
        <TipRow
          color="var(--price-high, #9fb0bf)"
          kind="price"
          name="Price"
          value={`import ${row.price.estimated ? "≈ " : ""}${pence(row.price.import, { unit: "p" })} · export ${pence(row.price.export, { unit: "p" })}`}
        />
      )}
      {row.cost != null && row.phase !== "past" && (
        <TipRow color="transparent" name="Predbat's slot cost" value={gbp(row.cost)} />
      )}
      {[
        asleep,
        ...notes,
        row.price.note ? `≈ price: ${row.price.note}` : null,
        approx ? "≈ timing shared out between half-hours" : null,
      ]
        .filter(Boolean)
        .map((n) => (
          <p key={n} className="chart-tip-note">
            {n}
          </p>
        ))}
    </div>
  );
}

/** The table view of the timeline: every slot, the same figures as the tooltip. */
function TimelineTable({
  rows,
  timeZone,
  car,
  grid = false,
  energy,
  compact = false,
}: {
  rows: Row[];
  timeZone?: string;
  car: boolean;
  /** A column for the grid: bought / sold, measured only. */
  grid?: boolean;
  energy: boolean;
  /** Phone: short headings, and the plan column wraps so the figures are what scrolls. */
  compact?: boolean;
}) {
  const cell = (r: Row, p: SeriesPoint) =>
    `${kwhTable(p.forecastRaw)} / ${r.phase === "past" ? (p.actualRaw != null ? `${p.approx ? "≈" : ""}${kwhTable(p.actualRaw)}` : p.state === "missing" ? "no reading" : "—") : "—"}`;
  const columns = 5 + (energy ? 2 : 0) + (car ? 1 : 0) + (grid ? 1 : 0);
  // Rows sit under a heading for their day, so each row needs only its times.
  const days: { day: string; rows: Row[] }[] = [];
  for (const r of rows) {
    const day = wallOf(r.start, timeZone).day;
    if (days.at(-1)?.day === day) days.at(-1)!.rows.push(r);
    else days.push({ day, rows: [r] });
  }
  const homeName = car ? (compact ? "Home*" : "Home excl. car") : "Home";
  return (
    <ChartTable label="Timeline, slot by slot" className={compact ? "tl-table-compact" : undefined}>
      {energy && (
        <caption className="tl-table-caption">
          {compact
            ? `Energy in kWh, forecast / actual${car ? ". *Home without the car." : "."}`
            : "Energy in kWh: forecast / actual."}
        </caption>
      )}
      <thead>
        <tr>
          <th scope="col">Time</th>
          <th scope="col">Plan</th>
          {energy && (
            <th scope="col" className="num">
              Solar
            </th>
          )}
          {energy && (
            <th scope="col" className="num">
              {homeName}
            </th>
          )}
          {car && (
            <th scope="col" className="num">
              Car
            </th>
          )}
          {grid && (
            <th scope="col" className="num">
              {compact ? "Grid" : "Grid in / out"}
            </th>
          )}
          <th scope="col" className="num">
            Battery
          </th>
          <th scope="col" className="num">
            Import
          </th>
          <th scope="col" className="num">
            Export
          </th>
        </tr>
      </thead>
      {days.map((d) => (
        <tbody key={d.day}>
          <tr className="tl-day-row">
            <th scope="colgroup" colSpan={columns}>
              {d.day}
            </th>
          </tr>
          {d.rows.map((r) => (
            <tr key={r.start}>
              <td>{slotLabel(r.start, r.end, timeZone, false)}</td>
              <td className="tl-plan-cell">
                {r.action.label}
                {r.action.target != null && ` → ${Math.round(r.action.target)}%`}
                {r.tags.length > 0 && ` (${r.tags.map((t) => tagLabels[t]).join(", ")})`}
              </td>
              {energy && <td className="num">{cell(r, r.solar)}</td>}
              {energy && <td className="num">{cell(r, r.home)}</td>}
              {car && <td className="num">{cell(r, r.ev)}</td>}
              {grid && (
                <td className="num">
                  {r.phase === "past" && (r.grid.importRaw != null || r.grid.exportRaw != null)
                    ? `${kwhTable(r.grid.importRaw)} / ${kwhTable(r.grid.exportRaw)}`
                    : "—"}
                </td>
              )}
              <td className="num">
                {r.phase === "past" ? percent(r.soc.end ?? r.soc.start) : percent(r.soc.forecastStart)}
              </td>
              <td className="num">
                {r.price.estimated && r.price.import != null ? "≈" : ""}
                {pence(r.price.import, { unit: "p" })}
              </td>
              <td className="num">{pence(r.price.export, { unit: "p" })}</td>
            </tr>
          ))}
        </tbody>
      ))}
    </ChartTable>
  );
}

/** Measured home use for the same local clock times a day or a week earlier, fetched in half-hour slots. */
function useEarlier(
  api: Api | undefined,
  compare: Compare,
  elapsed: Row[],
  timeZone: string | undefined,
  car: boolean,
) {
  const days = compareOptions.find((o) => o.value === compare)!.days;
  const first = elapsed[0]?.start,
    last = elapsed.at(-1)?.end;
  // The whole of the slot in progress is requested: the earlier day is fully in the past.
  const from = first != null && days ? shiftLocalDays(first, -days, timeZone ?? "UTC") : NaN;
  const to = last != null && days ? shiftLocalDays(last, -days, timeZone ?? "UTC") : NaN;
  const key = api && days && to > from ? `${compare}|${from}|${to}|${car}` : "";
  const [state, setState] = useState<{ key: string; map?: Map<number, number | null>; error?: string }>({ key: "" });
  useEffect(() => {
    if (!key || !api) return;
    let active = true;
    const query = new URLSearchParams({
      from: new Date(from).toISOString(),
      to: new Date(to).toISOString(),
      slotMinutes: "30",
    });
    api<{ slots: HistorySlot[] }>(`/telemetry/history?${query}`)
      .then((history) => {
        if (active) setState({ key, map: historyByStart(history.slots, car) });
      })
      .catch((e: Error) => {
        if (active) setState({ key, error: e.message });
      });
    return () => {
      active = false;
    };
    // The key captures compare, the window and the car flag.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key, api]);
  return {
    loading: !!key && state.key !== key,
    map: state.key === key ? state.map : undefined,
    error: state.key === key ? state.error : undefined,
  };
}

export type { SlotState };
