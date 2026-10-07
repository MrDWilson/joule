import { useCallback, useEffect, useState } from "react";
import { api } from "./api";

/**
 * Setup's write side (SetupConfig.cs): settings saved in Joule's data folder (environment variables always win), the
 * Predbat probe and finder, sensor suggestions from Predbat's apps.yaml, and waiting for Joule to restart after a save.
 */

export interface SetupConfigField {
  /** e.g. Predbat:BaseUrl */
  key: string;
  /** e.g. Predbat__BaseUrl */
  envVar: string;
  /** Notification settings add their own kinds (topic, events, quietHours…); see pushApi.ts. */
  kind: "bool" | "url" | "entity" | "accessKey" | "token" | (string & {});
  secret: boolean;
  /** Never set for secrets. */
  value: string | null;
  set: boolean;
  /** "environment" wins and can't be changed here; "saved" came from Setup; null is the default. */
  source: "environment" | "saved" | null;
  /** Saved, but Joule hasn't restarted with it yet. */
  pending: boolean;
}
export interface SetupConfig {
  demo: boolean;
  canSave: boolean;
  locked: string | null;
  settingsFile: string;
  inContainer: boolean;
  accessKeyNeeded: boolean;
  restartPending: boolean;
  fields: SetupConfigField[];
}
export interface PredbatProbe {
  url: string;
  ok: boolean;
  version: string | null;
  entities: number;
  error: string | null;
  milliseconds: number;
}
export interface MeterSuggestion {
  metric: string;
  /** e.g. HomeAssistant:Entities:Load */
  key: string;
  envVar: string;
  current: string | null;
  entity: string | null;
  /** "load_today in apps.yaml" or "name and unit match" */
  from: string | null;
  state: string | null;
  unit: string | null;
  alternatives: { entity: string; name: string | null; unit: string | null; state: string | null }[];
  /** Sure enough that Joule uses it without asking. */
  confident?: boolean;
  /** The current mapping was found automatically from Predbat. */
  auto?: boolean;
  /** Left unmapped on purpose ("none"). */
  declined?: boolean;
}
export interface MeterDetection {
  appsSource: string | null;
  appsError: string | null;
  haveEntities: boolean;
  meters: MeterSuggestion[];
  /** Predbat's metric_standing_charge when apps.yaml gives a number, in pence a day. */
  standingChargePence?: number | null;
}

/** The value that leaves a meter unmapped on purpose, so Joule doesn't fill it in from Predbat either. */
export const NOT_MAPPED_VALUE = "none";

/**
 * What saving the mapper would change: each meter whose choice differs from what is in use now, except those set in the
 * environment. Choosing "Not mapped" for a meter that has a sensor (chosen or found automatically) saves "none", so Joule
 * doesn't find it again; for a meter with nothing in use it changes nothing.
 */
export function meterChanges(
  meters: MeterSuggestion[],
  choice: Record<string, string>,
  fromEnvironment: (key: string) => boolean,
): Record<string, string | null> {
  const changes: Record<string, string | null> = {};
  for (const m of meters) {
    if (fromEnvironment(m.key) || !(m.key in choice)) continue;
    const chosen = choice[m.key];
    const current = m.current ?? "";
    if (chosen === current) continue;
    changes[m.key] = chosen || NOT_MAPPED_VALUE;
  }
  return changes;
}

/** Where an automatically found sensor came from, in words: "load_today in apps.yaml" → "Predbat's apps.yaml (load_today)". */
export function foundFromLabel(from: string | null | undefined) {
  if (!from) return "Found automatically from Predbat";
  const key = /^([a-z0-9_]+) in apps\.yaml$/.exec(from)?.[1];
  return key ? `Found automatically in Predbat's apps.yaml (${key})` : "Found automatically among Predbat's sensors";
}

export const getSetupConfig = () => api<SetupConfig>("/setup/config");
export const testPredbat = (url: string) => api<PredbatProbe>("/setup/predbat/test", { url });
export const findPredbat = () => api<PredbatProbe[]>("/setup/predbat/find", {});
export const detectMeters = () => api<MeterDetection>("/setup/meters/detect");
export const saveSetup = (values: Record<string, string | null>, restart: boolean) =>
  api<{ ok: boolean; restarting: boolean }>("/setup/config", { values, restart });

/** The field for a key, or undefined on an older server. */
export function field(config: SetupConfig | null, key: string) {
  return config?.fields.find((f) => f.key === key);
}

/** True when the environment sets this value, so Setup shows it read-only. */
export const fromEnvironment = (config: SetupConfig | null, key: string) =>
  field(config, key)?.source === "environment";

/** The setup config, refetched on demand (after a save). */
export function useSetupConfig() {
  const [config, setConfig] = useState<SetupConfig | null>(null);
  const [error, setError] = useState("");
  const [tick, setTick] = useState(0);
  useEffect(() => {
    let live = true;
    getSetupConfig()
      .then((c) => live && (setConfig(c), setError("")))
      .catch((e: Error) => live && setError(e.message));
    return () => {
      live = false;
    };
  }, [tick]);
  return { config, error, refresh: useCallback(() => setTick((t) => t + 1), []) };
}

/**
 * A new access key: 24 random bytes as URL-safe base64 (32 characters), the same strength as `openssl rand -base64 24`
 * and safe to type, paste and send in a header.
 */
export function generateAccessKey(
  random: (bytes: Uint8Array<ArrayBuffer>) => Uint8Array = (b) => crypto.getRandomValues(b),
) {
  const bytes = random(new Uint8Array(24));
  let text = "";
  bytes.forEach((b) => (text += String.fromCharCode(b)));
  return btoa(text).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

/** The address as the person typed it, tidied: trimmed, no trailing slash, http:// added when there's no scheme. */
export function normaliseAddress(text: string) {
  const t = text.trim().replace(/\/+$/, "");
  if (!t) return "";
  return /^[a-z][a-z0-9+.-]*:\/\//i.test(t) ? t : `http://${t}`;
}

async function startedAt(): Promise<string | null> {
  try {
    const response = await fetch("/api/health", { cache: "no-store" });
    if (!response.ok) return null;
    const body = (await response.json()) as { started?: string };
    return body.started ?? null;
  } catch {
    return null;
  }
}

/**
 * Saves and restarts Joule, then resolves once the restarted server answers (its /api/health "started" changed).
 * Rejects after `timeoutMs` without a restart.
 */
export async function saveAndRestart(
  values: Record<string, string | null>,
  { timeoutMs = 90_000, intervalMs = 750 }: { timeoutMs?: number; intervalMs?: number } = {},
) {
  const before = await startedAt();
  await saveSetup(values, true);
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    await new Promise((r) => setTimeout(r, intervalMs));
    const now = await startedAt();
    if (now && now !== before) return;
  }
  throw new Error(
    "Joule hasn't come back after restarting. Check its log (docker compose logs joule) and reload this page.",
  );
}
