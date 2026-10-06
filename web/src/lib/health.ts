import type { TelemetryStatus } from "../completion-types";
import type { Payload } from "../types";
import type { Tone } from "../components/ui/Chip";
import { abilityLabel, modeLabel } from "./copy";
import { metricLabel, providerLabel } from "./labels";
import { ago, clock } from "./time";

/**
 * One health model for the status chip, its popover and the alert rows: is Joule's own server answering, is Predbat
 * being read, are the Home Assistant sensors reporting, can Joule change Predbat, and what the AI may do.
 */

export interface HealthInput {
  data: Payload;
  telemetry: TelemetryStatus | null;
  /** True after two polls in a row failed to reach Joule's server. */
  offline: boolean;
  /** When /api/state last answered (ms since epoch). */
  fetchedAt: number | null;
  now?: number;
}

export interface HealthRow {
  key: "joule" | "predbat" | "sensors" | "writes" | "ai";
  label: string;
  detail: string;
  tone: Tone;
}

export interface Health {
  chip: { label: string; tone: Tone };
  rows: HealthRow[];
  /** Minutes since Predbat was last read, when known. */
  predbatAgeMinutes: number | null;
}

/** Predbat is read every 5 minutes; older than this and its data is called out of date. */
export const PREDBAT_STALE_MINUTES = 30;

export function health({ data, telemetry, offline, fetchedAt, now = Date.now() }: HealthInput): Health {
  const s = data.state,
    c = data.connection;
  const last = s.lastCollection ? Date.parse(s.lastCollection) : NaN;
  const predbatAgeMinutes = Number.isFinite(last) ? Math.max(0, Math.round((now - last) / 60000)) : null;
  const predbatStale = predbatAgeMinutes != null && predbatAgeMinutes > PREDBAT_STALE_MINUTES;

  const joule: HealthRow = offline
    ? {
        key: "joule",
        label: "Joule",
        detail: `Can't reach Joule's server${fetchedAt ? ` · showing data from ${clock(fetchedAt)}` : ""}. Retrying.`,
        tone: "danger",
      }
    : {
        key: "joule",
        label: "Joule",
        detail: fetchedAt ? `Connected · updated ${clock(fetchedAt)}` : "Connected",
        tone: "success",
      };

  const predbat: HealthRow = c.demo
    ? { key: "predbat", label: "Predbat", detail: "Demo data: a made-up house and plan.", tone: "info" }
    : !c.predbatConfigured
      ? {
          key: "predbat",
          label: "Predbat",
          detail: "Not set up yet. Joule needs Predbat's address to read its plan.",
          tone: "warn",
        }
      : s.collectionError
        ? {
            key: "predbat",
            label: "Predbat",
            detail: `Can't read Predbat: ${s.collectionError.replace(/\.+$/, "")}.`,
            tone: "warn",
          }
        : s.lastCollection
          ? {
              key: "predbat",
              label: "Predbat",
              detail: `Read ${ago(s.lastCollection, { now })}${predbatStale ? " · out of date" : ""}`,
              tone: predbatStale ? "warn" : "success",
            }
          : { key: "predbat", label: "Predbat", detail: "Waiting for the first reading.", tone: "neutral" };

  const offlineSensors = telemetry
    ? Object.entries(telemetry.latestReadings ?? {})
        .filter(([, r]) => r.status === "unavailable")
        .map(([k]) => metricLabel(k))
    : [];
  const sensors: HealthRow = !telemetry
    ? { key: "sensors", label: "Home Assistant", detail: "Checking the sensors…", tone: "neutral" }
    : telemetry.demo
      ? { key: "sensors", label: "Home Assistant", detail: "Demo readings.", tone: "info" }
      : !telemetry.configured
        ? { key: "sensors", label: "Home Assistant", detail: "Sensors not set up yet.", tone: "warn" }
        : telemetry.error && !offlineSensors.length
          ? { key: "sensors", label: "Home Assistant", detail: telemetry.error, tone: "warn" }
          : {
              key: "sensors",
              label: "Home Assistant",
              detail:
                (offlineSensors.length
                  ? `${offlineSensors.length === 1 ? "1 sensor" : `${offlineSensors.length} sensors`} offline: ${offlineSensors.join(", ")}`
                  : "All sensors reporting") +
                (telemetry.lastCollection ? ` · read ${ago(telemetry.lastCollection, { now })}` : ""),
              tone: offlineSensors.length ? "warn" : "success",
            };

  const writes: HealthRow = {
    key: "writes",
    label: "Changes to Predbat",
    detail: c.demo
      ? "Demo: approvals are saved here and never sent anywhere."
      : c.writesEnabled
        ? "Joule can apply the changes you approve."
        : "Read-only. Joule suggests changes; you make them in Predbat yourself.",
    tone: c.demo ? "info" : c.writesEnabled ? "success" : "neutral",
  };

  const ai: HealthRow = {
    key: "ai",
    label: "AI",
    detail: `${modeLabel(s.mode)} · ${providerLabel(s.ai.provider)}${data.ai.running ? " · checking now" : ""}. ${abilityLabel(s.mode, c.writesEnabled, c.demo)}.`,
    tone: data.ai.running ? "info" : "success",
  };

  const chip: Health["chip"] = offline
    ? { label: "Joule offline", tone: "danger" }
    : c.demo
      ? { label: "Demo", tone: "info" }
      : !c.predbatConfigured
        ? { label: "Not set up", tone: "warn" }
        : s.collectionError
          ? { label: "Can't reach Predbat", tone: "warn" }
          : predbatStale
            ? { label: "Predbat out of date", tone: "warn" }
            : c.writesEnabled
              ? { label: "Connected", tone: "success" }
              : { label: "Read-only", tone: "success" };

  return { chip, rows: [joule, predbat, sensors, writes, ai], predbatAgeMinutes };
}
