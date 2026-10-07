import { api } from "./api";

/**
 * Phone notifications (server: src/Joule.Api/Notifications/NotificationEndpoints.cs). Settings are saved like Setup's others
 * (settings.json in Joule's data folder; environment variables win) but apply at once, without a restart. Secrets are never
 * sent back: a secret field only says whether it is set.
 */

export interface PushField {
  /** e.g. Notifications:Ntfy:Topic */
  key: string;
  /** e.g. Notifications__Ntfy__Topic */
  envVar: string;
  label: string;
  kind: string;
  secret: boolean;
  required: boolean;
  /** Never set for secrets. */
  value: string | null;
  set: boolean;
  source: "environment" | "saved" | null;
  placeholder: string | null;
  note: string | null;
}
export interface PushEvent {
  id: string;
  label: string;
  description: string;
}
export interface PushChannel {
  id: string;
  name: string;
  description: string;
  enabled: boolean;
  /** Everything it needs is filled in. */
  ready: boolean;
  /** What's missing, in words, when not ready. */
  problem: string | null;
  events: string[];
  /** "22:00-07:00", or null for none. */
  quietHours: string | null;
  enabledField: PushField;
  eventsField: PushField;
  quietField: PushField;
  fields: PushField[];
}
export interface PushLogEntry {
  id: string;
  channel: string;
  channelName: string;
  title: string;
  /** queued, held, retrying, sent, failed or skipped. */
  status: string;
  error: string | null;
  attempts: number;
  at: string;
  /** When a waiting message goes next. */
  nextAt: string | null;
  test: boolean;
}
export interface PushSettingsView {
  canSave: boolean;
  locked: string | null;
  homeAssistantReady: boolean;
  publicUrl: string | null;
  offlineMinutes: number;
  summaryTime: string;
  maxPerHour: number;
  general: PushField[];
  events: PushEvent[];
  channels: PushChannel[];
  log: PushLogEntry[];
}

export const getPushSettings = () => api<PushSettingsView>("/push/settings");
/** A null or empty value removes the saved one. Answers with the settings as they are now. */
export const savePushSettings = (values: Record<string, string | null>) =>
  api<PushSettingsView>("/push/settings", { values });
export const testPush = (channel: string) =>
  api<{ ok: boolean; error: string | null }>(`/push/test/${encodeURIComponent(channel)}`, {});
export const homeAssistantNotifyServices = () =>
  api<{ services: string[]; error: string | null }>("/push/home-assistant/services");

/** The events setting for a set of choices: a comma list, or "none" (an empty value would mean "the defaults"). */
export const eventsValue = (events: string[]) => (events.length ? events.join(",") : "none");

/** "22:00-07:00" → ["22:00", "07:00"]; anything else → the usual night. */
export function quietParts(text: string | null): [string, string] {
  const m = /^(\d\d:\d\d)-(\d\d:\d\d)$/.exec(text ?? "");
  return m ? [m[1], m[2]] : ["22:00", "07:00"];
}

/** A log row's status as words and a chip tone. */
export function logStatus(entry: Pick<PushLogEntry, "status">): { label: string; tone: string } {
  switch (entry.status) {
    case "sent":
      return { label: "Sent", tone: "success" };
    case "failed":
      return { label: "Failed", tone: "danger" };
    case "retrying":
      return { label: "Trying again", tone: "warn" };
    case "held":
      return { label: "Waiting", tone: "neutral" };
    case "skipped":
      return { label: "Not sent", tone: "neutral" };
    default:
      return { label: "Queued", tone: "info" };
  }
}
