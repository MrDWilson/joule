/**
 * Chart chrome shared by every chart: the measured container, the <figure> with its caption and table view, legend chips
 * that hide and show series (remembered per chart), and the tooltip's row and mini-table layouts.
 */
import { useCallback, useEffect, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { Disclosure } from "../ui/Disclosure";
import { useScrollRow } from "../../lib/scrollRow";

/** Width of an element, kept current with a ResizeObserver. Starts from `fallback` so the first paint is never zero-sized. */
export function useWidth<T extends HTMLElement>(fallback = 640) {
  const ref = useRef<T>(null);
  const [width, setWidth] = useState(fallback);
  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    const read = () => {
      const w = el.getBoundingClientRect().width;
      if (w > 0) setWidth(Math.round(w));
    };
    read();
    if (typeof ResizeObserver === "undefined") return;
    const observer = new ResizeObserver(read);
    observer.observe(el);
    return () => observer.disconnect();
  }, []);
  return [ref, width] as const;
}

/** State saved in localStorage under `key` (private mode and bad values fall back to `initial`). */
export function useStored<T>(key: string, initial: T, valid: (v: unknown) => v is T) {
  const [value, setValue] = useState<T>(() => {
    try {
      const raw = localStorage.getItem(key);
      if (raw == null) return initial;
      const parsed: unknown = JSON.parse(raw);
      return valid(parsed) ? parsed : initial;
    } catch {
      return initial;
    }
  });
  const set = useCallback(
    (next: T) => {
      setValue(next);
      try {
        localStorage.setItem(key, JSON.stringify(next));
      } catch {
        /* private mode */
      }
    },
    [key],
  );
  return [value, set] as const;
}

export interface Chip {
  key: string;
  label: string;
  color: string;
  /** How the swatch is drawn: a line for lines, a block for bars and areas. */
  shape?: "line" | "block" | "strip";
}

/** Legend as toggle chips: every series stays named, and pressing a chip hides or shows it. */
export function LegendChips({
  chips,
  hidden,
  onToggle,
  label = "Series shown",
  scroll = false,
}: {
  chips: Chip[];
  hidden: string[];
  onToggle: (key: string) => void;
  label?: string;
  /** One row that scrolls sideways (phones), with a fade where more chips are, instead of wrapping. */
  scroll?: boolean;
}) {
  const ref = useRef<HTMLUListElement>(null);
  useScrollRow(ref, [chips.length, scroll]);
  if (chips.length < 2) return null;
  return (
    <ul ref={ref} className={`chart-chips${scroll ? " scroll-row" : ""}`} role="list" aria-label={label}>
      {chips.map((c) => {
        const on = !hidden.includes(c.key);
        return (
          <li key={c.key}>
            <button
              type="button"
              className="chart-chip"
              aria-pressed={on}
              data-chip={c.key}
              onClick={() => onToggle(c.key)}
              title={on ? `Hide ${c.label.toLowerCase()}` : `Show ${c.label.toLowerCase()}`}
            >
              <i className={`chart-chip-key ${c.shape ?? "line"}`} style={{ color: c.color }} aria-hidden="true" />
              {c.label}
            </button>
          </li>
        );
      })}
    </ul>
  );
}

/** Hidden-series state for a chart's chips, remembered per chart id. */
export function useHiddenSeries(id: string, defaults: string[] = []) {
  const [hidden, setHidden] = useStored<string[]>(
    `joule.chart.${id}.hidden`,
    defaults,
    (v): v is string[] => Array.isArray(v) && v.every((x) => typeof x === "string"),
  );
  const toggle = (key: string) => setHidden(hidden.includes(key) ? hidden.filter((k) => k !== key) : [...hidden, key]);
  return [hidden, toggle] as const;
}

/**
 * Every chart sits in a <figure> named by its heading and described by a generated one-sentence summary, which is also the
 * visible caption. The data is always reachable without hovering through the "Show as table" disclosure.
 */
export function ChartFigure({
  id,
  title,
  labelledBy,
  label,
  summary,
  toolbar,
  table,
  footer,
  className,
  children,
  data,
}: {
  id: string;
  /** Shown as a small heading when the chart has no panel heading of its own. */
  title?: string;
  /** Id of an existing heading that names the chart. */
  labelledBy?: string;
  /** The chart's accessible name when there is no heading id to point at. */
  label?: string;
  summary: string;
  toolbar?: ReactNode;
  table?: ReactNode;
  footer?: ReactNode;
  className?: string;
  children: ReactNode;
  data?: Record<string, string | number | undefined>;
}) {
  const titleId = `${id}-title`,
    summaryId = `${id}-summary`;
  const attributes = Object.fromEntries(Object.entries(data ?? {}).map(([k, v]) => [`data-${k}`, v]));
  return (
    <figure
      className={`chart-figure${className ? ` ${className}` : ""}`}
      aria-labelledby={labelledBy ?? (title ? titleId : undefined)}
      aria-label={!labelledBy && !title ? (label ?? "Chart") : undefined}
      aria-describedby={summaryId}
      {...attributes}
    >
      {title && (
        <p className="chart-figure-title" id={titleId}>
          {title}
        </p>
      )}
      {toolbar}
      {children}
      <figcaption className="chart-caption" id={summaryId}>
        {summary}
      </figcaption>
      {(footer || table) && (
        <div className="chart-footer">
          {footer}
          {table && (
            <Disclosure summary="Show as table" className="chart-table-disclosure">
              {table}
            </Disclosure>
          )}
        </div>
      )}
    </figure>
  );
}

/** A scrollable table region for a chart's table view. */
export function ChartTable({ label, children, className }: { label: string; children: ReactNode; className?: string }) {
  // .table-scroll draws its own edge shadows where more columns are, and keeps the first column in place.
  return (
    <div
      className={`table-wrap table-scroll chart-table${className ? ` ${className}` : ""}`}
      tabIndex={0}
      role="region"
      aria-label={`${label} (scrolls sideways)`}
    >
      <table>{children}</table>
    </div>
  );
}

/** One tooltip line: a short stroke key in the series colour, the name, and the value on the right. */
export function TipRow({
  color,
  name,
  value,
  kind = "",
  muted = false,
}: {
  color: string;
  name: string;
  value: string;
  kind?: "" | "dashed" | "earlier" | "block" | "price";
  muted?: boolean;
}) {
  return (
    <p className="chart-tip-row">
      <i className={`chart-key${kind ? ` ${kind}` : ""}`} style={{ color }} aria-hidden="true" />
      <span>{name}</span>
      <strong className={muted ? "chart-tip-missing" : undefined}>{value}</strong>
    </p>
  );
}

/** Floating tooltip positioned beside an x position inside a relative container, flipped to stay inside it. */
export function FloatingTip({
  x,
  top,
  width,
  children,
}: {
  x: number;
  top: number;
  width: number;
  children: ReactNode;
}) {
  const ref = useRef<HTMLDivElement>(null);
  const [tipWidth, setTipWidth] = useState(280);
  useEffect(() => {
    const w = ref.current?.offsetWidth;
    if (w && w !== tipWidth) setTipWidth(w);
  }, [children, tipWidth]);
  const right = x + 14 + tipWidth <= width;
  const left = right ? x + 14 : Math.max(0, x - 14 - tipWidth);
  return (
    <div ref={ref} className="chart-tip chart-tip-floating" style={{ left, top }} role="presentation">
      {children}
    </div>
  );
}
