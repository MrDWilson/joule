import { lazy, Suspense, useCallback, useEffect, useMemo, useRef, useState, type ComponentType } from "react";
import { ArrowRight, Clock, LoaderCircle, LogIn, RefreshCw, Settings2, Square } from "lucide-react";
import { BrandMark } from "./components/BrandMark";
import { Button, ButtonLink } from "./components/ui/Button";
import { ErrorBoundary } from "./components/ui/ErrorBoundary";
import { LoadingBlock } from "./components/ui/States";
import { needsYou } from "./lib/insights";
import { ReplyOutcomeHost } from "./components/RecommendationReply";
import { Sidebar, SubNav, TabBar } from "./components/shell/Navigation";
import { StatusChip } from "./components/shell/StatusChip";
import { NotificationsBell } from "./components/shell/NotificationsBell";
import { AlertRows, type AlertItem } from "./components/shell/AlertRows";
import { ToastStack, useToasts } from "./components/shell/Toasts";
import { FirstRunNotice, SiteFooter } from "./components/shell/SiteFooter";
import { DemoTour } from "./components/setup/DemoTour";
import { checklistCounts, useSetupAutoOpen, useSetupProgress } from "./lib/setupApi";
import type { EnergySummary, TelemetryStatus, ObservedMeterTrends } from "./completion-types";
import type { Payload, Proposal, Slot } from "./types";
import { AppContext, type AppContextValue } from "./context/AppContext";
import { todaySoFar, type EarlierPeriod } from "./lib/comparison";
import {
  AccessKeyRequiredError,
  storeAccessKey,
  storedAccessKey,
  ApiError,
  api as callApi,
  pollGet,
  POLL_TIMEOUT_MS,
  request,
} from "./lib/api";
import { isGatewayError, isOffline, plainError, SessionExpiredError } from "./lib/errors";
import { health as healthOf } from "./lib/health";
import { usePoll } from "./lib/poll";
import {
  buildHash,
  canonicalise,
  destinations,
  navigate,
  routeTitle,
  toHash,
  useRoute,
  type Destination,
} from "./lib/router";
import { clock, dayLabel, dayTime, localDate, setHouseholdTimeZone } from "./lib/time";
import { words } from "./lib/copy";
import OverviewPage from "./pages/OverviewPage";
import { ProposalReviewDialog } from "./pages/ProposalReviewDialog";

// Today loads with the app; every other destination is fetched the first time it is opened.
const PlanPage = lazy(() => import("./pages/PlanPage"));
const InsightsPage = lazy(() => import("./pages/InsightsPage"));
const EnergyPage = lazy(() => import("./pages/EnergyPage"));
const SetupPage = lazy(() => import("./pages/SetupPage"));
const KitPage = lazy(() => import("./pages/KitPage"));
const pageFor: Record<Destination, ComponentType> = {
  today: OverviewPage,
  plan: PlanPage,
  insights: InsightsPage,
  energy: EnergyPage,
  setup: SetupPage,
  kit: KitPage,
};

const BUILD_VERSION = typeof __JOULE_VERSION__ === "string" ? __JOULE_VERSION__ : "dev";
const STATE_INTERVAL = 10_000,
  RUNNING_INTERVAL = 2_500,
  TELEMETRY_INTERVAL = 10_000,
  /** Measured figures are refetched when a new reading arrives, and at least this often. */
  MEASURED_MAX_AGE = 5 * 60_000,
  /** Polls in a row that must fail before the "Can't reach Joule" banner appears. */
  OFFLINE_AFTER = 2;

