import type { ReactNode } from "react";
import { cn } from "@/lib/utils";

/** The meaning of a chip; drives its colour. Older names (green, amber, blue) map onto these. */
export type Tone = "neutral" | "info" | "success" | "warn" | "danger" | "violet";
type LegacyTone = "green" | "amber" | "blue" | "red" | "muted";
const tones: Record<Tone | LegacyTone, Tone> = {
  neutral: "neutral",
  muted: "neutral",
  info: "info",
  blue: "info",
  success: "success",
  green: "success",
  warn: "warn",
  amber: "warn",
  danger: "danger",
  red: "danger",
  violet: "violet",
};
export const toTone = (tone: string | undefined): Tone => tones[(tone ?? "neutral") as Tone] ?? "neutral";

/**
 * A short status label in sentence case at 12px: "Ready for review", "Read-only", "Sensor offline".
 * dot adds a leading dot in the tone colour (for live states); icon puts a small icon first.
 */
export function Chip({
  children,
  tone = "neutral",
  dot = false,
  icon,
  className,
  title,
}: {
  children: ReactNode;
  tone?: Tone | LegacyTone | string;
  dot?: boolean;
  icon?: ReactNode;
  className?: string;
  title?: string;
}) {
  const t = toTone(tone);
  return (
    // "badge" and the legacy tone class keep older stylesheets that target .badge.green working.
    <span className={cn("chip badge", `chip-${t}`, typeof tone === "string" && tone, className)} title={title}>
      {dot && <i className="chip-dot" aria-hidden="true" />}
      {icon}
      {children}
    </span>
  );
}
/** The same as Chip; kept for the components that call it a badge. */
export const Badge = Chip;
