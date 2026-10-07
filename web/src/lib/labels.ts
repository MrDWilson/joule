import type { Tone } from "../components/ui/Chip";
import { words } from "./copy";

/**
 * Server enums and identifiers in plain words. Nothing the server names internally ("ChatGpt", "Denied", "HomeAssistant",
 * "grid_export") is shown as-is.
 */

const metricLabels: Record<string, string> = {
  load: words.homeUse,
  pv: "Solar",
  grid_import: "Grid import",
  grid_export: "Grid export",
  battery_charge: "Battery charge",
  battery_discharge: "Battery discharge",
  ev: "Car charging",
  soc: "Battery level",
  import_tariff: "Import price",
  export_tariff: "Export price",
  standing_charge: "Standing charge",
  intelligent_slots: "Octopus smart-charge slots",
};
/** "load" → "Home use", "grid_export" → "Grid export"; an unknown key reads as words, never snake_case. */
export const metricLabel = (key: string) => metricLabels[key] ?? humanise(key);

/** "some_internal_key" or "SomeInternalKey" → "Some internal key". */
export function humanise(key: string) {
  const spaced = key
    .replace(/[_.-]+/g, " ")
    .replace(/([a-z])([A-Z])/g, "$1 $2")
    .trim()
    .toLowerCase();
  return spaced ? spaced[0].toUpperCase() + spaced.slice(1) : key;
}

const providers: Record<string, string> = {
  ChatGpt: "ChatGPT",
  chatgpt: "ChatGPT",
  Api: "AI API",
  api: "AI API",
  Demo: "Demo",
  demo: "Demo",
};
/** "ChatGpt" → "ChatGPT", "Api" → "AI API". */
export const providerLabel = (provider: string | null | undefined) =>
  provider ? (providers[provider] ?? provider) : "Not set";

const sources: Record<string, string> = {
  HomeAssistant: "Home Assistant",
  "Predbat mirror": "Home Assistant (through Predbat)",
  Predbat: "Predbat",
  Live: "Predbat",
  You: "You",
  Timeline: "Predbat's plan",
  "Initial snapshot": "First reading",
  Demo: "Demo",
  "Demo telemetry": "Demo readings",
  "Demo telemetry (scripted)": "Demo readings",
};
/** Where a record or reading came from, as people say it: "HomeAssistant" → "Home Assistant". */
export const sourceLabel = (source: string | null | undefined) => {
  if (!source) return "Unknown";
  return sources[source] ?? source.replace(/([a-z])([A-Z])/g, "$1 $2");
};
/** Kept for older imports. */
export const sourceName = (s: string) => sourceLabel(s);

/** A reading's health in plain words. */
const readings: Record<string, string> = {
  observed: "OK",
  unavailable: "Sensor offline",
  unknown: "No value (idle)",
  idle: "Idle",
  invalid: "Invalid reading",
  reset: "Meter reset",
  stale: "Out of date",
  unsupported_unit: "Unsupported unit",
  spread: "Shared across a gap",
  gap: "No readings",
};
export const readingStatus = (status: string) => readings[status] ?? humanise(status);

export interface StatusLabel {
  label: string;
  tone: Tone;
}
const statuses: Record<string, StatusLabel> = {
  // Suggestions
  pending: { label: "Ready for review", tone: "success" },
  approved: { label: "Approved", tone: "success" },
  applied: { label: "Applied", tone: "success" },
  denied: { label: words.declined, tone: "neutral" },
  declined: { label: words.declined, tone: "neutral" },
  dismissed: { label: words.declined, tone: "neutral" },
  superseded: { label: words.noLongerNeeded, tone: "neutral" },
  retired: { label: words.noLongerNeeded, tone: "neutral" },
  closed: { label: "Closed", tone: "neutral" },
  verified: { label: "Checked", tone: "success" },
  open: { label: "Open", tone: "info" },
  // AI checks and usage
  running: { label: "Running", tone: "info" },
  completed: { label: "Finished", tone: "success" },
  failed: { label: "Didn't finish", tone: "neutral" },
  interrupted: { label: "Didn't finish", tone: "neutral" },
  cancelled: { label: "Stopped", tone: "neutral" },
  // Experiments
  active: { label: "Running", tone: "info" },
  monitoring: { label: "Running", tone: "info" },
  extended: { label: "Running longer", tone: "info" },
  kept: { label: "Kept", tone: "success" },
  reverted: { label: "Undone", tone: "neutral" },
  inconclusive: { label: "Too early to tell", tone: "warn" },
  uncertain: { label: "Not sure", tone: "warn" },
  reconciled: { label: "Matched", tone: "success" },
  // Reports
  unread: { label: "Unread", tone: "success" },
};
/** A server status in plain words with a chip tone: "Denied" → Declined (neutral), "Failed" → Didn't finish. */
export function statusLabel(status: string | null | undefined): StatusLabel {
  if (!status) return { label: "Unknown", tone: "neutral" };
  return statuses[status.toLowerCase()] ?? { label: humanise(status), tone: "neutral" };
}

const reportKinds: Record<string, string> = {
  daily: "Daily report",
  weekly: "Weekly report",
  manual: "Report",
  custom: "Report",
};
export const reportKindLabel = (kind: string | null | undefined) =>
  (kind && reportKinds[kind.toLowerCase()]) ?? "Report";