export default function App() {
  const route = useRoute();
  const browserIsLoopback = ["127.0.0.1", "localhost", "[::1]"].includes(window.location.hostname);
  const toasts = useToasts();

  // ---------------------------------------------------------------- server state (/api/state)
  const [data, setData] = useState<Payload | null>(null),
    [fetchedAt, setFetchedAt] = useState<number | null>(null),
    [failures, setFailures] = useState(0),
    [lastTried, setLastTried] = useState<number | null>(null),
    [connectionError, setConnectionError] = useState(""),
    // A gateway (or Joule itself) answered with a server error: Joule is restarting or down, not unreachable.
    [notAnswering, setNotAnswering] = useState(false),
    [sessionExpired, setSessionExpired] = useState(""),
    [needsKey, setNeedsKey] = useState(false),
    [keyRejected, setKeyRejected] = useState(false),
    [access, setAccess] = useState(""),
    [busy, setBusy] = useState(false),
    [refreshing, setRefreshing] = useState(false),
    [proposal, setProposal] = useState<Proposal | null>(null),
    [pendingFocus, setPendingFocus] = useState("");
  // Read synchronously by go(): callers select an investigation and navigate in the same click.
  const pendingFocusRef = useRef("");
  const etag = useRef<string | null>(null);
  // Responses are applied in order: one that started before the response on screen can never replace it.
  const sequence = useRef({ requested: 0, applied: 0 });
  const running = useRef(false);
  running.current = !!data?.ai.running;
  const failuresRef = useRef(0);

  const loadState = useCallback(async (signal?: AbortSignal) => {
    const n = ++sequence.current.requested;
    setLastTried(Date.now());
    try {
      const r = await request<Payload>("/state", { signal, etag: etag.current, timeoutMs: POLL_TIMEOUT_MS });
      if (n < sequence.current.applied) return;
      sequence.current.applied = n;
      if (!r.notModified) {
        setData(r.data);
        etag.current = r.etag;
      }
      setFetchedAt(Date.now());
      failuresRef.current = 0;
      setFailures(0);
      setConnectionError("");
      setNotAnswering(false);
      setNeedsKey(false);
      setSessionExpired("");
    } catch (e) {
      if (signal?.aborted || n < sequence.current.applied) return;
      if (e instanceof AccessKeyRequiredError) {
        setNeedsKey(true);
        // Only a key that the server has actually turned down counts as rejected (not one still being checked).
        setKeyRejected(!!storedAccessKey());
        return;
      }
      if (e instanceof SessionExpiredError) {
        setSessionExpired(e.message);
        return;
      }
      if (isOffline(e)) setFailures(++failuresRef.current);
      setConnectionError(plainError(e));
      setNotAnswering(isGatewayError(e) || (e instanceof ApiError && e.status >= 500));
    }
  }, []);
  const load = useCallback(() => loadState(), [loadState]);
  const pollState = usePoll(loadState, {
    // After a miss, check again soon (so a real outage is reported quickly), then back off while it lasts.
    interval: () =>
      failuresRef.current === 0
        ? running.current
          ? RUNNING_INTERVAL
          : STATE_INTERVAL
        : failuresRef.current < OFFLINE_AFTER
          ? 2000
          : Math.min(30_000, 5000 * 2 ** (failuresRef.current - OFFLINE_AFTER)),
    enabled: !needsKey && !sessionExpired,
  });
  const offline = failures >= OFFLINE_AFTER;

  // ---------------------------------------------------------------- measured figures (/api/telemetry/*)
  const [telemetry, setTelemetry] = useState<TelemetryStatus | null>(null),
    [energyTimeZone, setEnergyTimeZone] = useState("Europe/London"),
    [daily, setDaily] = useState<EnergySummary | null>(null),
    [yesterday, setYesterday] = useState<{ summary: EnergySummary; earlier: EarlierPeriod } | null>(null),
    [recent, setRecent] = useState<Slot[]>([]),
    [meterTrends, setMeterTrends] = useState<ObservedMeterTrends | null>(null),
    [telemetryError, setTelemetryError] = useState(""),
    [trendError, setTrendError] = useState(""),
    [measuredAt, setMeasuredAt] = useState<number | null>(null);
  const measuredKey = useRef(""),
    measuredFetched = useRef(0),
    forceMeasured = useRef(false),
    stateCollection = useRef<string | null>(null);
  stateCollection.current = data?.state.lastCollection ?? null;

  const loadTelemetry = useCallback(async (signal: AbortSignal) => {
    let status: TelemetryStatus;
    try {
      status = await pollGet<TelemetryStatus>("/telemetry/status", signal);
    } catch (e) {
      // Keep the last good figures; an unreachable server is reported once, by the offline banner.
      if (!signal.aborted) setTelemetryError(isOffline(e) ? "" : plainError(e));
      return;
    }
    setTelemetry(status);
    setHouseholdTimeZone(status.timeZone);
    setEnergyTimeZone(status.timeZone);
    const now = new Date();
    const key = `${status.lastCollection}|${stateCollection.current}|${localDate(now, { timeZone: status.timeZone })}`;
    // The figures only change when a reading arrives: skip the heavier requests until then.
    if (
      !forceMeasured.current &&
      key === measuredKey.current &&
      Date.now() - measuredFetched.current < MEASURED_MAX_AGE
    ) {
      setTelemetryError("");
      return;
    }
    forceMeasured.current = false;
    let ok = true;
    try {
      const { params, earlier } = todaySoFar(now, status.timeZone);
      const [summary, before] = await Promise.all([
        pollGet<EnergySummary>(`/telemetry/summary?${params}`, signal),
        // Yesterday is optional: a failure only hides the comparison lines.
        earlier
          ? pollGet<EnergySummary>(`/telemetry/summary?${earlier.params}`, signal)
              .then((summary) => ({ summary, earlier }))
              .catch(() => null)
          : Promise.resolve(null),
      ]);
      if (signal.aborted) return;
      setDaily(summary);
      setYesterday(before);
      setTelemetryError("");
    } catch (e) {
      ok = false;
      if (!signal.aborted) setTelemetryError(isOffline(e) ? "" : plainError(e));
    }
    if (signal.aborted) return;
    // Measured history for the charts is independent of the summary: a failure here leaves only the plan.
    await Promise.allSettled([
      (async () => {
        try {
          const timeline = await pollGet<{ slots: Slot[] }>("/plans/timeline?hours=24", signal);
          if (!signal.aborted) setRecent(timeline.slots);
        } catch {
          // Keep the previous slots.
        }
      })(),
      (async () => {
        try {
          const end = new Date(),
            start = new Date(end.getTime() - 86400000);
          const params = new URLSearchParams({ from: start.toISOString(), to: end.toISOString() });
          const trends = await pollGet<ObservedMeterTrends>(`/telemetry/trends?${params}`, signal);
          if (signal.aborted) return;
          setMeterTrends(trends);
          setTrendError("");
        } catch (e) {
          ok = false;
          if (!signal.aborted) setTrendError(isOffline(e) ? "" : plainError(e));
        }
      })(),
    ]);
    if (signal.aborted) return;
    if (ok) {
      measuredKey.current = key;
      measuredFetched.current = Date.now();
      setMeasuredAt(Date.now());
    }
  }, []);
  const pollTelemetry = usePoll(loadTelemetry, {
    interval: () => TELEMETRY_INTERVAL,
    enabled: !!data && !needsKey && !sessionExpired,
  });
  const retryMeasured = useCallback(() => {
    forceMeasured.current = true;
    pollTelemetry();
  }, [pollTelemetry]);
  // A new Predbat collection can bring new plan slots: refresh the measured history with it.
  // The first value arrives with the first state, when the telemetry poll is starting anyway: skip that one.
  const seenCollection = useRef<string | null | undefined>(undefined);
  useEffect(() => {
    const c = data?.state.lastCollection;
    if (!c || c === seenCollection.current) return;
    const first = seenCollection.current === undefined;
    seenCollection.current = c;
    if (!first) pollTelemetry();
  }, [data?.state.lastCollection, pollTelemetry]);

  // ---------------------------------------------------------------- versions
  const [server, setServer] = useState<{ version?: string; predbatVersion?: string }>({});
  useEffect(() => {
    callApi<{ version?: string; predbatVersion?: string }>("/health")
      .then((h) => setServer(h ?? {}))
      .catch(() => {});
  }, []);

  // ---------------------------------------------------------------- setup progress (live installs only)
  const setupProgress = useSetupProgress(data?.connection.demo ?? true, [
    data?.state.lastCollection,
    data?.state.revision,
    data?.connection.predbatConfigured,
    data?.state.ai.provider,
    data?.ai.chatGptConnected,
  ]);
  const openSetup = useCallback(() => navigate("#/setup", { replace: true }), []);
  useSetupAutoOpen(setupProgress, data?.connection.demo, openSetup);

  // ---------------------------------------------------------------- actions
  const mutate = useCallback(
    async (path: string, body: unknown, message: string) => {
      setBusy(true);
      try {
        await callApi(path, body);
        await loadState();
        toasts.push("success", message);
        return true;
      } catch (e) {
        if (e instanceof SessionExpiredError) setSessionExpired(e.message);
        else toasts.push("error", plainError(e));
        return false;
      } finally {
        setBusy(false);
      }
    },
    [loadState, toasts.push],
  );
  /** One refresh for everything: Predbat's plan and settings, and the Home Assistant sensors. */
  const refreshNow = useCallback(async () => {
    setRefreshing(true);
    const [predbat, sensors] = await Promise.allSettled([callApi("/collect", {}), callApi("/telemetry/collect", {})]);
    await loadState();
    forceMeasured.current = true;
    pollTelemetry();
    setRefreshing(false);
    const failed = (r: PromiseSettledResult<unknown>) => (r.status === "rejected" ? plainError(r.reason) : "");
    const p = failed(predbat),
      h = failed(sensors);
    if (!p && !h) toasts.push("success", "Updated from Predbat and Home Assistant.");
    else if (p && h) toasts.push("error", `Couldn't refresh. Predbat: ${p} Home Assistant: ${h}`);
    else if (p) toasts.push("success", `Sensor readings updated · couldn't reach Predbat: ${p}`);
    else toasts.push("success", `Plan updated · couldn't read Home Assistant: ${h}`);
  }, [loadState, pollTelemetry, toasts.push]);

  const go = useCallback((target: string) => {
    let hash = toHash(target);
    const focus = pendingFocusRef.current;
    if (hash === "#/insights" && focus) hash = buildHash("insights", "", { id: focus });
    pendingFocusRef.current = "";
    setPendingFocus("");
    navigate(hash);
  }, []);
  const focusedInvestigation = route.destination === "insights" ? (route.params.id ?? "") : pendingFocus;
  const setFocusedInvestigation = useCallback(
    (id: string) => {
      // Remembered for a go("Investigations") in the same click; on Insights the address changes straight away.
      pendingFocusRef.current = id;
      if (route.destination === "insights")
        navigate(id ? buildHash("insights", "", { id }) : buildHash("insights"), { replace: true });
      else setPendingFocus(id);
    },
    [route.destination],
  );
  const reportError = useCallback((message: string) => toasts.push("error", plainError(message)), [toasts.push]);
  const notify = useCallback(
    (message: string, action?: { label: string; onClick: () => void }) => toasts.push("success", message, action),
    [toasts.push],
  );

  // ---------------------------------------------------------------- route effects
  const heading = useRef<HTMLHeadingElement>(null);
  const mainRef = useRef<HTMLElement>(null);
  const [announcement, setAnnouncement] = useState("");
  const title = routeTitle(route);
  const place = `${route.destination}/${route.section}`;
  const firstPlace = useRef(true);
  // Old names and partial addresses (#/history, #/) are rewritten to their canonical form without a new history entry.
  useEffect(() => {
    canonicalise();
  }, [route.hash]);
  useEffect(() => {
    document.title = `${title} · Joule`;
  }, [title]);
  useEffect(() => {
    // Moving to another page: start at the top, move focus to its title and announce it (not on the first load).
    if (firstPlace.current) {
      firstPlace.current = false;
      return;
    }
    window.scrollTo({ top: 0 });
    heading.current?.focus({ preventScroll: true });
    setAnnouncement(`${title} page`);
  }, [place]);

  const measured = useMemo(
    () => ({
      telemetry,
      daily,
      yesterday,
      recent,
      meterTrends,
      trendError: offline ? "" : trendError,
      telemetryError: offline ? "" : telemetryError,
      fetchedAt: measuredAt,
      retry: retryMeasured,
    }),
    [telemetry, daily, yesterday, recent, meterTrends, trendError, telemetryError, measuredAt, retryMeasured, offline],
  );
  const health = useMemo(
    () => (data ? healthOf({ data, telemetry, offline, fetchedAt }) : null),
    [data, telemetry, offline, fetchedAt],
  );
  const about = useMemo(
    () => ({ version: server.version ?? BUILD_VERSION, predbatVersion: server.predbatVersion }),
    [server],
  );
  const context = useMemo<AppContextValue | null>(
    () =>
      data && health
        ? {
            api: callApi,
            mutate,
            busy,
            setBusy,
            data,
            load,
            timeZone: energyTimeZone,
            measured,
            route,
            go,
            reportError,
            notify,
            focusedInvestigation,
            setFocusedInvestigation,
            reviewProposal: setProposal,
            health,
            offline,
            about,
          }
        : null,
    [
      data,
      health,
      mutate,
      busy,
      load,
      energyTimeZone,
      measured,
      route,
      go,
      reportError,
      notify,
      focusedInvestigation,
      setFocusedInvestigation,
      offline,
      about,
    ],
  );

  // ---------------------------------------------------------------- screens before the app
  if (sessionExpired)
    return (
      <div className="connection-screen">
        <BrandMark size={56} variant="bare" className="connection-mark" decorative />
        <h1>Sign in again</h1>
        <p role="alert">{sessionExpired}</p>
        <Button size="lg" onClick={() => window.location.reload()}>
          <LogIn size={16} aria-hidden="true" />
          Sign in again
        </Button>
      </div>
    );
  if (needsKey)
    return (
      <div className="connection-screen">
        <BrandMark size={56} variant="bare" className="connection-mark" decorative />
        <h1>Connect to Joule</h1>
        <p>This server asks for an access key. It is kept only for this browser session.</p>
        <form
          onSubmit={(e) => {
            e.preventDefault();
            storeAccessKey(access);
            setKeyRejected(false);
            void loadState();
          }}
        >
          <label htmlFor="access">Server access key</label>
          <input
            id="access"
            name="password"
            type="password"
            autoComplete="current-password"
            required
            value={access}
            onChange={(e) => setAccess(e.target.value)}
          />
          <Button size="lg">
            Connect <ArrowRight size={16} aria-hidden="true" />
          </Button>
        </form>
        {keyRejected && (
          <p role="alert" className="error-text">
            That key wasn't accepted. Check it and try again.
          </p>
        )}
      </div>
    );
  if (!data || !context || !health)
    return (
      <div className="connection-screen">
        <BrandMark size={56} variant="bare" className="connection-mark" decorative />
        <h1>Joule</h1>
        {connectionError ? (
          <>
            <p role="alert">
              {notAnswering
                ? "Joule isn't answering right now. It may be restarting; it keeps trying on its own."
                : failures > 0
                  ? `Can't reach Joule at ${window.location.host}.`
                  : connectionError}
            </p>
            <p className="muted">
              {notAnswering
                ? ""
                : failures > 0
                  ? browserIsLoopback
                    ? "Is the Joule service running? It listens on port 5080 unless you changed it."
                    : "It may be restarting. Joule keeps trying on its own."
                  : "Joule keeps trying on its own."}
              {lastTried ? `${notAnswering ? "" : " "}Last tried ${clock(lastTried)}.` : ""}
            </p>
            <Button onClick={() => pollState()}>
              <RefreshCw size={16} aria-hidden="true" />
              Try again
            </Button>
          </>
        ) : (
          <>
            <LoaderCircle className="spin" aria-hidden="true" />
            <p role="status">Connecting to Joule…</p>
          </>
        )}
      </div>
    );

  // ---------------------------------------------------------------- the app
  const s = data.state,
    c = data.connection;
  // One count of what needs you (suggestions, to-dos, file edits, trials due a decision): the Insights badge, its
  // Suggestions tab and the bell's "Needs you" row all show it.
  const insightsCount = needsYou(s).length;
  const destination = destinations[route.destination];
  const sectionInfo = destination.sections.find((x) => x.key === route.section);
  const description = sectionInfo?.description ?? destination.description;
  const dev = import.meta.env.DEV;
  const Page = route.destination === "kit" && !dev ? OverviewPage : pageFor[route.destination];

  const alerts: AlertItem[] = [];
  if (offline)
    alerts.push({
      key: "offline",
      tone: "warn",
      title: notAnswering ? "Joule isn't answering" : "Can't reach Joule",
      detail: `${notAnswering ? "It may be restarting. " : ""}${fetchedAt ? `Showing data from ${dayTime(fetchedAt)}. ` : ""}Retrying on its own.`,
      action: (
        <Button variant="secondary" size="sm" onClick={() => pollState()}>
          Try again
        </Button>
      ),
    });
  if (s.writeUncertain)
    alerts.push({
      key: "write",
      tone: "danger",
      title: "Joule isn't sure its last change reached Predbat",
      detail: "Check Predbat's settings before making another change.",
      action: (
        <Button
          variant="secondary"
          size="sm"
          disabled={busy}
          onClick={() => void mutate("/reconcile", {}, "Predbat settings re-read.")}
        >
          <RefreshCw size={14} aria-hidden="true" />
          Re-read Predbat settings
        </Button>
      ),
    });
  if (s.pendingFileReload)
    alerts.push({
      key: "files",
      tone: "danger",
      title: "Predbat's files were restored",
      detail: "Restart Predbat and check its settings before making more changes.",
      action: (
        <ButtonLink href="#/setup/files" size="sm">
          Go to Files
        </ButtonLink>
      ),
    });
  // On the setup checklist itself this alert would only repeat its first step.
  const onChecklist = route.destination === "setup" && route.section === "";
  if (!c.demo && !c.predbatConfigured && !onChecklist)
    alerts.push({
      key: "setup",
      tone: "info",
      title: "Predbat isn't connected yet",
      detail: "Tell Joule where Predbat is to see your real plan and readings.",
      action: (
        <ButtonLink href="#/setup" variant="primary" size="sm">
          Set up live data
        </ButtonLink>
      ),
    });
  else if (s.collectionError && (c.predbatConfigured || c.demo))
    alerts.push({
      key: "predbat",
      tone: "warn",
      title: s.lastCollection ? "Couldn't read from Predbat" : "Predbat isn't connected yet",
      detail: s.lastCollection
        ? `${s.collectionError.replace(/\.+$/, "")}. Showing Predbat's plan from ${dayTime(s.lastCollection)}.`
        : `${s.collectionError.replace(/\.+$/, "")}.`,
      action: (
        <Button variant="secondary" size="sm" disabled={refreshing} onClick={() => void refreshNow()}>
          Try again
        </Button>
      ),
    });
  // A check that didn't finish is news on Today and Insights only, and only until a later check completes or while
  // one is running again (the running row says so).
  const aiFailedAt = (() => {
    const failed = s.investigations
      .filter((i) => /^(failed|interrupted)$/i.test(i.status ?? ""))
      .map((i) => Date.parse(i.at))
      .filter(Number.isFinite);
    if (failed.length) return Math.max(...failed);
    const attempt = Date.parse(data.ai.schedule?.lastAttemptAt ?? "");
    return Number.isFinite(attempt) ? attempt : null;
  })();
  const lastCompleted = Date.parse(s.lastAnalysis ?? "");
  const aiFailureStale = aiFailedAt != null && Number.isFinite(lastCompleted) && lastCompleted > aiFailedAt;
  if (
    s.analysisError &&
    !aiFailureStale &&
    !data.ai.running &&
    (route.destination === "today" || route.destination === "insights")
  ) {
    const retryAt = Date.parse(data.ai.schedule?.nextRunAt ?? "");
    const now = Date.now();
    const retry =
      Number.isFinite(retryAt) && retryAt > now
        ? localDate(retryAt) === localDate(now)
          ? ` It will try again at ${clock(retryAt)}.`
          : localDate(retryAt) === localDate(now + 86400000)
            ? ` It will try again tomorrow at ${clock(retryAt)}.`
            : ` It will try again ${dayTime(retryAt)}.`
        : "";
    alerts.push({
      key: "ai",
      tone: "warn",
      title: `The last ${words.aiCheck} ${words.didntFinish.toLowerCase()}`,
      detail: `${s.analysisError.trim().replace(/\.+$/, "")}.${retry}`,
      action:
        route.destination === "insights" && route.section === "" ? undefined : (
          <ButtonLink href="#/insights" size="sm">
            Open Insights
          </ButtonLink>
        ),
    });
  }
  // A check in progress: one quiet row with Stop, except where the page shows it already (Insights' running card,
  // Setup › AI checks).
  const showRunning =
    data.ai.running &&
    !(route.destination === "insights" && route.section === "") &&
    !(route.destination === "setup" && route.section === "ai");
  const stopCheck = () => void mutate("/investigations/cancel", {}, "Stopping the check.");

  const updatedAt = fetchedAt ? `Updated ${clock(fetchedAt)}` : "";

  return (
    <AppContext.Provider value={context}>
      <a
        className="skip-link"
        href="#main"
        onClick={(e) => {
          e.preventDefault();
          mainRef.current?.focus();
        }}
      >
        Skip to main content
      </a>
      <div className="app">
        <Sidebar route={route} insightsCount={insightsCount} />
        <div className="main">
          <header className="topbar">
            <div className="topbar-title">
              <a href="#/today" className="topbar-mark" aria-label="Joule, go to Today">
                <BrandMark size={26} variant="bare" decorative />
              </a>
              <strong className="mobile-only" aria-hidden="true">
                {destination.label}
              </strong>
            </div>
            <div className="topbar-right">
              {showRunning && (
                <div className="check-running-mini">
                  <a href="#/insights" className="check-running-mini-link">
                    <LoaderCircle size={15} className="spin" aria-hidden="true" />
                    <span>Checking</span>
                  </a>
                  <button
                    type="button"
                    className="icon-button"
                    aria-label={`Stop the ${words.aiCheck}`}
                    disabled={busy}
                    onClick={stopCheck}
                  >
                    <Square size={14} aria-hidden="true" />
                  </button>
                </div>
              )}
              {updatedAt && (
                <span className="updated-at" title={fetchedAt ? dayTime(fetchedAt) : undefined}>
                  {updatedAt}
                </span>
              )}
              <Button
                variant="ghost"
                size="sm"
                className="refresh-button"
                disabled={refreshing}
                onClick={() => void refreshNow()}
              >
                <RefreshCw size={15} aria-hidden="true" className={refreshing ? "spin" : undefined} />
                <span>Refresh now</span>
              </Button>
              <StatusChip
                health={health}
                predbatConfigured={c.predbatConfigured}
                demo={c.demo}
                onRefresh={() => void refreshNow()}
                refreshing={refreshing}
                setup={setupProgress ? checklistCounts(setupProgress) : null}
              />
              <NotificationsBell data={data} />
              <a
                href="#/setup"
                className="icon-button setup-link"
                aria-label="Setup"
                aria-current={route.destination === "setup" ? "page" : undefined}
              >
                <Settings2 size={20} aria-hidden="true" />
              </a>
            </div>
          </header>
          <main id="main" ref={mainRef} tabIndex={-1}>
            <AlertRows items={alerts} />
            {route.destination === "today" && (c.demo ? <DemoTour /> : <FirstRunNotice />)}
            <div className="page-heading">
              <div>
                <h1 ref={heading} tabIndex={-1}>
                  {destination.label}
                </h1>
                <p>{description}</p>
              </div>
              <div className="heading-actions">
                {route.destination === "today" && (
                  <span className="date-label">
                    <Clock size={14} aria-hidden="true" />
                    {dayLabel(Date.now(), { timeZone: energyTimeZone })}
                  </span>
                )}
              </div>
            </div>
            {showRunning && (
              <div className="check-running" role="status">
                <LoaderCircle size={16} className="spin" aria-hidden="true" />
                <a href="#/insights">An {words.aiCheck} is running</a>
                <Button variant="ghost" size="sm" disabled={busy} onClick={stopCheck}>
                  <Square size={12} aria-hidden="true" />
                  Stop
                </Button>
              </div>
            )}
            <SubNav route={route} counts={route.destination === "insights" ? { suggestions: insightsCount } : {}} />
            <ErrorBoundary label={title} resetKey={route.hash}>
              <Suspense fallback={<LoadingBlock lines={6} label={`Loading ${title}`} />}>
                <Page />
              </Suspense>
            </ErrorBoundary>
            <SiteFooter
              version={about.version}
              lastRead={`${c.demo ? "(demo) " : ""}${s.lastCollection ? dayTime(s.lastCollection) : "not yet"}`}
            />
          </main>
        </div>
        <TabBar route={route} insightsCount={insightsCount} />
      </div>
      <div className="sr-only" aria-live="polite" aria-atomic="true">
        {announcement}
      </div>
      <ToastStack toasts={toasts.toasts} dismiss={toasts.dismiss} />
      <ReplyOutcomeHost mutate={mutate} reload={load} />
      <ProposalReviewDialog proposal={proposal} setProposal={setProposal} />
    </AppContext.Provider>
  );
}
