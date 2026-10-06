import { useEffect, useRef, useState } from "react";
import type { SetupProgress } from "../types";
import { api, pollGet } from "./api";

/**
 * Setup's server contract: GET /api/setup (SetupEndpoints.cs), GET /api/setup/predbat-apps, and the change previews
 * (GET /api/settings/{key}/preview?value=, /api/revisions/{id}/revert/preview, /api/revisions/{id}/restore/preview).
 */

export interface SensorCandidate {
  entity: string;
  name: string | null;
  unit: string | null;
  state: string | null;
}
export interface SetupMeter {
  metric: string;
  /** e.g. HomeAssistant__Entities__Load */
  envVar: string;
  required: boolean;
  entity: string | null;
  status: string | null;
  unit: string | null;
  value: number | null;
  /** daily_counter, solar_daily, session_counter, lifetime_counter, price or state; null when not mapped. */
  profile: string | null;
  candidates: SensorCandidate[];
}
export interface SetupStatus {
  demo: boolean;
  progress: SetupProgress;
  predbat: {
    configured: boolean;
    address: string | null;
    lastCollection: string | null;
    error: string | null;
    version: string | null;
    writesEnabled: boolean;
  };
  sensors: {
    configured: boolean;
    direct: boolean;
    viaPredbat: boolean;
    address: string | null;
    lastCollection: string | null;
    error: string | null;
    meters: SetupMeter[];
  };
  mcp: {
    configured: boolean;
    connected: boolean;
    tools: number;
    canReadApps: boolean;
    error: string | null;
    checkedAt: string | null;
  };
}
export interface PredbatAppsView {
  available: boolean;
  reason: string | null;
  text: string | null;
  truncated: boolean;
  at: string;
}
export interface PreviewChange {
  key: string;
  name: string;
  before: string;
  after: string;
}
export interface AffectedTrial {
  id: string;
  title: string;
  startedAt: string;
  /** "rolled back" (the undo ends it) or "confounded" (its result can't be trusted any more). */
  effect: string;
}
export interface ChangePreview {
  allowed: boolean;
  reason: string | null;
  changes: PreviewChange[];
  notRestored: { key: string; name: string; kind: string; storedValue: string; currentValue: string }[];
  affectedExperiments: AffectedTrial[];
}

/**
 * The setup checklist's status. Fetched when Setup opens and again whenever Predbat's settings, the last collection or
 * the sensor readings change; `refresh` fetches it now (after a "Test connection").
 */
export function useSetupStatus(deps: unknown[]) {
  const [status, setStatus] = useState<SetupStatus | null>(null);
  const [error, setError] = useState("");
  const [tick, setTick] = useState(0);
  const latest = useRef(0);
  useEffect(() => {
    const controller = new AbortController();
    const n = ++latest.current;
    pollGet<SetupStatus>("/setup", controller.signal)
      .then((s) => {
        if (n !== latest.current) return;
        setStatus(s);
        setError("");
      })
      .catch((e: Error) => {
        if (!controller.signal.aborted && n === latest.current) setError(e.message);
      });
    return () => controller.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...deps, tick]);
  return { status, error, refresh: () => setTick((t) => t + 1) };
}

export const previewEdit = (key: string, value: string) =>
  api<ChangePreview>(`/settings/${encodeURIComponent(key)}/preview?value=${encodeURIComponent(value)}`);
export const previewUndo = (id: number) => api<ChangePreview>(`/revisions/${id}/revert/preview`);
export const previewRestore = (id: number) => api<ChangePreview>(`/revisions/${id}/restore/preview`);

/** "This ends the trial “…” early (started 2 days ago)." / "The trial “…” (started …) can't be judged fairly after this." */
export function trialWarning(t: AffectedTrial, startedLabel: string) {
  return t.effect === "rolled back"
    ? `This ends the trial “${t.title}” early (started ${startedLabel}).`
    : `The trial “${t.title}” (started ${startedLabel}) can't be judged fairly after this: it will need your review.`;
}

/**
 * The checklist's own count: "Connect to Predbat" and "Read Predbat's plan" are one row there, so they count once.
 * Returns e.g. { done: 3, total: 6, requiredDone: false }.
 */
export function checklistCounts(progress: SetupProgress) {
  const merged = progress.steps.filter((s) => s.key !== "collecting");
  const predbatDone = progress.steps.filter((s) => s.key === "predbat" || s.key === "collecting").every((s) => s.done);
  const done = merged.filter((s) => (s.key === "predbat" ? predbatDone : s.done)).length;
  return { done, total: merged.length, requiredDone: progress.requiredDone };
}

const AUTO_OPENED = "joule.setupAutoOpened";

/**
 * Whether a fresh visit should open Setup: a live install whose required steps aren't done, reached at the app's root
 * (not a deep link), once per browser session.
 */
export function shouldAutoOpenSetup(
  initialHash: string,
  progress: SetupProgress | null,
  demo: boolean,
  storage: Pick<Storage, "getItem"> = sessionStorage,
) {
  if (demo || !progress || progress.requiredDone) return false;
  if (!["", "#", "#/", "#/today"].includes(initialHash)) return false;
  return storage.getItem(AUTO_OPENED) !== "1";
}

/**
 * The live setup progress for the shell (status chip and the first-visit redirect). Not fetched in demo mode, where
 * there is nothing to set up. Refetched when Predbat is read again or its settings change.
 */
export function useSetupProgress(demo: boolean, deps: unknown[]) {
  const [progress, setProgress] = useState<SetupProgress | null>(null);
  useEffect(() => {
    if (demo) {
      setProgress(null);
      return;
    }
    const controller = new AbortController();
    pollGet<SetupStatus>("/setup", controller.signal)
      .then((s) => setProgress(s.progress))
      .catch(() => {});
    return () => controller.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [demo, ...deps]);
  return progress;
}

/** Opens Setup once on a fresh visit while required setup steps are missing (see shouldAutoOpenSetup). */
export function useSetupAutoOpen(progress: SetupProgress | null, demo: boolean | undefined, open: () => void) {
  const initial = useRef(typeof window === "undefined" ? "" : window.location.hash);
  const decided = useRef(false);
  useEffect(() => {
    // Wait for the first state (demo known) and, on a live install, the first setup progress.
    if (decided.current || demo === undefined || (!demo && !progress)) return;
    decided.current = true;
    // Not if the person has already moved on to another page while this loaded.
    const stillAtRoot = ["", "#", "#/", "#/today"].includes(window.location.hash);
    if (stillAtRoot && shouldAutoOpenSetup(initial.current, progress, demo)) {
      sessionStorage.setItem(AUTO_OPENED, "1");
      open();
    }
  }, [progress, demo, open]);
}
