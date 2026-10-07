import type { Investigation, MemoryFact, Revision, Setting, State, ReportSchedules } from "./types";
export type Api = <T>(path: string, body?: unknown) => Promise<T>;
export type Mutate = (path: string, body: unknown, message: string) => Promise<boolean>;
export interface EnergyMetric {
  energyKwh: number | null;
  observedSeconds: number;
  coverageFraction: number;
  missingIntervals: number;
  // Added by the accounting work (server TelemetryModels.cs); optional for older servers.
  measuredSeconds?: number;
  idleSeconds?: number;
  estimatedSeconds?: number;
  estimatedKwh?: number | null;
  missingKwh?: number | null;
  gaps?: EnergyGap[];
  /** For daily counters on a window starting at local midnight: the meter's own total for the day. */
  counterDayTotalKwh?: number | null;
  /** True when the figure is within 0.05 kWh of counterDayTotalKwh. */
  reconciled?: boolean | null;
  /** daily_counter, session_counter, solar_daily, lifetime_counter or derived. */
  profile?: string | null;
  /** complete, idle_zero, estimated, partial, missing or no_records. */
  state?: string;
  coverageFrom?: string | null;
  coverageTo?: string | null;
}
/** A stretch a meter couldn't account for: offline, idle, not_found, invalid, no_samples, reset or source_changed. */
export interface EnergyGap {
  from: string;
  to: string;
  reason: string;
  /** Energy the counter proved flowed during the gap, when known. */
  knownKwh?: number | null;
}
export interface CostGap {
  metric: string;
  from: string;
  to: string;
  reason: string;
}
export interface EnergySummary {
  from: string;
  to: string;
  metrics: Record<string, EnergyMetric>;
  importCostGbp: number | null;
  exportCreditGbp: number | null;
  observedNetCostGbp: number | null;
  costCoverageFraction: number;
  costObservedSeconds: number;
  sources: string[];
  limitations: string[];
  /** Import cost minus export credit, each side with its own coverage: the headline figure. */
  netCostGbp?: number | null;
  importCostCoverage?: number;
  exportCostCoverage?: number;
  /** Home use excluding the car (load minus ev), when the load meter includes the car and the car meter covers the window. */
  home?: EnergyMetric | null;
  /** Whether the load meter includes the car's charging; null when unknown. */
  loadIncludesEv?: boolean | null;
  importCostEstimated?: boolean;
  exportCostEstimated?: boolean;
  unpricedGridKwh?: number;
  gridEnergyUnknown?: boolean;
  estimatedCostGbp?: number;
  costGaps?: CostGap[];
  /** The standing charge for the window (£), prorated by the share of each day covered up to now. Never part of netCostGbp. */
  standingChargeGbp?: number | null;
  /** The rate on the window's last day, pence per day. */
  standingChargePencePerDay?: number | null;
  /** "sensor" (Home Assistant) or "manual" (typed into Setup). */
  standingChargeSource?: string | null;
  /** An early day took the first rate Joule recorded. */
  standingChargeAssumed?: boolean;
  /** The owner's choice: the headline net cost includes the standing charge. */
  standingChargeIncluded?: boolean;
  netCostWithStandingChargeGbp?: number | null;
}

