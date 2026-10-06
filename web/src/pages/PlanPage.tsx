import { useEffect, useMemo, useRef, useState } from "react";
import { History } from "lucide-react";
import { useApp } from "../context/AppContext";
import { buildHash, navigate } from "../lib/router";
import { Empty } from "../components/ui/Panel";
import { Disclosure } from "../components/ui";
import { PlanBrowser } from "../components/PlanBrowser";
import { AlternativeForecast } from "../components/AlternativeForecast";
import { PlanMeasuredEvidence } from "../components/plan/SlotEvidence";
import { EnergyTimeline } from "../components/charts/EnergyTimeline";
import { mergeTimeline, reserveSetting } from "../components/charts/timeline";
import { planSentence, planWindows, type PlanSlotLike } from "../components/plan/windows";
import { StatusStrip } from "../components/plan/StatusStrip";
import { WhatsNext } from "../components/plan/WhatsNext";
import { WhatHappened, useDayEvidence } from "../components/plan/WhatHappened";
import { actionOf, batteryNow, slotAt, type Reading } from "../components/today/model";
import { dayTime } from "../lib/time";
import type { Api } from "../completion-types";
import type { Plan } from "../types";
import "./plan.css";

function useNow() {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const t = window.setInterval(() => setNow(Date.now()), 60000);
    return () => window.clearInterval(t);
  }, []);
  return now;
}

