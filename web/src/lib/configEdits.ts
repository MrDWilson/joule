import { useEffect, useState } from "react";
import { api } from "./api";
import type { ConfigFileChange, ConfigFileEdit } from "../types";

/**
 * "Apply for me" for configuration-file edits (ConfigFileEdits.cs). Joule can make an edit to Predbat's apps.yaml itself when the file
 * is mounted into its container and the owner has switched editing on in Setup. It reviews a masked diff first, keeps a copy of the file,
 * watches Predbat reload it, and puts the copy back if Predbat has a problem.
 */

export interface ConfigEditStatus {
  demo: boolean;
  /** apps.yaml is mounted and listed (ConfigFiles__Root, ConfigFiles__AllowedFiles__0). */
  configured: boolean;
  file: string | null;
  /** Joule's process can write the file. */
  writable: boolean;
  /** "Let Joule edit Predbat's config files" is on. */
  allowed: boolean;
  /** "environment" when ConfigFiles__AllowEdits is set there (Setup can't change it), "saved" from Setup, null by default. */
  allowedSource: "environment" | "saved" | null;
  quarantined: boolean;
  canApply: boolean;
  /** What is missing, in words; null when Joule can apply edits. */
  reason: string | null;
  /** The mounted folder inside Joule's container, e.g. /predbat-config. */
  root: string | null;
}

export interface DiffLine {
  /** " " unchanged, "-" removed, "+" added, "…" unchanged lines left out. */
  kind: " " | "-" | "+" | "…";
  old: number | null;
  new: number | null;
  text: string;
}

export interface ConfigEditReview {
  file: string;
  canApply: boolean;
  /** Why Joule can't apply edits at all (set up, permission). */
  reason: string | null;
  /** Why Joule won't make this particular edit (the lines aren't there, invalid YAML…). */
  problem: string | null;
  /** The file as reviewed; Apply sends it back so nothing is written if the file changed since. */
  hash: string | null;
  placement: string | null;
  keys: { key: string; name: string; change: "added" | "removed" | "changed" }[];
  lines: DiffLine[];
  note: string;
}

/** The environment lines that let Joule edit apps.yaml (beside the compose volume line). */
export const EDIT_ENV_LINES = [
  "ConfigFiles__Root=/predbat-config",
  "ConfigFiles__AllowedFiles__0=apps.yaml",
  "ConfigFiles__AllowEdits=true",
];
/** The compose volume: Predbat's config folder (the one holding apps.yaml) mounted into Joule. */
export const EDIT_VOLUME_LINES = ["    volumes:", "      - ./predbat/config:/predbat-config"];

let cached: { at: number; promise: Promise<ConfigEditStatus | null> } | null = null;
const listeners = new Set<() => void>();

/** Forget the cached status (after Setup saves, or an apply), so every card asks again. */
export function refreshConfigEditStatus() {
  cached = null;
  listeners.forEach((l) => l());
}

function loadStatus(): Promise<ConfigEditStatus | null> {
  if (!cached || Date.now() - cached.at > 30_000)
    cached = {
      at: Date.now(),
      // An older server has no such endpoint: treat it as "not set up", which keeps the copy-it-yourself path.
      promise: api<ConfigEditStatus>("/config-edits/status").catch(() => null),
    };
  return cached.promise;
}

/** Whether Joule can edit config files, shared by every card on the page (one request at most every 30 s). */
export function useConfigEditStatus() {
  const [status, setStatus] = useState<ConfigEditStatus | null>(null);
  const [tick, setTick] = useState(0);
  useEffect(() => {
    const listener = () => setTick((t) => t + 1);
    listeners.add(listener);
    return () => {
      listeners.delete(listener);
    };
  }, []);
  useEffect(() => {
    let live = true;
    void loadStatus().then((s) => live && setStatus(s));
    return () => {
      live = false;
    };
  }, [tick]);
  return status;
}

const base = (investigationId: string, changeId: string) =>
  `/investigations/${encodeURIComponent(investigationId)}/filechanges/${encodeURIComponent(changeId)}`;
export const reviewPath = (investigationId: string, changeId: string) => `${base(investigationId, changeId)}/review`;
export const applyPath = (investigationId: string, changeId: string) => `${base(investigationId, changeId)}/apply`;
export const restorePath = (investigationId: string, changeId: string) => `${base(investigationId, changeId)}/restore`;

/** Joule made this edit and it is still in the file (so "Restore previous version" applies). */
export const jouleEditInPlace = (change: Pick<ConfigFileChange, "edit">) =>
  !!change.edit && ["checking", "confirmed", "unconfirmed"].includes(change.edit.check);

/** One plain sentence about how Predbat took Joule's edit. */
export function editOutcome(
  edit: ConfigFileEdit,
  file: string,
  at: string,
): { text: string; tone: "info" | "success" | "warn" } {
  const note = edit.checkNote ?? "";
  switch (edit.check) {
    case "checking":
      return { text: `Joule made this edit ${at}. Watching Predbat reload ${file}…`, tone: "info" };
    case "confirmed":
      return { text: `Joule made this edit ${at}. ${note || `Predbat reloaded ${file}.`}`, tone: "success" };
    case "unconfirmed":
      return { text: `Joule made this edit ${at}. ${note}`, tone: "info" };
    case "rolled_back":
      return { text: `Joule made this edit ${at}, then undid it. ${note}`, tone: "warn" };
    case "restored":
      return { text: `Joule made this edit ${at}. You put the previous ${file} back.`, tone: "info" };
    default:
      return { text: `Joule made this edit ${at}. ${note}`, tone: "warn" };
  }
}
