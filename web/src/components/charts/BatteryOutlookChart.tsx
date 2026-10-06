import { EnergyTimeline } from "./EnergyTimeline";
import { mergeTimeline, type TimelineSlot } from "./timeline";

/**
 * Battery outlook: the battery lane of the energy timeline on its own, measured level solid (at each reading's own time),
 * Predbat's planned level dashed, the reserve line and a "Now" marker, on a fixed 0–100 % scale.
 */
export function BatteryOutlookChart({
  chart,
  plan,
  timeZone,
  reserve,
  currentSoc,
}: {
  /** History followed by the plan (or pass history as `chart` and the plan as `plan` to merge them here). */
  chart: TimelineSlot[];
  plan?: TimelineSlot[];
  timeZone?: string;
  reserve?: number | null;
  currentSoc?: { value: number | null; time: string } | null;
}) {
  const slots = plan ? mergeTimeline(chart, plan) : chart;
  return (
    <EnergyTimeline
      slots={slots}
      timeZone={timeZone}
      preset="battery"
      reserve={reserve}
      currentSoc={currentSoc}
      label="Battery level, measured and planned"
    />
  );
}