/** What Predbat plans to do, and how the last day compared with its plan. */
export default function PlanPage() {
  const { api, data, measured, timeZone, reportError, route } = useApp();
  const s = data.state;
  const now = useNow();
  const planRequest = useRef(0);
  const [planLoading, setPlanLoading] = useState(false);
  const [selectedPlan, setSelectedPlan] = useState<Plan | null>(null);
  // The chosen snapshot lives in the URL (#/plan/:planId), so it survives a refresh and can be linked to.
  const planId = route.params.planId ?? "";
  // The plan on screen stays until the next one arrives, so stepping through plans doesn't blank the page or lose the
  // scroll position.
  function choosePlan(id: string) {
    navigate(buildHash("plan", "", id ? { planId: id } : {}));
  }
  useEffect(() => {
    const request = ++planRequest.current;
    if (!planId) {
      setPlanLoading(false);
      return;
    }
    let active = true;
    setPlanLoading(true);
    api<Plan>("/plans/" + encodeURIComponent(planId))
      .then((result) => {
        if (active && request === planRequest.current) setSelectedPlan(result);
      })
      .catch((e: Error) => {
        if (active && request === planRequest.current) {
          setSelectedPlan(null);
          reportError(e.message);
        }
      })
      .finally(() => {
        if (active && request === planRequest.current) setPlanLoading(false);
      });
    return () => {
      active = false;
    };
  }, [api, planId, data.state.lastCollection, measured.telemetry?.lastCollection, reportError]);
  const evidenceRefresh = `${data.state.lastCollection || ""}/${measured.telemetry?.lastCollection || ""}`;
  const plan = planId ? selectedPlan : data.plan;
  // The banner's stepper works from the plan asked for, not one still on screen while it loads.
  const shownAt = planId ? (selectedPlan?.id === planId ? selectedPlan.at : undefined) : data.plan?.at;
  const slots = useMemo(() => (plan?.slots ?? []) as PlanSlotLike[], [plan]);
  const reserve = reserveSetting(s.settings);
  // Elapsed half-hours lead into the current plan; a selected earlier plan is shown on its own.
  const latestChart = useMemo(
    () => mergeTimeline(measured.recent as PlanSlotLike[], (data.plan?.slots ?? []) as PlanSlotLike[]),
    [measured.recent, data.plan],
  );
  const chart = planId ? slots : latestChart;
  const ahead = useMemo(
    () => planWindows(slots, { now, reserve, from: planId ? -Infinity : now }),
    [slots, now, reserve, planId],
  );
  const evidence = useDayEvidence(api, now);
  const current = slotAt(slots, now);
  const latestSlots = useMemo(() => (data.plan?.slots ?? []) as PlanSlotLike[], [data.plan]);
  const readings = (measured.telemetry?.latestReadings ?? {}) as Record<string, Reading>;
  const battery = batteryNow({
    reading: readings.soc,
    recent: measured.recent as PlanSlotLike[],
    plan: latestSlots,
    now,
  });
  const liveSoc = battery.stale ? null : battery.value;
  return (
    <div className="plan-page">
      {/* An earlier plan has its own banner below; the status strip is about Predbat now. */}
      {!planId && (
        <StatusStrip
          settings={s.settings}
          planAt={plan?.at}
          collectedAt={plan?.collectedAt ?? s.lastCollection}
          lastCollection={s.lastCollection}
          collectionError={s.collectionError}
          action={current ? actionOf(current) : null}
          reserve={reserve}
          now={now}
          timeZone={timeZone}
          demo={data.connection.demo}
          selected={false}
          battery={liveSoc}
          next={ahead.find((w) => w.phase === "future" && w.tone !== "neutral" && !w.atReserve)}
        />
      )}
      {planId && (
        // A div, not a section: the shared "section + section" spacing would double the gap under the status strip.
        <div className="plan-selected" role="region" aria-label="Earlier plan">
          <div className="plan-selected-head">
            <p role="status">
              <History size={15} aria-hidden="true" />
              {planLoading
                ? "Loading the earlier plan…"
                : plan
                  ? `Showing the plan Predbat made ${dayTime(plan.at, { timeZone })}.`
                  : "That plan couldn’t be loaded."}
            </p>
            <button type="button" className="text-link" onClick={() => choosePlan("")}>
              Back to the latest plan
            </button>
          </div>
          <PlanBrowser
            api={api}
            selected={planId}
            at={shownAt}
            onChoose={choosePlan}
            refreshKey={s.lastCollection || ""}
            timeZone={timeZone}
          />
        </div>
      )}
      {slots.length || (!planId && latestChart.length) ? (
        <>
          <section className="panel" aria-labelledby="plan-timeline-heading">
            <div className="panel-head">
              <div>
                <h2 id="plan-timeline-heading">{planId ? "The earlier plan" : "The plan, and how it’s going"}</h2>
                <p>
                  {planId
                    ? "Its forecast, with what actually happened where the meters have caught up"
                    : "The last day as planned and measured, then the plan ahead"}
                </p>
              </div>
            </div>
            <EnergyTimeline
              slots={chart}
              timeZone={timeZone}
              api={planId ? undefined : api}
              preset="plan"
              reserve={reserve}
              currentSoc={planId ? null : measured.telemetry?.latestReadings.soc}
              dataSince={measured.telemetry?.firstObservationAt}
              labelledBy="plan-timeline-heading"
              describePlan={(from, to) => planSentence(ahead, from, to, now, timeZone)}
              // An earlier plan opens on its whole span, and its caption speaks as of the plan ("This plan expected…").
              asOfPlan={!!planId}
              defaultRange={planId ? "all" : undefined}
            />
            <Disclosure summary="How to read this chart" className="plan-help">
              <p>
                Dashed lines are Predbat’s forecast; solid lines are what your meters measured. The coloured blocks
                along the top are Predbat’s plan, over a strip that is darker when import is cheaper. Meter readings
                rarely land exactly on the half-hour, so energy from a reading that spans two half-hours is shared
                between them by time; the tooltip marks those figures with ≈.
              </p>
              <p>
                A gap in readings of up to an hour is joined with a faint line in the same colour. A longer gap is a
                light shaded band that names the meter: missing readings are never drawn as zero. Solar overnight, when
                Predbat expects none, counts as zero.
              </p>
            </Disclosure>
          </section>
          {!planId && (
            <section className="panel" aria-labelledby="happened-heading">
              <div className="panel-head">
                <div>
                  <h2 id="happened-heading">What happened (last 24 h)</h2>
                  <p>Predbat’s plan, made just before each half-hour, against what your meters measured</p>
                </div>
              </div>
              <WhatHappened
                history={latestChart as PlanSlotLike[]}
                evidence={evidence}
                now={now}
                reserve={reserve}
                timeZone={timeZone}
              />
            </section>
          )}
          <section className="panel" aria-labelledby="next-heading">
            <div className="panel-head">
              <div>
                <h2 id="next-heading">{planId ? "What this plan said" : "What’s next"}</h2>
                <p>
                  Each window expands to its half-hours. Prices in pence per kWh: import coloured from cheapest to
                  dearest; export windows show what you’re paid, greener when it pays more.
                </p>
              </div>
            </div>
            <WhatsNext
              windows={ahead}
              now={now}
              timeZone={timeZone}
              reserve={reserve}
              totalFrom={planId ? -Infinity : now}
              liveSoc={planId ? null : liveSoc}
            />
          </section>
          {planId && plan && (
            <PlanMeasuredEvidence
              api={api}
              id={plan.id}
              slots={plan.slots}
              refreshKey={evidenceRefresh}
              timeZone={timeZone}
            />
          )}
          {plan && (
            <AlternativeForecastGate api={api} planId={plan.id} refreshKey={evidenceRefresh} timeZone={timeZone} />
          )}
          {!planId && (
            <Disclosure summary="Browse earlier plans" className="plan-compare">
              <PlanBrowser
                api={api}
                selected=""
                at={data.plan?.at}
                onChoose={choosePlan}
                refreshKey={s.lastCollection || ""}
                timeZone={timeZone}
              />
            </Disclosure>
          )}
        </>
      ) : (
        <Empty title="No plan yet">Once Joule has read Predbat, its plan appears here.</Empty>
      )}
    </div>
  );
}

/** Shows the second-forecast comparison only when one is set up; otherwise nothing (setup lives under Setup). */
function AlternativeForecastGate({
  api,
  planId,
  refreshKey,
  timeZone,
}: {
  api: Api;
  planId: string | undefined;
  refreshKey?: string;
  timeZone?: string;
}) {
  const [available, setAvailable] = useState<boolean | null>(null);
  useEffect(() => {
    let active = true;
    setAvailable(null);
    if (!planId) return;
    api<{ available: boolean }>(`/telemetry/plans/${encodeURIComponent(planId)}/alternative`)
      .then((value) => {
        if (active) setAvailable(value.available);
      })
      // On failure defer to the component, which shows its own error and retry controls.
      .catch(() => {
        if (active) setAvailable(true);
      });
    return () => {
      active = false;
    };
  }, [api, planId, refreshKey]);
  if (!planId || !available) return null;
  return <AlternativeForecast api={api} planId={planId} refreshKey={refreshKey} timeZone={timeZone} />;
}
