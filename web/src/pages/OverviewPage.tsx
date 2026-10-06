import { lazy, Suspense, useEffect, useMemo, useState } from "react";
import { AlertTriangle, ArrowRight } from "lucide-react";
import { useApp } from "../context/AppContext";
import { Button, Chip, ErrorNotice } from "../components/ui";
import { LoadingBlock } from "../components/ui/States";
import { AutomationAlert } from "../components/AutomationStatus";
import { mergeTimeline, reserveSetting } from "../components/charts/timeline";
import { planSentence, planWindows, type PlanSlotLike } from "../components/plan/windows";
import { WindowList } from "../components/plan/WindowList";
import { FELL_DURING_CHARGE, historyRows } from "../components/plan/history";
import { NowHero } from "../components/today/NowHero";
import { NeedsYou } from "../components/today/NeedsYou";
import { needsYou } from "../lib/insights";
import { TodayTiles } from "../components/today/TodayTiles";
import { AiCard } from "../components/today/AiCard";
import { LastNightCard } from "../components/today/LastNightCard";
import { useLastNight } from "../components/today/useLastNight";
import {
  actionOf,
  batteryNow,
  batteryOutlook,
  costView,
  lastCheapWindow,
  lastNight,
  nextWindow,
  nightFlag,
  planEvery,
  planStaleness,
  priceNow,
  runStrip,
  slotAt,
  soFarSentence,
  topFinding,
  type Reading,
} from "../components/today/model";
import { zoneNote } from "../lib/time";
import "./overview.css";

// The timeline loads just after the page, so the first paint shows the figures straight away.
const EnergyTimeline = lazy(() =>
  import("../components/charts/EnergyTimeline").then((m) => ({ default: m.EnergyTimeline })),
);

const HOUR = 3600000;

/** Hook: "now", ticking once a minute so relative times and the current window stay right. */
function useNow() {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const t = window.setInterval(() => setNow(Date.now()), 60000);
    return () => window.clearInterval(t);
  }, []);
  return now;
}

/**
 * Today: what is happening now, how today is going, what needs you, the timeline, what comes next, how last night went
 * and what the AI last found. Every sentence is built from the figures (components/today/model.ts).
 */
