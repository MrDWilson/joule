import type { ReactNode } from "react";
import { cn } from "@/lib/utils";

/** The colour a stat or card belongs to: a data series, or the brand accent. */
export type Accent = "accent" | "load" | "solar" | "battery" | "ev" | "export" | "grid" | "neutral";

/**
 * A headline figure: label, value with unit, an optional delta line, a status chip and a slot for a sparkline.
 * Used for the Today tiles and the Energy summary. The value uses tabular figures so tiles line up.
 */
export function Stat({
  label,
  value,
  unit,
  icon,
  delta,
  status,
  sparkline,
  footnote,
  accent = "accent",
  size = "md",
  className,
  ariaLabel,
}: {
  label: ReactNode;
  value: ReactNode;
  unit?: ReactNode;
  icon?: ReactNode;
  /** One line comparing with an earlier period, e.g. "Yesterday by now: 42.0 kWh". */
  delta?: ReactNode;
  /** A Chip for coverage or freshness ("Sensor offline", "≈ estimated"); omit when everything is fine. */
  status?: ReactNode;
  sparkline?: ReactNode;
  footnote?: ReactNode;
  accent?: Accent;
  size?: "sm" | "md" | "lg";
  className?: string;
  /** Names the tile for assistive tech when the label is not plain text. */
  ariaLabel?: string;
}) {
  return (
    <article className={cn("stat", `stat-${size}`, `accent-${accent}`, className)} aria-label={ariaLabel}>
      <div className="stat-head">
        <span className="stat-label">{label}</span>
        {icon && (
          <span className="stat-icon" aria-hidden="true">
            {icon}
          </span>
        )}
      </div>
      <div className="stat-value">
        <span>{value}</span>
        {unit && <small>{unit}</small>}
      </div>
      {sparkline && <div className="stat-spark">{sparkline}</div>}
      {(delta || status) && (
        <div className="stat-foot">
          {delta && <span className="stat-delta">{delta}</span>}
          {status}
        </div>
      )}
      {footnote && <p className="stat-footnote">{footnote}</p>}
    </article>
  );
}

/**
 * A content card. Titles clamp to two lines and summaries to about 240 characters (preview()), so a long AI title
 * can't stretch a grid row. Cards in a grid align to the top.
 */
export function Card({
  title,
  eyebrow,
  actions,
  children,
  footer,
  className,
  level = 3,
  as: As = "article",
}: {
  title?: ReactNode;
  eyebrow?: ReactNode;
  actions?: ReactNode;
  children?: ReactNode;
  footer?: ReactNode;
  className?: string;
  level?: 2 | 3 | 4;
  as?: "article" | "section" | "div";
}) {
  const H = `h${level}` as const;
  return (
    <As className={cn("card", className)}>
      {(title || eyebrow || actions) && (
        <header className="card-head">
          <div className="card-titles">
            {eyebrow && <div className="card-eyebrow">{eyebrow}</div>}
            {title && <H className="card-title">{title}</H>}
          </div>
          {actions && <div className="card-actions-top">{actions}</div>}
        </header>
      )}
      {children}
      {footer && <footer className="card-foot">{footer}</footer>}
    </As>
  );
}

/** Shortens text for a preview at a word boundary: preview(summary) is at most ~240 characters and ends with "…". */
export function preview(text: string | null | undefined, max = 240) {
  const t = (text ?? "").trim();
  if (t.length <= max) return t;
  const cut = t.slice(0, max);
  const space = cut.lastIndexOf(" ");
  return `${(space > max * 0.6 ? cut.slice(0, space) : cut).replace(/[\s,;:.–—-]+$/, "")}…`;
}
