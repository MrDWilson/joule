/**
 * How the charts treat slots without a measurement. A missing reading is never drawn as zero and never given a number, but
 * a meter that is asleep for a known reason is not a fault either:
 *
 *   value     measured (≈ when the counter proved the energy but its timing was shared out)
 *   zero      no reading, but zero is known: the sensor reported idle, or solar overnight where Predbat forecast nothing.
 *             Drawn at 0; the tooltip says "Solar sensor asleep overnight; counted as 0".
 *   missing   the slot is over and nothing was measured: a short run (≤ 2 slots and ≤ 1 hour) between drawn slots is bridged
 *             in the series' own hue at 30 % opacity; a longer run is a 6 % wash in that series' lane only, named in the
 *             tooltip ("Solar meter offline 18:30–19:30").
 *   awaiting  ended within the last 15 minutes and may simply not be collected yet; drawn as nothing, no wash.
 *   none      not expected yet (in progress or in the future).
 */
export const BRIDGE_MAX_SLOTS = 2;
export const BRIDGE_MAX_MS = 60 * 60000;
/** A slot that ended within this grace period may not have been collected yet. */
export const AWAITING_MS = 15 * 60000;
/** Solar forecast at or below this (kWh per slot) means Predbat expects no sun. */
export const SOLAR_ASLEEP_KWH = 0.01;

export type SlotState = "value" | "zero" | "missing" | "awaiting" | "none";

export interface Measurement {
  value: number | null | undefined;
  /** Slot end, ms. */
  end: number;
  /** How the value was obtained ("measured", "boundary", "estimated", "idle"…). */
  method?: string | null;
  /** For solar: Predbat's forecast for the slot, kWh. */
  forecast?: number | null;
  metric?: "solar" | "home" | "ev" | "soc" | string;
}

export function classify(m: Measurement, now: number): SlotState {
  if (m.value != null && Number.isFinite(m.value)) return "value";
  if (m.end > now) return "none";
  if (m.method === "idle") return "zero";
  if (m.metric === "solar" && m.forecast != null && m.forecast <= SOLAR_ASLEEP_KWH) return "zero";
  if (m.metric === "ev" && m.forecast != null && m.forecast <= 0) return "zero";
  if (m.end > now - AWAITING_MS) return "awaiting";
  return "missing";
}

/** True for ≈ values: energy the counter proved, with its timing shared out between slots. */
export const approximate = (method: string | null | undefined) => method === "boundary" || method === "estimated";

export interface Gaps {
  /** Pairs of drawn slots [before, after] joined across a short run of missing slots. */
  bridges: [number, number][];
  /** Long runs of missing slots, as inclusive index ranges. */
  bands: [number, number][];
}

/** Finds the short gaps to bridge and the long ones to wash. `starts`/`ends` are slot bounds in ms. */
export function analyseGaps(states: SlotState[], starts: number[], ends: number[]): Gaps {
  const bridges: [number, number][] = [];
  const bands: [number, number][] = [];
  const drawn = (s: SlotState | undefined) => s === "value" || s === "zero";
  for (let i = 0; i < states.length;) {
    if (states[i] !== "missing") {
      i++;
      continue;
    }
    let j = i;
    while (j + 1 < states.length && states[j + 1] === "missing") j++;
    const span = ends[j] - starts[i];
    const before = i - 1,
      after = j + 1;
    if (drawn(states[before]) && drawn(states[after]) && j - i + 1 <= BRIDGE_MAX_SLOTS && span <= BRIDGE_MAX_MS)
      bridges.push([before, after]);
    else bands.push([i, j]);
    i = j + 1;
  }
  return { bridges, bands };
}

const meterNames: Record<string, string> = {
  solar: "Solar meter",
  home: "Home meter",
  ev: "Car charger meter",
  soc: "Battery level sensor",
};
/** "Solar meter offline 18:30–19:30" for a long gap. `range` formats the time span. */
export function gapLabel(metric: string, rangeText: string) {
  return `${meterNames[metric] ?? "Meter"} offline ${rangeText}`;
}

/** Tooltip wording for a slot state, or null when the value itself is shown. */
export function stateNote(state: SlotState, metric: string) {
  switch (state) {
    case "zero":
      return metric === "solar" ? "Solar sensor asleep overnight; counted as 0" : "Sensor idle; counted as 0";
    case "awaiting":
      return "Waiting for readings";
    case "missing":
      return "No readings";
    default:
      return null;
  }
}
