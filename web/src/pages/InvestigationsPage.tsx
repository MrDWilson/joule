import { useEffect, useRef, useState } from "react";
import { ArrowRight, X } from "lucide-react";
import { useApp } from "../context/AppContext";
import { Button } from "../components/ui";
import { InvestigationById, InvestigationDetail } from "../components/InvestigationEvidence";
import { RichText, VerdictBadge, investigationVerdict } from "../components/InvestigationText";
import { MemoryPanel } from "../components/MemoryPanel";
import { NeedsYou } from "../components/insights/NeedsYou";
import { AskJoule, RunningCheck } from "../components/insights/AskJoule";
import { RunTimeline } from "../components/insights/RunTimeline";
import { closedItems, headlineOf, isUnfinished, verdictLook } from "../lib/insights";
import { buildHash } from "../lib/router";
import { useMediaQuery } from "../lib/useMediaQuery";
import type { Investigation } from "../types";
import "./insights.css";

/** Two panes (list and the open check) from this width; below it a check opens as its own full screen. */
const TWO_PANES = "(min-width: 1180px)";

/**
 * The check you started (or saw running) once it has finished: shown as a card at the top and announced once. A check can
 * finish before the page ever sees it running (the demo answers at once), so it is matched by start time, not by state.
 */
function useFinished(
  investigations: Investigation[],
  running: boolean,
  runningSince?: string,
  onFinished?: (finished: Investigation) => void,
) {
  const [since, setSince] = useState<number | null>(null);
  const [hidden, setHidden] = useState<string | null>(null);
  const [announcement, setAnnouncement] = useState("");
  const announced = useRef<string | null>(null);
  // Seen running: count from when that check started (it may have been running for a while already).
  if (running && since == null) setSince(Math.min(Date.now(), Date.parse(runningSince ?? "") || Date.now()));
  const finished =
    since != null && !running
      ? ([...investigations]
          .filter((i) => i.status !== "Running" && Date.parse(i.at) >= since - 20_000)
          .sort((a, b) => Date.parse(b.at) - Date.parse(a.at))[0] ?? null)
      : null;
  const shown = finished && finished.id !== hidden ? finished : null;
  useEffect(() => {
    if (running) setAnnouncement("Check started.");
  }, [running]);
  const finishedRef = useRef(onFinished);
  finishedRef.current = onFinished;
  useEffect(() => {
    if (!shown || announced.current === shown.id) return;
    announced.current = shown.id;
    setAnnouncement(`Check finished. ${verdictLook(shown).label}: ${headlineOf(shown)}`);
    finishedRef.current?.(shown);
  }, [shown]);
  return {
    finished: shown,
    started: () => {
      setSince(Date.now());
      setHidden(null);
    },
    hide: () => setHidden(finished?.id ?? null),
    announcement,
  };
}

