import { useEffect, useId, useRef, useState } from "react";
import { ArrowUp, CalendarRange, LoaderCircle, ScanSearch, Sparkles, Square } from "lucide-react";
import { useApp } from "../../context/AppContext";
import { Button } from "../ui";
import { RichText } from "../InvestigationText";
import { STAGES, elapsedText, stageOf, stepsOf, wallTimeToIso } from "../../lib/insights";
import { householdTimeZone, zoneNote } from "../../lib/time";
import type { Investigation } from "../../types";

const SUGGESTIONS = [
  "Why was yesterday expensive?",
  "Did last night's charge go to plan?",
  "Is my battery reserve right?",
];

/** Seconds since a time, ticking once a second while `live`. */
function useElapsed(from: string | null, live: boolean) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!live) return;
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [live]);
  return from ? elapsedText(from, now) : "";
}

/**
 * The check in progress: what it was asked, a stepper of friendly stages, the latest steps and how long it has run.
 * `asked` is the question you just sent, shown until the server's running record (which carries it) arrives.
 */
export function RunningCheck({
  running,
  compact = false,
  asked = null,
}: {
  running: Investigation | null;
  compact?: boolean;
  asked?: string | null;
}) {
  const { mutate, busy } = useApp();
  const steps = running ? stepsOf(running).filter((s) => !s.minor) : [];
  const stage = stageOf(steps);
  const elapsed = useElapsed(running?.at ?? null, true);
  const question = running
    ? running.request?.question && !running.request.scheduled
      ? running.request.question
      : null
    : asked;
  return (
    <section className={`running-check${compact ? " is-compact" : ""}`} aria-label="Check in progress">
      <div className="running-head">
        <span className="running-spinner" aria-hidden="true">
          <LoaderCircle size={18} className="spin" />
        </span>
        <div className="running-copy">
          <p className="running-eyebrow">{question ? "Your question · checking" : "Checking now"}</p>
          <h2>
            {question
              ? `“${question}”`
              : !running
                ? "Getting started"
                : running.request?.scheduled
                  ? `An automatic check${running.request.trigger ? `, for ${running.request.trigger}` : ""}`
                  : "A full check of your plan, meters and Predbat's log"}
          </h2>
          <p className="running-time">Running for {elapsed || "a moment"}</p>
        </div>
        <Button
          variant="secondary"
          size="sm"
          disabled={busy}
          onClick={() => void mutate("/investigations/cancel", {}, "Stopping the check.")}
        >
          <Square size={12} aria-hidden="true" />
          Stop
        </Button>
      </div>
      {!compact && (
        <ol className="stepper" aria-label="Progress">
          {STAGES.map((label, i) => (
            <li
              key={label}
              className={i < stage ? "done" : i === stage ? "current" : ""}
              aria-current={i === stage ? "step" : undefined}
            >
              <span className="stepper-dot" aria-hidden="true" />
              <span>{label}</span>
            </li>
          ))}
        </ol>
      )}
      {!compact && steps.length > 0 && (
        <ul className="running-steps" aria-label="Latest steps">
          {steps.slice(-4).map((s, i) => (
            <li key={`${i}-${s.label}`}>
              <RichText text={s.label} />
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

/**
 * Ask Joule: one line for a question, suggestion chips that ask straight away, a visible "Run a full check" button, and
 * optional dates. Dates are read in the household's time zone and checked when you ask, not while you are still
 * choosing them. The question clears once the check starts and stays on screen as "You asked: …" until it is answered
 * (`pending`). compact (a check open beside the list) keeps just the one-line input.
 */
export function AskJoule({
  onStarted,
  pending = null,
  compact = false,
}: {
  onStarted?: (question: string | null) => void;
  /** The question you asked that Joule is still answering. */
  pending?: string | null;
  compact?: boolean;
}) {
  const { mutate, busy, data } = useApp();
  const running = data.ai.running;
  const [question, setQuestion] = useState(""),
    [from, setFrom] = useState(""),
    [to, setTo] = useState(""),
    [datesOpen, setDatesOpen] = useState(false),
    [error, setError] = useState("");
  const input = useRef<HTMLInputElement>(null);
  const hintId = useId(),
    errorId = useId(),
    datesId = useId();
  const zone = householdTimeZone();

  async function run(text: string | null) {
    setError("");
    const start = from ? wallTimeToIso(from, zone) : null,
      end = to ? wallTimeToIso(to, zone) : null;
    if (Boolean(from) !== Boolean(to) || (start && end && Date.parse(start) >= Date.parse(end))) {
      setError("Choose both dates in order, or leave both blank.");
      return;
    }
    const body = { question: text?.trim() || null, from: start, to: end };
    const ok = await mutate(
      "/investigations/run",
      body,
      body.question ? "Asked. Joule is checking." : "Check started.",
    );
    if (ok) {
      setQuestion("");
      setFrom("");
      setTo("");
      setDatesOpen(false);
      onStarted?.(body.question);
    }
  }

  return (
    <section className={`insights-section ask-joule${compact ? " is-compact" : ""}`} aria-labelledby="ask-heading">
      <h2 id="ask-heading" className={compact ? "sr-only" : "insights-section-title"}>
        <Sparkles size={16} aria-hidden="true" />
        Ask Joule
      </h2>
      <form
        className="ask-form"
        onSubmit={(e) => {
          e.preventDefault();
          void run(question);
        }}
      >
        <label className="sr-only" htmlFor="ask-input">
          Your question
        </label>
        <div className="ask-field">
          <input
            id="ask-input"
            ref={input}
            className="ask-input"
            maxLength={2000}
            value={question}
            autoComplete="off"
            enterKeyHint="send"
            placeholder="Ask Joule about your energy"
            aria-describedby={`${hintId}${error ? ` ${errorId}` : ""}`}
            onChange={(e) => setQuestion(e.target.value)}
          />
          <Button type="submit" className="ask-send" disabled={busy || running} aria-label="Ask">
            {running ? (
              <LoaderCircle size={16} className="spin" aria-hidden="true" />
            ) : (
              <ArrowUp size={17} aria-hidden="true" />
            )}
          </Button>
        </div>
        <p id={hintId} className="sr-only">
          Joule reads your plan, meters and Predbat's log to answer, and never changes anything without you.
        </p>
        {pending && (
          <div className="ask-pending" role="status">
            <p className="ask-pending-bubble">
              <span className="ask-pending-label">You asked</span>“{pending}”
            </p>
            <p className="ask-pending-state">
              <LoaderCircle size={14} className="spin" aria-hidden="true" />
              Joule is checking
            </p>
          </div>
        )}
        {!compact && !pending && (
          <div className="ask-chips" role="group" aria-label="Suggested questions">
            {SUGGESTIONS.map((s) => (
              // A suggestion asks straight away: it is a whole question, so filling the box would only add a step.
              <button
                key={s}
                type="button"
                className="suggestion-chip"
                disabled={busy || running}
                onClick={() => void run(s)}
              >
                {s}
              </button>
            ))}
          </div>
        )}
        {!compact && (
          <div className="ask-footer">
            <Button
              type="button"
              variant="secondary"
              size="sm"
              disabled={busy || running}
              onClick={() => void run(null)}
            >
              <ScanSearch size={14} aria-hidden="true" />
              Run a full check
            </Button>
            <button
              type="button"
              className="ask-dates-toggle"
              aria-expanded={datesOpen}
              aria-controls={datesId}
              onClick={() => setDatesOpen((v) => !v)}
            >
              <CalendarRange size={14} aria-hidden="true" />
              {from && to ? "Time chosen" : "Choose the time to look at"}
            </button>
          </div>
        )}
        {datesOpen && !compact && (
          <div className="ask-dates" id={datesId}>
            <label className="field">
              From
              <input
                type="datetime-local"
                value={from}
                onChange={(e) => {
                  setFrom(e.target.value);
                  setError("");
                }}
              />
            </label>
            <label className="field">
              To
              <input
                type="datetime-local"
                value={to}
                onChange={(e) => {
                  setTo(e.target.value);
                  setError("");
                }}
              />
            </label>
            {zoneNote(zone) && <p className="ask-zone">Times are in the home's time zone{zoneNote(zone)}.</p>}
          </div>
        )}
        {error && (
          <p id={errorId} role="alert" className="ask-error">
            {error}
          </p>
        )}
      </form>
    </section>
  );
}
