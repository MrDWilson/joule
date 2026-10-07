import type { ToolEvidence, ReportPreferences, EnergyReport, Notification } from "./completion-types";
export interface Change {
  key: string;
  before: string;
  after: string;
}
export interface Setting {
  key: string;
  name: string;
  description: string;
  category: string;
  value: string;
  type: string;
  risk: string;
  entityId: string;
  min: number | null;
  max: number | null;
  step: number;
  options: string[];
  autoAllowed: boolean;
  autoMinimum: number | null;
  autoMaximum: number | null;
  autoMaxStep: number;
  autoCooldownHours: number;
  editable: boolean;
  documentation: string;
  // Settings catalogue. Optional so older fixtures still type-check.
  /** tunable | override | software | control | debug. Only tunable settings can be edited, undone or restored through Joule. */
  kind?: SettingKind;
  /** Catalogue section, e.g. "Battery" or "Car & Octopus". */
  section?: string;
  /** Predbat's default value, when known. */
  default?: string | null;
  unit?: string;
  /** One of the settings people most often tune: shown first. */
  commonlyTuned?: boolean;
  /** Automatic changes can be allowed (a low-risk numeric tunable). */
  autoEligible?: boolean;
  /** Predbat's own name for the entity, when it differs from Joule's. */
  predbatName?: string | null;
  expertOnly?: boolean;
  /** Cited documentation section, e.g. "customisation.md#battery-loss-options". */
  documentationAnchor?: string | null;
}
export type SettingKind = "tunable" | "override" | "software" | "control" | "debug";
/** A change to one of Predbat's own controls (manual override, software update, mode), shown on the Changes timeline. */
export interface SettingEvent {
  id: string;
  at: string;
  kind: SettingKind | string;
  key: string;
  name: string;
  before: string;
  after: string;
  /** Plain sentence, e.g. "Predbat updated to v9.3.5". */
  title: string;
  /** Set when the change was undone within a day (for example a manual charge cleared 20 minutes later). */
  revertedAt: string | null;
}
export interface Revision {
  id: number;
  at: string;
  source: string;
  reason: string;
  changes: Change[];
  values: Record<string, string>;
  reverts: number | null;
  fileVersionBefore: string | null;
  fileVersionAfter: string | null;
}
/** One message in the conversation under a recommendation: the user's note, the AI's answer, or a server notice. */
/** answer: the AI answered a question (stays open). unavailable: the AI couldn't read the reply; nothing was dismissed. reopened: an
 * old wrongful dismissal was undone. */
