import { useId, type CSSProperties, type ReactNode } from "react";

/**
 * Joule brand mark: a brilliant-cut jewel split by a lightning bolt.
 * The bolt's jog sits on the gem's girdle and its tip is the gem's point,
 * so energy (joule) and jewel are one shape. Geometry matches /favicon.svg
 * (64-unit grid); see docs/brand/README.md.
 */

/** "tile": dark rounded tile, as the favicon. "bare": gem only, bolt is see-through. "mono": single colour (currentColor). */
export type BrandMarkVariant = "tile" | "bare" | "mono";

const BOLT = "M35.7 4L21 28H34.5L32 60L43.5 22H31L43.3 4Z";

// [path, colour] in paint order: pavilion (lower) facets first, crown (upper) facets on top.
const FACETS: ReadonlyArray<readonly [string, string]> = [
  ["M6 25H38L32 58Z", "#55dfb6"],
  ["M6 25H17L32 58Z", "#46d3aa"],
  ["M38 25H58L32 58Z", "#1f9f84"],
  ["M47 25H58L32 58Z", "#188873"],
  ["M6 25L19 12H34.4L26 25Z", "#b2fae1"],
  ["M6 25L19 12L17 25Z", "#9df5d6"],
  ["M34.4 12H45L58 25H26Z", "#7cefc9"],
  ["M45 12L58 25H47Z", "#6be6be"],
];

// The two shards left after the bolt is cut out (same as /mask-icon.svg).
const SHARDS = ["M19 12H30.81L21 28H34.5L32.17 57.78L32 58L6 25Z", "M37.86 12H45L58 25L33.29 56.75L43.5 22H31Z"];

export const BRAND_TILE = "#0a1219";

export function BrandMark({
  size = 32,
  className,
  variant = "tile",
  title = "Joule",
  decorative = false,
  style,
}: {
  size?: number | string;
  className?: string;
  variant?: BrandMarkVariant;
  /** Accessible name. Ignored when `decorative`. */
  title?: string;
  /** Hide from assistive tech, e.g. when the wordmark is next to it. */
  decorative?: boolean;
  style?: CSSProperties;
}) {
  const maskId = `joule-bolt-${useId().replace(/[^a-zA-Z0-9_-]/g, "")}`;
  const a11y = decorative
    ? { "aria-hidden": true as const, focusable: "false" as const }
    : { role: "img", "aria-label": title };

  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 64 64"
      width={size}
      height={size}
      className={className}
      style={{ display: "block", flexShrink: 0, ...style }}
      {...a11y}
    >
      {variant === "mono" ? (
        <g fill="currentColor">
          {SHARDS.map((d) => (
            <path key={d} d={d} />
          ))}
        </g>
      ) : (
        <>
          <defs>
            <mask id={maskId} maskUnits="userSpaceOnUse" x="0" y="0" width="64" height="64">
              <rect width="64" height="64" fill="#fff" />
              <path d={BOLT} fill="#000" />
            </mask>
          </defs>
          {variant === "tile" && <rect width="64" height="64" rx="14" fill={BRAND_TILE} />}
          <g mask={`url(#${maskId})`} strokeWidth="1.5" strokeLinejoin="round">
            {FACETS.map(([d, colour]) => (
              <path key={d} d={d} fill={colour} stroke={colour} />
            ))}
          </g>
        </>
      )}
    </svg>
  );
}

/** Mark plus the "Joule" wordmark, with an optional tagline underneath. Text inherits `color`. */
export function BrandLockup({
  size = 32,
  className,
  variant = "bare",
  tagline,
  style,
}: {
  /** Mark size in px; the wordmark scales with it. */
  size?: number;
  className?: string;
  variant?: BrandMarkVariant;
  tagline?: ReactNode;
  style?: CSSProperties;
}) {
  return (
    <div
      className={className}
      style={{ display: "inline-flex", alignItems: "center", gap: Math.round(size * 0.3), ...style }}
    >
      <BrandMark size={size} variant={variant} decorative />
      <div style={{ display: "flex", flexDirection: "column", minWidth: 0, lineHeight: 1.05 }}>
        <span
          style={{
            color: "inherit",
            fontSize: Math.round(size * 0.66),
            fontWeight: 650,
            letterSpacing: "-0.03em",
          }}
        >
          Joule
        </span>
        {tagline != null && (
          <span
            style={{
              color: "inherit",
              opacity: 0.62,
              fontSize: Math.max(11, Math.round(size * 0.3)),
              fontWeight: 500,
              marginTop: 3,
              letterSpacing: 0,
            }}
          >
            {tagline}
          </span>
        )}
      </div>
    </div>
  );
}