/** GET /api/telemetry/standing-charge. */
export interface StandingChargeSettings {
  includeInNet: boolean;
  manualPencePerDay: number | null;
  /** The Home Assistant sensor Joule reads; origin "octopus" when worked out from the Octopus import rate sensor. */
  entity: string | null;
  entityOrigin: "configured" | "octopus" | null;
  sensorPencePerDay: number | null;
  sensorAt: string | null;
  sensorStatus: string | null;
  todayPencePerDay: number | null;
  todaySource: string | null;
  recent: { day: string; pencePerDay: number; source: string; entityId: string | null; recordedAt: string }[];
}
export interface LatestReading {
  value: number | null;
  unit: string;
  time: string;
  status: string;
  entityId: string;
  source: string;
  rawState?: string | null;
  rawUnit?: string | null;
  sourceUpdatedAt?: string | null;
  /** The last usable reading when the latest is idle or offline. */
  lastObservedValue?: number | null;
  lastObservedAt?: string | null;
  /** True when a non-observed state is normal for this sensor (a charger between sessions, solar overnight). */
  expected?: boolean;
  /** Why, in plain words. */
  reason?: string | null;
  ageSeconds?: number;
  stale?: boolean;
  profile?: string | null;
}
export interface TelemetryStatus {
  firstObservationAt?: string | null;
  demo: boolean;
  configured: boolean;
  homeAssistantDirect: boolean;
  lastSource: string | null;
  timeZone: string;
  lastCollection: string | null;
  error: string | null;
  entityMappings: Record<string, string>;
  missingMappings: string[];
  maxGapMinutes: number;
  latestReadings: Record<string, LatestReading>;
  profiles?: Record<string, string>;
  loadIncludesEv?: boolean | null;
}
export interface PlanEvidence {
  planId: string;
  slots: {
    time: string;
    durationMinutes: number;
    actual: EnergySummary;
    estimatedLoadKwh: number | null;
    estimatedPvKwh: number | null;
    estimateMethod: string;
  }[];
}
export interface DocumentationReference {
  id: string;
  version: string;
  path: string;
  url: string;
  contentSha256: string;
  startLine: number;
  endLine: number;
  excerpt: string;
  cachedAt: string;
}
export interface ToolEvidence {
  id: string;
  kind: string;
  request: string;
  retrievedAt: string;
  success: boolean;
  resultJson: string;
  sourceReferences: DocumentationReference[];
  error: string | null;
  /** Plain description of what was read ("Read Predbat's log for “Warn”, 02:05–03:06"); request keeps the raw call. */
  label?: string | null;
}
export interface FileVersion {
  id: string;
  at: string;
  runtimeRevision: number;
  reason: string;
  restoredFrom: string | null;
  files: { path: string; hash: string; bytes: number }[];
}
export interface FileInventory {
  status: {
    enabled: boolean;
    quarantined: boolean;
    latestVersion: string | null;
    allowedFiles: string[];
    reason: string | null;
  };
  versions: FileVersion[];
}
export interface FileView {
  version: string;
  file: string;
  hash: string;
  text: string;
  redaction: string;
}
export interface FileDiff {
  from: string;
  to: string;
  file: string;
  changed: boolean;
  before: string;
  after: string;
  redaction: string;
  /** The settings that differ, by key, without their values. */
  keys?: { key: string; name: string; change: "added" | "removed" | "changed" }[] | null;
}
export interface ReportPreferences {
  dailyEnabled: boolean;
  weeklyEnabled: boolean;
  timeZone: string;
  hourLocal: number;
  /** Set by the server once daily reports are on by default or the user has saved a choice. */
  defaultsVersion?: number;
}
export interface EnergyReport {
  timeZone?: string | null;
  id: string;
  createdAt: string;
  kind: string;
  from: string;
  to: string;
  title: string;
  summary: string;
  isDemo: boolean;
  readAt: string | null;
  energySummary: EnergySummary | null;
  days: EnergySummary[];
  investigationIds: string[];
  /** 2 when the title and summary use the plain wording and only the period is stored. */
  textVersion?: number;
}
/** GET /api/reports/{id}: a saved report recomputed from the meters now. */
export interface ReportView {
  id: string;
  createdAt: string;
  kind: string;
  timeZone: string;
  from: string;
  to: string;
  title: string;
  summary: string;
  isDemo: boolean;
  readAt: string | null;
  partial: boolean;
  hasReadings: boolean;
  energySummary: EnergySummary;
  days: EnergySummary[];
  relatedChecks: { id: string; at: string; title: string }[];
}
export interface Notification {
  id: string;
  at: string;
  title: string;
  message: string;
  reportId: string;
  readAt: string | null;
}
export interface CompletionProps {
  api: Api;
  mutate: Mutate;
  busy: boolean;
}
export interface PermissionProps extends CompletionProps {
  setting: Setting;
  close: () => void;
}
export interface FilesProps extends CompletionProps {
  revision: number;
  revisions: Revision[];
  demo: boolean;
  pendingReload: boolean;
  mode: string;
  writeUncertain: boolean;
  onHistory: () => void;
}
export interface ReportsProps extends CompletionProps {
  schedules?: ReportSchedules;
  firstObservationAt?: string | null;
  preferences: ReportPreferences;
  reports: EnergyReport[];
  notifications: Notification[];
  onInvestigation: (id: string) => void;
}
export interface InvestigationProps extends CompletionProps {
  investigations: Investigation[];
  memory?: MemoryFact[];
  activities: State["activities"];
  running: boolean;
  selectedId: string;
  onSelect: (id: string) => void;
}

export interface ObservedMeterTrends {
  from: string;
  to: string;
  intervals: {
    metric: string;
    start: string;
    end: string;
    averageKw: number | null;
    source: string;
    entityId: string | null;
    status: string;
  }[];
  truncated: boolean;
  limit: number;
  method: string;
  /** Half-hour power series for load, pv, ev and (when the load meter includes the car) home. */
  series?: MeterPowerSeries[];
}
export interface MeterPowerSeries {
  metric: string;
  entityId: string | null;
  start: string;
  stepMinutes: number;
  kw: (number | null)[];
  /** measured, idle, estimated, missing or pending, per step. */
  status: string[];
  peakKw: number | null;
  peakAt: string | null;
}