export type ReplyVerdict = "accept" | "answer" | "disagree" | "clarify" | "unavailable" | "reopened";
export interface ReplyMessage {
  at: string;
  role: "user" | "ai" | "system";
  text: string;
  verdict?: ReplyVerdict | null;
  provider?: string | null;
  /** The fact stored in shared memory because of this answer. */
  memory?: string | null;
  /** A fact the user can save with one click (memory was full). */
  suggestedMemory?: string | null;
  /** What the AI says the user still needs to do; such an item is never auto-dismissed. */
  action?: string | null;
  /** A configuration edit the AI drafted with this answer (on the same investigation's fileChanges). */
  fileChangeId?: string | null;
}
export interface ReplyOutcome {
  verdict: ReplyVerdict;
  reply: string;
  retired: boolean;
  memory: string | null;
  suggestedMemory: string | null;
  notice: string | null;
  provider: string;
  thread: ReplyMessage[];
  /** True when closing this item left nothing open from its check, so the check closed too. */
  findingClosed?: boolean;
}
/** A Predbat configuration file edit the user reviews and applies by hand; the app never writes the file. */
export type ConfigFileChangeStatus = "pending" | "applied" | "verified" | "dismissed" | "retired";
export interface ConfigFileChange {
  id: string;
  status: ConfigFileChangeStatus;
  file: string;
  summary: string;
  location: string;
  snippet: string;
  before?: string | null;
  reason: string;
  appliedAt?: string | null;
  closedAt?: string | null;
  closedReason?: string | null;
  decisionNote?: string | null;
  decidedAt?: string | null;
  thread?: ReplyMessage[];
}
export interface Proposal {
  id: string;
  title: string;
  summary: string;
  expectedEffect: string;
  tradeoff: string;
  confidence: string;
  status: string;
  source: string;
  investigationId: string;
  estimatedMonthlySavingGbp: number | null;
  baseRevision: number;
  reviewDays: number;
  changes: Change[];
  evidence: string[];
  createdAt: string;
  documentationReferences: { settingKey: string; referenceId: string }[];
  evidenceReferences: string[];
  decisionNote?: string | null;
  decidedAt?: string | null;
  /** "Not needed", or "Findings dismissed by user" when it closed with its check's findings. */
  closedReason?: string | null;
  thread?: ReplyMessage[];
  // Insights: fields the server already sends.
  /** A £/month range when one could honestly be estimated (currently always null). */
  savingLowGbpPerMonth?: number | null;
  savingHighGbpPerMonth?: number | null;
  /** "Not estimated: …", or how the range was worked out. */
  savingEstimate?: string | null;
  /** For calibration suggestions: the measured quantity over recent comparable windows next to what Predbat assumes. */
  calibration?: CalibrationSeries | null;
  /** Keys whose current value no longer matches the change's "before": the suggestion is stale only when this is non-empty. */
  staleKeys?: string[];
  /** Open trials that applying this would confound (information only). */
  confoundsExperimentIds?: string[];
}
export interface CalibrationSeries {
  quantity: string;
  unit: string;
  assumed: number | null;
  points: { at: string; value: number }[];
  note: string;
}
/** finding: neutral (the AI gave no verdict and the server couldn't tell). Checks that didn't finish have no verdict (null). */
export type InvestigationVerdict = "problem" | "opportunity" | "no_change" | "finding";
export type InvestigationNextStepStatus = "open" | "closed";
export type FindingClosedReason = "dismissed" | "not_needed" | "resolved" | "repeat" | "own_traffic";
export interface InvestigationNextStep {
  /** Absent on records written before follow-ups could be dismissed. */
  id?: string;
  title: string;
  rationale: string;
  suggestedAction: string;
  verification: string;
  uncertainty: string;
  evidenceReferences: string[];
  /** Absent on old records; treat as "open". */
  status?: InvestigationNextStepStatus;
  closedAt?: string | null;
  closedReason?: string | null;
  decisionNote?: string | null;
  decidedAt?: string | null;
  thread?: ReplyMessage[];
}
export interface Investigation {
  id: string;
  at: string;
  title: string;
  summary: string;
  category: string;
  confidence: string;
  evidence: string[];
  steps: string[];
  provider: string;
  request: {
    question: string | null;
    from: string | null;
    to: string | null;
    scheduled?: boolean;
    /** Plain description of an automatic check ("Automatic check of 05:16–08:20: …"), shown instead of the prompt. */
    label?: string | null;
    /** The event that started an automatic check ("the 3-hour heartbeat"). */
    trigger?: string | null;
    /** Set when this run continues one that didn't finish. */
    resumeOf?: string | null;
  };
  status: string;
  toolEvidence: ToolEvidence[];
  evidenceReferences: string[];
  nextSteps?: InvestigationNextStep[];
  /** Null for checks that didn't finish (status Failed or Interrupted) and while Running; absent on old records (show "Finding"). */
  verdict?: InvestigationVerdict | null;
  fileChanges?: ConfigFileChange[];
  /** Set when the findings closed as a whole. */
  dismissedAt?: string | null;
  decisionNote?: string | null;
  /** How they closed: dismissed (also older records with no reason), not_needed, resolved (you closed the last thing from
   * them), repeat (the same finding you closed recently) or own_traffic (about Joule's own connection to Predbat). */
  closedReason?: FindingClosedReason | null;
  thread?: ReplyMessage[];
  // AI quality (stream D): plain fields, failure details, repeats and labelled steps.
  /** At most 80 characters of plain English for list rows. */
  headline?: string | null;
  /** At most 280 characters: what happened and what to do. */
  plain?: string | null;
  /** provider_busy, timeout, rate_limited, usage_limit, sign_in, rejected, incomplete, content_filter, invalid_answer, setup, stopped, restart. */
  failureKind?: string | null;
  providerCode?: string | null;
  providerReference?: string | null;
  /** When Joule tries again automatically (failed and interrupted checks). */
  nextTryAt?: string | null;
  /** Measured net grid cost of the cited half-hours minus what the plan expected, in pence (positive = cost more). */
  impactPence?: number | null;
  /** action or info. */
  severity?: string | null;
  fingerprint?: string | null;
  /** Set on a later check that found the same thing again; the earlier one carries occurrences. */
  repeatOf?: string | null;
  occurrences?: number;
  lastSeenAt?: string | null;
  /** Things Joule's next check will measure itself (instead of to-dos). */
  watching?: string[];
  /** Each step in words (label) with the raw step (detail) for a technical view. */
  stepDetails?: { at: string; kind: string; label: string; detail: string; evidenceId?: string | null }[];
  updatedAt?: string | null;
  finishedAt?: string | null;
  /** How many times the check has been tried (a resume adds one). */
  attempts?: number;
}
/** A crucial fact every investigation reads, such as "There is a 10 kW heat pump". */
export interface MemoryFact {
  id: string;
  text: string;
  /** user-reply: accepted by the AI from your reply to a recommendation. */
  source: "user" | "user-reply" | "model";
  createdAt: string;
  investigationId: string | null;
}
export interface Experiment {
  id: string;
  title: string;
  hypothesis: string;
  status: string;
  revisionId: number;
  startedAt: string;
  reviewAt: string;
  result: string;
  baselineError: number | null;
  currentError: number | null;
  automaticRollback: boolean;
  source: string;
  decisions: { at: string; decision: string; notes: string; extendedDays: number | null }[];
  baselineCostGbpPerDay: number | null;
  currentCostGbpPerDay: number | null;
  baselineCostCoverage: number;
  currentCostCoverage: number;
  confounders: string[];
  revertEligible: boolean;
  automaticRevertEligible: boolean;
  revertReason: string;
  financialMethod: string;
  forecastMethod?: string;
}
export interface Slot {
  durationMinutes: number;
  time: string;
  loadForecast: number;
  loadActual: number | null;
  /** How loadActual was obtained: "measured" (meter intervals exactly cover the slot), "boundary" (straddling intervals prorated to the slot edges) or null when no actual exists. */
  loadActualMethod?: "measured" | "boundary" | null;
  pvForecast: number;
  pvActual: number | null;
  pvActualMethod?: "measured" | "boundary" | null;
  socForecast: number;
  socActual: number | null;
  importRate: number;
  exportRate: number;
  action: string;
  cost: number;
}
export interface Plan {
  id: string;
  at: string;
  collectedAt?: string | null;
  source: string;
  slots: Slot[];
}
export interface Preferences {
  provider: string;
  model: string;
  scheduled: boolean;
  intervalMinutes: number;
  maxRunsPerDay: number;
  inputUsdPerMillion: number;
  outputUsdPerMillion: number;
}
export interface Usage {
  at: string;
  provider: string;
  model: string;
  inputTokens: number;
  outputTokens: number;
  estimatedUsd: number | null;
  status: string;
  cachedInputTokens?: number;
}
export interface State {
  dataSource: string;
  mode: string;
  revision: number;
  settings: Setting[];
  revisions: Revision[];
  proposals: Proposal[];
  experiments: Experiment[];
  investigations: Investigation[];
  activities: { at: string; kind: string; message: string }[];
  usage: Usage[];
  ai: Preferences;
  lastCollection: string | null;
  lastAnalysis: string | null;
  collectionError: string | null;
  analysisError: string | null;
  writeUncertain: boolean;
  pendingFileReload: boolean;
  lastFileVersionId: string | null;
  reports: EnergyReport[];
  notifications: Notification[];
  /** The bell's notifications, server-side so read and dismissed stick (NotificationInbox.cs). Absent on older servers. */
  inbox?: InboxItem[];
  reportPreferences: ReportPreferences;
  /** Changes to Predbat's own controls, oldest first. */
  settingEvents?: SettingEvent[];
  /** Things the AI said it will test at the next check, and how they turned out. */
  claims?: AiClaim[];
  /** max_savings, limit_cycling or self_sufficiency. */
  householdObjective?: string;
}
/** A claim an AI check made that a later check can confirm or refute. */
export interface AiClaim {
  id: string;
  text: string;
  test: string | null;
  /** open, confirmed or refuted. */
  status: string;
  createdAt: string;
  investigationId: string | null;
  resolvedAt: string | null;
  resolvedBy: string | null;
  reason: string | null;
}
/** What the last check learned about Predbat's MCP tools. */
export interface McpDiscoveryRecord {
  at: string;
  configured: boolean;
  connected: boolean;
  toolCount: number;
  tools: string[];
  error: string | null;
  source: string;
}
export interface InvestigationSchedule {
  enabled: boolean;
  state: string;
  reason: string;
  nextRunAt: string | null;
  lastAttemptAt: string | null;
  lastCompletedAt: string | null;
  runsToday: number;
  maxRunsPerDay: number;
  intervalMinutes: number;
  // Event-led scheduling.
  /** The event the next check is for ("the 3-hour heartbeat", "the cheap window that ended at 05:30"). */
  trigger?: string | null;
  triggerKind?: string | null;
  resumeId?: string | null;
  /** The next quiet check (no AI) that looks for anything new. */
  nextCheckAt?: string | null;
  lastQuietCheckAt?: string | null;
  /** "Checked 15:40–16:41: nothing new. …" */
  lastQuietCheck?: string | null;
  failedToday?: number;
  lastRunRecordedAt?: string | null;
}
export interface ReportSchedule {
  kind: string;
  enabled: boolean;
  state: string;
  reason: string;
  nextRunAt: string | null;
  lastGeneratedAt: string | null;
  from: string | null;
  to: string | null;
}
export interface ReportSchedules {
  timeZone: string;
  hourLocal: number;
  daily: ReportSchedule;
  weekly: ReportSchedule;
}
export interface Payload {
  reportSchedules?: ReportSchedules;
  state: State;
  memory?: MemoryFact[];
  plan: Plan | null;
  /** @deprecated No longer sent by /api/state; page through GET /api/plans instead. */
  plans?: { id: string; at: string; source: string }[];
  connection: {
    demo: boolean;
    writesEnabled: boolean;
    predbatConfigured: boolean;
    /** "None" means Joule itself has no sign-in (a proxy must protect it). Added by the platform API. */
    authMode?: "AccessKey" | "None";
  };
  ai: {
    schedule?: InvestigationSchedule;
    running: boolean;
    apiConfigured: boolean;
    chatGptConnected: boolean;
    chatGptLocalSignInAvailable: boolean;
    chatGptEmail: string | null;
    /** From the last check that talked to Predbat's MCP server. */
    mcp?: McpDiscoveryRecord | null;
    objective?: string | null;
  };
}

