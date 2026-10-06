import { createContext, useContext } from "react";
import type { Api, EnergySummary, Mutate, ObservedMeterTrends, TelemetryStatus } from "../completion-types";
import type { EarlierPeriod } from "../lib/comparison";
import type { Health } from "../lib/health";
import type { Route } from "../lib/router";
import type { Payload, Proposal, Slot } from "../types";

/** Today's measured figures, shared by the Today cards and the charts. Kept (not cleared) when a refresh fails. */
export interface TelemetrySnapshot {
  telemetry: TelemetryStatus | null;
  /** Today so far. */
  daily: EnergySummary | null;
  /** The same stretch of yesterday as "today so far", for the comparison lines. */
  yesterday: { summary: EnergySummary; earlier: EarlierPeriod } | null;
  /** Elapsed half-hour slots of the last 24 hours, with measured actuals. */
  recent: Slot[];
  meterTrends: ObservedMeterTrends | null;
  /** Plain-language errors from the last refresh; empty when it worked (or when Joule itself is offline). */
  trendError: string;
  telemetryError: string;
  /** When the measured figures last refreshed successfully (ms since epoch). */
  fetchedAt: number | null;
  /** Fetches the measured figures again now. */
  retry: () => void;
}

export interface AboutInfo {
  /** Joule's version: from the server when it reports one, otherwise this build's. */
  version: string;
  predbatVersion?: string;
}

/**
 * Everything a page needs from the shell: the API, the latest server state, the household time zone, the route and
 * navigation. Pages read it with useApp(); only App provides it.
 */
export interface AppContextValue {
  api: Api;
  mutate: Mutate;
  /** True while a mutate() call is in flight. */
  busy: boolean;
  setBusy: (busy: boolean) => void;
  data: Payload;
  /** Fetches /api/state again. */
  load: () => Promise<void>;
  /** The household's IANA time zone (from the telemetry status). */
  timeZone: string;
  measured: TelemetrySnapshot;
  /** The current route (the URL is the source of truth). */
  route: Route;
  /** Goes to a route: "#/plan", or an old page name such as "Investigations". */
  go: (page: string) => void;
  reportError: (message: string) => void;
  /** A success toast, optionally with one next step ("Undo", "View") shown as a text button. */
  notify: (message: string, action?: { label: string; onClick: () => void }) => void;
  /** The investigation shown on Insights (from #/insights/inv/:id). */
  focusedInvestigation: string;
  /** Selects an investigation; on Insights this updates the URL. */
  setFocusedInvestigation: (id: string) => void;
  /** Opens the review dialog for a proposal. */
  reviewProposal: (proposal: Proposal) => void;
  /** Joule, Predbat and Home Assistant connection health (the status chip's model). */
  health: Health;
  /** True when Joule's server has missed two polls in a row; the data on screen is from `fetchedAt`. */
  offline: boolean;
  about: AboutInfo;
}

export const AppContext = createContext<AppContextValue | null>(null);

export function useApp(): AppContextValue {
  const value = useContext(AppContext);
  if (!value) throw new Error("useApp() must be used inside <AppContext.Provider>.");
  return value;
}
