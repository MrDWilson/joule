import { coverage } from "./charts/chartUtils";
import { number } from "../lib/format";

/**
 * Change against the equivalent earlier period, e.g. "▲ 1.2 kWh (+12%) vs this time yesterday". Shown only when both periods are
 * measured to a similar extent; otherwise it says which side's readings are incomplete instead of quoting a misleading change.
 */
export function Delta({
  current,
  previous,
  currentCoverage,
  previousCoverage,
  label,
  noun = label,
  currentNoun = "this period",
  format,
  better,
  compact = false,
}: {
  current: number | null | undefined;
  previous: number | null | undefined;
  currentCoverage: number | undefined;
  previousCoverage: number | undefined;
  label: string;
  noun?: string;
  currentNoun?: string;
  format: (n: number) => string;
  better?: "higher" | "lower";
  compact?: boolean;
}) {
  if (current == null) return null;
  if (previous == null) return <small className="evidence-delta delta-quiet">No readings {label}</small>;
  const earlier = `${label[0].toUpperCase()}${label.slice(1)}: ${format(previous)} (${coverage(previousCoverage).toLowerCase()})`;
  if (Math.abs((currentCoverage ?? 0) - (previousCoverage ?? 0)) > 0.05) {
    const which = (previousCoverage ?? 0) < (currentCoverage ?? 0) ? noun : currentNoun;
    const text = compact
      ? `${which[0].toUpperCase()}${which.slice(1)} incomplete, not compared`
      : `Not compared: ${which.endsWith("s") ? `${which}’` : `${which}’s`} readings are incomplete`;
    return (
      <small className="evidence-delta delta-quiet" title={earlier}>
        {text}
      </small>
    );
  }
  const change = current - previous,
    scale = Math.max(Math.abs(current), Math.abs(previous));
  if (scale < 1e-9 || Math.abs(change) < scale * 0.005)
    return (
      <small className="evidence-delta" title={earlier}>
        Same as {label}
      </small>
    );
  const up = change > 0;
  const percent =
    Math.abs(previous) > 1e-9 && previous > 0 && current >= 0
      ? ` (${up ? "+" : "−"}${number(Math.abs((change / previous) * 100), 0)}%)`
      : "";
  const tone = !better ? "neutral" : up === (better === "higher") ? "good" : "bad";
  return (
    <small className="evidence-delta" title={earlier}>
      <span className={`delta-arrow ${tone}`} aria-hidden="true">
        {up ? "▲" : "▼"}
      </span>
      <span className="sr-only">{up ? "Up" : "Down"} </span>
      {format(Math.abs(change))}
      {percent} vs {label}
    </small>
  );
}