// ---------------------------------------------------------------------------
// Platform API contract. Additive types for the slim state header and the paged history endpoints.
// Server source: src/Joule.Api/StateProjection.cs and StateEndpoints.cs. JSON schema: docs/api/state-header.schema.json.
// StateHeaderContractTests keeps the StateHeader property list below in step with the schema; add fields at the end only.
//
//   GET /api/state?view=header   -> StateHeader   (poll this; ETag + 304 when unchanged)
//   GET /api/state[?full=1]      -> Payload       (legacy full payload, kept until every page reads the header)
//   GET /api/settings            -> SettingsCatalogue (full settings with option lists; ETag)
//   GET /api/investigations?cursor=&limit=20&verdict=&status= -> InvestigationPage (newest first, includes archived)
//   GET /api/investigations/{id} -> Investigation (full record; also finds archived ones)
//   GET /api/activities?since=ISO | ?before=ISO&limit=50&kind= -> ActivityPage (oldest first)
//   GET /api/usage?days=30       -> UsageReport (daily totals in the household timezone)
//   GET /api/health              -> Health (no access key needed)
// ---------------------------------------------------------------------------

/** An investigation as a list row. headline/plain fall back to title/summary on records written before they existed. */
export interface InvestigationSummary {
  id: string;
  at: string;
  /** Completed, Failed, Interrupted, Running... Present failed/interrupted runs as "Didn't finish", never as a problem. */
  status: string;
  /** problem | opportunity | no_change. Legacy records may say "problem" for runs that failed; check status first. */
  verdict: InvestigationVerdict | string;
  title: string;
  headline: string;
  /** Plain-English summary, at most about 280 characters. */
  plain: string | null;
  category: string;
  confidence: string;
  provider: string;
  scheduled: boolean;
  dismissedAt: string | null;
  openFollowUps: number;
  openFileChanges: number;
  pendingProposals: number;
  /** Estimated money at stake in pence, when the investigation recorded one. */
  impactPence: number | null;
}
export interface RunStripEntry {
  id: string;
  at: string;
  status: string;
  verdict: InvestigationVerdict | string;
}
/** A setting without its option list or long description; fetch GET /api/settings for those. */
export interface SettingIndexEntry {
  key: string;
  name: string;
  category: string;
  value: string;
  type: string;
  risk: string;
  editable: boolean;
  autoAllowed: boolean;
  entityId: string;
}
export interface HeaderCounts {
  /** Every investigation, including archived ones. */
  investigations: number;
  archivedInvestigations: number;
  pendingProposals: number;
  openFollowUps: number;
  openFileChanges: number;
  openExperiments: number;
  unreadNotifications: number;
  unreadReports: number;
  /** Activities still in the live state (older ones are paged through /api/activities). */
  activities: number;
}
export interface HomeAssistantHealth {
  configured: boolean;
  lastCollection: string | null;
  error: string | null;
  /** Mapped meters whose latest reading is neither a normal reading nor an expected idle (offline, stale, invalid). */
  unexpected: string[];
  missingMappings: string[];
}
export type WritesStatus = "demo" | "enabled" | "disabled" | "blocked";
export type AiStatus = "running" | "needs_setup" | "last_run_failed" | "off" | "ready";
/** Timestamps, not ages: derive "5 min ago" in the browser so an unchanged system keeps its ETag. */
export interface HeaderHealth {
  predbatLastAt: string | null;
  collectionError: string | null;
  analysisError: string | null;
  writeUncertain: boolean;
  pendingFileReload: boolean;
  writes: WritesStatus;
  aiStatus: AiStatus;
  /** Null in the legacy payload or when Home Assistant status could not be read. */
  homeAssistant: HomeAssistantHealth | null;
}
export interface PredbatStatusHeader {
  /** Predbat's running version, e.g. "9.3.5" (from select.predbat_update); null when unknown. */
  version: string | null;
  /** Predbat's own mode (select.predbat_mode), not Joule's control mode. */
  mode: string | null;
  /** The plan's action for the slot covering now. */
  currentAction: string | null;
  /** Minimum battery reserve % Predbat keeps (set_reserve_min); null when unknown. */
  reserve: number | null;
  planAt: string | null;
  planCollectedAt: string | null;
}
export interface SetupStep {
  key: "predbat" | "collecting" | "meters" | "ai" | "mcp" | "reviews" | "writes" | string;
  label: string;
  done: boolean;
  required: boolean;
}
export interface SetupProgress {
  done: number;
  total: number;
  requiredDone: boolean;
  steps: SetupStep[];
}
export interface ActivityEntry {
  at: string;
  kind: string;
  message: string;
}
export interface HeaderConnection {
  demo: boolean;
  writesEnabled: boolean;
  predbatConfigured: boolean;
  authMode: "AccessKey" | "None";
}
export interface StateHeader {
  schemaVersion: 1;
  /** Joule version, as in /api/health. */
  version: string;
  dataSource: "Demo" | "Live" | string;
  /** Joule's control mode: Monitor, Recommend or Auto. */
  mode: string;
  revision: number;
  lastCollection: string | null;
  lastAnalysis: string | null;
  lastAnalysisAttemptAt: string | null;
  connection: HeaderConnection;
  ai: {
    running: boolean;
    schedule: InvestigationSchedule;
    apiConfigured: boolean;
    chatGptConnected: boolean;
    /** Masked for display, e.g. "jo.…@example.com". */
    chatGptEmail: string | null;
    chatGptLocalSignInAvailable: boolean;
    preferences: Preferences;
  };
  health: HeaderHealth;
  predbatStatus: PredbatStatusHeader;
  counts: HeaderCounts;
  plan: Plan | null;
  /** Newest five, newest first. */
  latestInvestigations: InvestigationSummary[];
  /** The completed problem/opportunity in the last 48 hours with the most money at stake, not dismissed. */
  topFinding48h: InvestigationSummary | null;
  /** Today's runs (household timezone), oldest first. */
  todayRunStrip: RunStripEntry[];
  /** Pending proposals only, newest first. */
  proposals: Proposal[];
  /** Unread notifications only, newest first, at most 20. */
  notifications: Notification[];
  settings: SettingIndexEntry[];
  memory: MemoryFact[];
  reportSchedules: ReportSchedules;
  reportPreferences: ReportPreferences;
  setupProgress: SetupProgress;
  lastActivity: ActivityEntry | null;
  /** Household timezone (IANA id) used for "today". */
  timeZone: string;
}
export interface InvestigationListItem extends InvestigationSummary {
  summary: string;
  question: string | null;
  /** True when the record has moved to the long-term archive (still fully readable). */
  archived: boolean;
}
export interface InvestigationPage {
  items: InvestigationListItem[];
  /** Pass back as ?cursor= for the next (older) page; null on the last page. */
  nextCursor: string | null;
  /** All investigations, or -1 when a verdict/status filter is applied. */
  total: number;
}
export interface ActivityPage {
  items: ActivityEntry[];
  oldest: string | null;
  newest: string | null;
  more: boolean;
}
export interface UsageDay {
  date: string;
  runs: number;
  failed: number;
  inputTokens: number;
  outputTokens: number;
  estimatedUsd: number | null;
}
export interface UsageReport {
  from: string;
  to: string;
  timeZone: string;
  runs: number;
  failed: number;
  inputTokens: number;
  outputTokens: number;
  /** Null when no run had a price; see costComplete. */
  estimatedUsd: number | null;
  costComplete: boolean;
  daily: UsageDay[];
  records: Usage[];
}
export interface SettingsCatalogue {
  revision: number;
  settings: Setting[];
}
export interface Health {
  status: "ok";
  version: string;
  build: string | null;
}

/**
 * One notification in the bell (server: src/Joule.Api/Notifications/NotificationInbox.cs). Made once per thing (key) and never
 * re-created, so reading or dismissing it sticks. `open` is false once the thing is handled (approved, turned down, back online):
 * only open, unread, undismissed items count in the badge.
 */
export interface InboxItem {
  id: string;
  key: string;
  /** needs_you, problem, unfinished, offline or report. */
  event: string;
  /** The chip: Suggestion, To-do, File edit, Trial to decide, Found something, Didn't finish, Offline, Report. */
  label: string;
  /** May hold AI or server text: render through <PlainText>. */
  title: string;
  detail: string | null;
  /** A hash route, e.g. "#/insights/inv/abc". */
  link: string;
  tone: "accent" | "warn";
  at: string;
  readAt: string | null;
  dismissedAt: string | null;
  resolvedAt: string | null;
  open: boolean;
}