/** Insights home: what needs you, Ask Joule, then recent checks; a check opens beside the list or as its own screen. */
export default function InvestigationsPage() {
  const { data, focusedInvestigation: id, setFocusedInvestigation, busy, mutate, notify } = useApp();
  const s = data.state;
  const wide = useMediaQuery(TWO_PANES);
  const running = data.ai.running;
  const runningRecord = s.investigations.find((i) => i.status === "Running") ?? null;
  // The question you just asked: shown as "You asked: …" while Joule checks, and on the answer's banner.
  const [asked, setAsked] = useState<{ question: string | null; answered: boolean } | null>(null);
  const { finished, started, hide, announcement } = useFinished(
    s.investigations,
    running,
    runningRecord?.at,
    (done) => {
      if (!asked || asked.answered) return;
      setAsked({ ...asked, answered: true });
      // Replaces "Asked. Joule is checking." so the toast never says it is still checking.
      notify(
        isUnfinished(done)
          ? "The check didn't finish. See why at the top."
          : asked.question
            ? "Joule has answered your question."
            : "Check finished.",
      );
    },
  );
  const pendingQuestion = asked && !asked.answered ? asked.question : null;
  const finishedQuestion = finished
    ? finished.request?.question && !finished.request.scheduled
      ? finished.request.question
      : (asked?.question ?? null)
    : null;
  const selected = id ? (s.investigations.find((i) => i.id === id) ?? null) : null;
  const closed = closedItems(s).length;

  // On narrow screens the open check replaces the list: start it at the top, and come back to where you were.
  const listScroll = useRef(0);
  useEffect(() => {
    if (id) return;
    const remember = () => (listScroll.current = window.scrollY);
    window.addEventListener("scroll", remember, { passive: true });
    return () => window.removeEventListener("scroll", remember);
  }, [id]);
  const previous = useRef(id);
  const previousId = useRef(id);
  useEffect(() => {
    if (!wide && id && id !== previous.current) {
      window.scrollTo({ top: 0 });
      document.getElementById("check-title")?.focus({ preventScroll: true });
    }
    if (!wide && !id && previous.current) window.scrollTo({ top: listScroll.current });
    if (wide && id && id !== previous.current) document.getElementById("check-title")?.focus({ preventScroll: true });
    previous.current = id;
  }, [id, wide]);

  // "All checks" goes back in history when the check was opened from the list, so Back and the link agree.
  const openedFromList = useRef(false);
  useEffect(() => {
    if (id && !previousId.current) openedFromList.current = true;
    if (!id) openedFromList.current = false;
    previousId.current = id;
  }, [id]);
  const back = () => {
    if (openedFromList.current) window.history.back();
    else setFocusedInvestigation("");
  };
  const detail = id ? (
    selected ? (
      <InvestigationDetail
        key={id}
        investigation={selected}
        onBack={wide ? undefined : back}
        onClose={wide ? back : undefined}
      />
    ) : (
      <InvestigationById key={id} id={id} onBack={wide ? undefined : back} />
    )
  ) : null;

  const live = (
    <div className="sr-only" aria-live="polite" aria-atomic="true">
      {announcement}
    </div>
  );

  if (id && !wide)
    return (
      <div className="insights-page is-detail">
        {live}
        {running && <RunningCheck running={runningRecord} asked={pendingQuestion} compact />}
        {detail}
      </div>
    );

  return (
    <div className={`insights-page${id ? " has-detail" : ""}`}>
      {live}
      <div className="insights-main">
        {running && <RunningCheck running={runningRecord} asked={pendingQuestion} />}
        {!running && finished && (
          <section className="finished-check" aria-label="Check finished">
            <VerdictBadge verdict={investigationVerdict(finished)} investigation={finished} />
            <p className="finished-headline">
              <span className="finished-eyebrow">
                {finishedQuestion ? <>Answer to “{finishedQuestion}”</> : "Check finished"}
              </span>
              <RichText text={headlineOf(finished)} />
            </p>
            <a
              className="button button-secondary button-sm"
              href={buildHash("insights", "", { id: finished.id })}
              onClick={hide}
            >
              Read it <ArrowRight size={14} aria-hidden="true" />
            </a>
            <Button variant="ghost" size="sm" className="finished-close" aria-label="Hide this" onClick={hide}>
              <X size={16} aria-hidden="true" />
            </Button>
          </section>
        )}
        <NeedsYou limit={4} closedCount={closed} />
        <div className="insights-rail">
          <AskJoule
            onStarted={(question) => {
              started();
              setAsked({ question, answered: false });
            }}
            pending={pendingQuestion}
            compact={!!id}
          />
          {/* With a check open beside the list, the memory row waits so Recent checks starts in the first view. */}
          {!id && <MemoryPanel memory={data.memory ?? []} mutate={mutate} busy={busy} />}
        </div>
        <RunTimeline selectedId={id} />
      </div>
      {detail && (
        <aside className="insights-detail-pane" aria-label="The open check">
          {detail}
        </aside>
      )}
    </div>
  );
}