export default function OverviewPage() {
  const { api, data, go, measured, timeZone, setFocusedInvestigation, reviewProposal } = useApp();
  const { daily, yesterday, recent, meterTrends, trendError, telemetryError, telemetry } = measured;
  const now = useNow();
  const s = data.state;
  const plan = useMemo(() => (data.plan?.slots ?? []) as PlanSlotLike[], [data.plan]);
  const history = recent as PlanSlotLike[];
  const reserve = reserveSetting(s.settings);
  const chart = useMemo(() => mergeTimeline(history, plan), [history, plan]);
  const readings = (telemetry?.latestReadings ?? {}) as Record<string, Reading>;
  const soc = readings.soc ?? null;

  const windows = useMemo(() => planWindows(plan, { now, reserve }), [plan, now, reserve]);
  const [allWindows, setAllWindows] = useState(false);
  const shownWindows = windows.filter((w) => w.end > now && w.start < now + (allWindows ? 48 : 12) * HOUR);
  const current = slotAt(plan, now);
  const battery = batteryNow({ reading: soc, recent: history, plan, now });
  const price = priceNow({ importReading: readings.import_tariff, exportReading: readings.export_tariff, plan, now });
  const stale = planStaleness({
    collectedAt: data.plan?.collectedAt ?? s.lastCollection,
    lastCollection: s.lastCollection,
    collectionError: s.collectionError,
    everyMinutes: planEvery(s.settings),
    now,
  });

  // One list with Insights: setting changes, file edits, to-dos and trials due a decision, repeats shown once.
  const needs = useMemo(
    () => needsYou({ proposals: s.proposals, investigations: s.investigations, experiments: s.experiments ?? [] }, now),
    [s.proposals, s.investigations, s.experiments, now],
  );
  const cost = costView(daily, timeZone);
  // The merged timeline: measured history, then the plan (whose own past slots carry actuals when it began earlier).
  const cheap = useMemo(() => lastCheapWindow(chart as PlanSlotLike[], now), [chart, now]);
  // The window's own totals settle within half an hour of its end; after that, fetch them once.
  const settled = cheap && now - cheap.end > 30 * 60000 ? "settled" : (telemetry?.lastCollection ?? "");
  const nightData = useLastNight(api, cheap, timeZone, settled);
  const night = cheap ? lastNight(cheap, nightData.window, nightData.planned) : null;
  const nightFlags = useMemo(
    () =>
      cheap
        ? historyRows(chart as PlanSlotLike[], now)
            .filter((r) => r.start >= cheap.start && r.end <= cheap.end)
            .flatMap((r) =>
              r.flags
                // The plan history knows why the battery fell during a charge (the car, or a planned pause).
                .map((f) => nightFlag({ text: f, cause: f === FELL_DURING_CHARGE ? r.cause : null }, r.start))
                .filter((f) => !f.text.startsWith("Ended")),
            )
        : [],
    [cheap, chart, now],
  );
  const sentence = soFarSentence({ cost, night, needs: needs.length, now, timeZone });
  const top = topFinding(s.investigations, now);
  const strip = runStrip(s.investigations, now, timeZone, s.usage);
  const yesterdayCover = yesterday
    ? Math.min(
        yesterday.summary.importCostCoverage ?? yesterday.summary.costCoverageFraction,
        yesterday.summary.exportCostCoverage ?? yesterday.summary.costCoverageFraction,
      )
    : 0;
  const yesterdayCost =
    yesterday && yesterdayCover >= 0.9
      ? (yesterday.summary.netCostGbp ?? yesterday.summary.observedNetCostGbp ?? null)
      : null;
  const solarAsleep =
    !!daily &&
    (daily.metrics.pv?.energyKwh ?? 0) < 0.05 &&
    (readings.pv?.status === "idle" || (current != null && (current.pvForecast ?? 0) < 0.01));
  const openInvestigation = (id: string) => {
    setFocusedInvestigation(id);
    go("Investigations");
  };

  return (
    <div className="today">
      <NowHero
        action={current ? actionOf(current) : null}
        battery={battery}
        price={price}
        next={nextWindow(windows, now)}
        reserve={reserve}
        sentence={sentence}
        now={now}
        timeZone={timeZone}
        dispatching={readings.intelligent_slots?.rawState === "on"}
        tags={windows.find((w) => w.phase === "current")?.tags}
        stale={stale?.label}
        demo={data.connection.demo}
      />
      <NeedsYou
        items={needs}
        revisions={s.revisions}
        settings={s.settings}
        onReview={reviewProposal}
        onOpen={openInvestigation}
      />
      <AutomationAlert schedule={data.ai.schedule} scheduled={s.ai.scheduled} timeZone={timeZone} />
      {(trendError || telemetryError) && (
        <ErrorNotice
          className="today-callout"
          title={telemetryError ? "Couldn’t load today’s readings" : "Couldn’t load the 24-hour graphs"}
          error={telemetryError || trendError}
          onRetry={() => measured.retry()}
        />
      )}
      <TodayTiles
        daily={daily}
        yesterday={yesterday?.summary ?? null}
        cost={cost}
        yesterdayCost={yesterdayCost}
        trends={meterTrends}
        trendError={trendError}
        battery={battery}
        batteryOutlook={stale ? "" : batteryOutlook(windows, plan, now, timeZone)}
        chart={chart}
        reserve={reserve}
        socReading={soc}
        plan={plan}
        timeZone={timeZone}
        solarAsleep={solarAsleep}
      />
      <section className="panel today-timeline" aria-labelledby="timeline-heading">
        <div className="panel-head">
          <div>
            <h2 id="timeline-heading">Today and the plan ahead</h2>
            <p>
              {data.connection.demo ? "Demo data · " : ""}What the meters measured, then what Predbat plans next
              {zoneNote(timeZone)}
            </p>
          </div>
          {stale && (
            <Chip tone="warn" icon={<AlertTriangle size={13} aria-hidden="true" />}>
              {stale.label}
            </Chip>
          )}
        </div>
        <Suspense fallback={<LoadingBlock height={300} label="Loading the timeline" />}>
          <EnergyTimeline
            slots={chart}
            timeZone={timeZone}
            api={api}
            preset="today"
            reserve={reserve}
            currentSoc={soc}
            dataSince={telemetry?.firstObservationAt}
            labelledBy="timeline-heading"
            describePlan={(from, to) => planSentence(windows, from, to, now, timeZone)}
          />
        </Suspense>
      </section>
      <div className="today-lower">
        <section className="panel coming-up" aria-labelledby="coming-heading">
          <div className="panel-head">
            <div>
              <h2 id="coming-heading">Coming up</h2>
              <p>{allWindows ? "The next 48 hours" : "The next 12 hours"} of Predbat’s plan</p>
            </div>
            <a className="text-link" href="#/plan">
              Full plan <ArrowRight size={14} aria-hidden="true" />
            </a>
          </div>
          {stale && (
            <p className="stale-note" role="status">
              <AlertTriangle size={14} aria-hidden="true" /> {stale.label}
            </p>
          )}
          {shownWindows.length ? (
            <WindowList
              windows={shownWindows}
              now={now}
              timeZone={timeZone}
              stale={!!stale}
              reserve={reserve}
              liveSoc={battery.stale ? null : battery.value}
            />
          ) : (
            <p className="muted">No plan from Predbat yet. It appears here once Joule has read it.</p>
          )}
          {windows.some((w) => w.start >= now + 12 * HOUR) && (
            <Button variant="ghost" size="sm" className="coming-more" onClick={() => setAllWindows((v) => !v)}>
              {allWindows ? "Show the next 12 hours" : "Show the next 48 hours"}
            </Button>
          )}
        </section>
        <div className="today-side">
          <LastNightCard
            night={night}
            flags={nightFlags}
            yesterday={nightData.yesterday}
            now={now}
            timeZone={timeZone}
          />
          <AiCard
            schedule={data.ai.schedule}
            scheduled={s.ai.scheduled}
            top={top}
            strip={strip}
            timeZone={timeZone}
            onOpen={openInvestigation}
          />
        </div>
      </div>
      <p className="today-footnote muted">
        Today counts from midnight{zoneNote(timeZone)}. Costs leave out standing charges.
      </p>
    </div>
  );
}
