/**
 * The chart design system: one colour per thing measured, and one meaning per line style, used by every chart.
 *
 * Colours come from the CSS tokens (tokens.css, charts.css), so tiles, legends and plots always agree. They were checked with
 * the dataviz palette validator against the dark panel surface (#0d151d): home, solar, battery and car pass every check as
 * neighbours; grid (magenta) passes next to battery, solar, home and car in the order the daily bars stack them. Home and car
 * are too close to sit side by side (normal-vision ΔE 9.8), so the car never touches the home line: on the timeline it has
 * its own lane, and in the daily bars the battery sits between them. Mint is the brand colour and is never a series.
 *
 * Line styles:
 *   forecast  dashed "5 4"                    (only ever means "forecast")
 *   measured  solid 2 px
 *   earlier   solid 1 px at 35 % opacity, neutral ink (yesterday / last week)
 *   bridge    the series' own hue, solid, 30 % opacity, across a short gap in readings
 *   now       solid 1 px line with a "Now" pill
 */
export type SeriesKey = "home" | "solar" | "battery" | "ev" | "grid";

export const series: Record<SeriesKey, { label: string; color: string }> = {
  home: { label: "Home", color: "var(--load, #3987e5)" },
  solar: { label: "Solar", color: "var(--solar, #c98500)" },
  battery: { label: "Battery", color: "var(--battery, #199e70)" },
  ev: { label: "Car", color: "var(--ev, #9085e9)" },
  grid: { label: "Grid", color: "var(--series-grid, #d55181)" },
};

/** Neutral ink for series that are not one of the things measured: "actual" on forecast-comparison charts, net cost bars. */
export const ink = "var(--series-ink, #dce5ed)";
/** The earlier-period line (yesterday, last week). */
export const earlierInk = "var(--series-earlier, #b4c3cf)";

export const encodings = {
  forecast: { strokeDasharray: "5 4", strokeWidth: 2 },
  measured: { strokeWidth: 2 },
  earlier: { strokeOpacity: 0.35, strokeWidth: 1 },
  bridge: { strokeOpacity: 0.3, strokeWidth: 2 },
  now: { strokeWidth: 1 },
  /** Area under a measured line. */
  area: 0.1,
  /** Wash over a long gap in readings, drawn only in the lane of the series affected. */
  gapWash: 0.06,
  /** Bars for a period that was only partly measured. */
  partial: 0.4,
} as const;

/** Glossary tone of a plan action → ribbon colour (the same tokens as the action badges). */
export const toneColor: Record<string, string> = {
  blue: "var(--info, #7fb0f0)",
  green: "var(--success, #4fd3a2)",
  amber: "var(--warn, #e8b45c)",
  violet: "var(--violet, #b3aaf3)",
  neutral: "var(--bg-3, #182431)",
};
