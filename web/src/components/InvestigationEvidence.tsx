import { useCallback, useEffect, useState } from "react";
import { ArrowLeft, Eye, FlaskConical, ListChecks, RotateCw, X } from "lucide-react";
import { useApp } from "../context/AppContext";
import { Button, ButtonLink, Chip, Disclosure } from "./ui";
import {
  InvestigationSummary,
  InvestigationText,
  RichText,
  VerdictBadge,
  investigationVerdict,
} from "./InvestigationText";
import { EvidencePanel } from "./InvestigationSources";
import { InvestigationNextStepCard } from "./InvestigationNextSteps";
import { ConfigFileChangeCard } from "./ConfigFileChangeCard";
import { ComposerWatch, RecommendationReply } from "./RecommendationReply";
import { CloseButtons } from "./CloseActions";
import { RecommendationCard } from "../pages/RecommendationsPage";
import type { Investigation } from "../types";
import {
  failureView,
  headlineOf,
  impactChipFor,
  isOpenFileChange,
  isOpenNextStep,
  isUnfinished,
  isUserQuestion,
  repeatText,
  requestLabel,
  stepsOf,
} from "../lib/insights";
import { providerLabel } from "../lib/labels";
import { closePaths } from "../lib/closing";
import { dayTime, range } from "../lib/time";
import { plainText } from "../lib/sanitize";
import "./InvestigationEvidence.css";

/** A check that didn't finish: why in plain words, what happens next, and what you can do. */
function FailurePanel({ i }: { i: Investigation }) {
  const { mutate, busy, data } = useApp();
  const f = failureView(i);
  return (
    <section className="failure-panel" aria-label="Why this check didn't finish">
      <p className="failure-message">{f.message}</p>
      {f.nextTry && <p className="failure-next">{f.nextTry}.</p>}
      <div className="card-action-row">
        {f.action?.kind === "resume" && (
          <Button
            size="sm"
            disabled={busy || data.ai.running}
            onClick={() =>
              void mutate(`/investigations/${encodeURIComponent(i.id)}/resume`, {}, "Trying that check again.")
            }
          >
            <RotateCw size={14} aria-hidden="true" />
            {f.action.label}
          </Button>
        )}
        {f.action?.kind === "setup" && (
          <ButtonLink size="sm" variant="primary" href="#/setup/ai">
            {f.action.label}
          </ButtonLink>
        )}
      </div>
      {f.technical.length > 0 && (
        <details className="technical" data-raw>
          <summary>Technical details</summary>
          <dl className="technical-list">
            {f.technical.map((t) => (
              <div key={t.label}>
                <dt>{t.label}</dt>
                <dd>{t.value}</dd>
              </div>
            ))}
          </dl>
        </details>
      )}
    </section>
  );
}

/** Steps in words, with the raw calls behind one toggle; schema look-ups and re-reads are hidden. */
function Steps({ i }: { i: Investigation }) {
  const [technical, setTechnical] = useState(false);
  const steps = stepsOf(i).filter((s) => technical || !s.minor);
  if (!steps.length) return null;
  return (
    <div className="steps-block">
      <ol className="step-list">
        {steps.map((s, n) => (
          <li key={n}>
            <span className="step-number" aria-hidden="true">
              {n + 1}
            </span>
            <span>
              <RichText text={s.label} />
              {technical && s.detail !== s.label && (
                <span className="mono-text step-detail" data-raw>
                  {s.detail}
                </span>
              )}
            </span>
          </li>
        ))}
      </ol>
      <Button variant="ghost" size="sm" aria-pressed={technical} onClick={() => setTechnical(!technical)}>
        {technical ? "Hide technical details" : "Show technical details"}
      </Button>
    </div>
  );
}

/** One sentence for a check whose items are all closed, so "For you" never shows a bare "Closed 1". */
function closedSentence(proposalStatuses: string[], closedCount: number) {
  const one = proposalStatuses.length === 1;
  if (proposalStatuses.includes("Reverted"))
    return one
      ? "You applied this suggestion, then undid it."
      : "You applied a suggestion from this check, then undid it.";
  if (proposalStatuses.includes("Applied"))
    return one
      ? "You applied this suggestion. Joule is tracking it as a trial."
      : "You applied a suggestion from this check.";
  if (proposalStatuses.length && proposalStatuses.every((s) => s === "Denied"))
    return one ? "You declined this suggestion." : "You declined these suggestions.";
  return closedCount === 1
    ? "Nothing left to do. The item below is closed."
    : "Nothing left to do. The items below are closed.";
}

/** What a check left for you: suggestions, to-dos and file edits, open first; closed ones fold away. */
function LeftForYou({ i }: { i: Investigation }) {
  const { api, mutate, data } = useApp();
  const proposals = data.state.proposals.filter((p) => p.investigationId === i.id);
  const pending = proposals.filter((p) => p.status === "Pending");
  const steps = i.nextSteps ?? [],
    files = i.fileChanges ?? [];
  const openSteps = steps.filter(isOpenNextStep),
    openFiles = files.filter(isOpenFileChange);
  const closedSteps = steps.filter((s) => !isOpenNextStep(s)),
    closedFiles = files.filter((f) => !isOpenFileChange(f));
  const closedProposals = proposals.length - pending.length;
  const closed = closedSteps.length + closedFiles.length + closedProposals;
  const anyOpen = pending.length + openSteps.length + openFiles.length > 0;
  if (!anyOpen && !closed) return null;
  const closedCards = (
    <div className="detail-cards">
      {closedFiles.map((change) => (
        <ConfigFileChangeCard key={change.id} investigation={i} change={change} api={api} mutate={mutate} />
      ))}
      {closedSteps.map((step, n) => (
        <InvestigationNextStepCard key={step.id ?? n} investigation={i} step={step} api={api} mutate={mutate} />
      ))}
      {closedProposals > 0 && (
        <p className="muted">
          {closedProposals === 1 ? "The suggestion" : `${closedProposals} suggestions`} from this check{" "}
          {closedProposals === 1 ? "is" : "are"} under{" "}
          <a className="text-link" href="#/insights/suggestions?view=closed">
            Suggestions › Closed
          </a>
          .
        </p>
      )}
    </div>
  );
  return (
    <section className="detail-section detail-for-you" aria-label="What this check left for you">
      <h3 className="detail-heading">
        <ListChecks size={16} aria-hidden="true" />
        For you
      </h3>
      {anyOpen ? (
        <div className="detail-cards">
          {pending.map((p) => (
            <RecommendationCard key={p.id} proposal={p} />
          ))}
          {openFiles.map((change) => (
            <ConfigFileChangeCard key={change.id} investigation={i} change={change} api={api} mutate={mutate} />
          ))}
          {openSteps.map((step, n) => (
            <InvestigationNextStepCard key={step.id ?? n} investigation={i} step={step} api={api} mutate={mutate} />
          ))}
        </div>
      ) : (
        <p className="detail-done">
          {closedSentence(
            proposals.filter((p) => p.status !== "Pending").map((p) => p.status),
            closed,
          )}
        </p>
      )}
      {closed > 0 &&
        (anyOpen ? (
          <Disclosure summary="Closed" count={closed} className="detail-closed">
            {closedCards}
          </Disclosure>
        ) : (
          closedCards
        ))}
    </section>
  );
}

/** Why the findings are closed, in a sentence: "You dismissed this finding 14:05: “…”". */
function closedNote(i: Investigation) {
  const when = i.dismissedAt ? dayTime(i.dismissedAt) : "";
  switch (i.closedReason) {
    case "not_needed":
      return `Closed as not needed ${when}.`;
    case "resolved":
      return `Closed ${when}: nothing from it is waiting for you.`;
    case "repeat":
      return "Not raised again: you closed the same finding recently.";
    case "own_traffic":
      return `Closed ${when}: it was about Joule's own connection to Predbat, not your system.`;
    default:
      return `You dismissed this finding ${when}${i.decisionNote ? `: “${i.decisionNote}”` : "."}`;
  }
}

/** What the AI said it would test next time, and how it turned out. */
function Claims({ i }: { i: Investigation }) {
  const { data } = useApp();
  const claims = (data.state.claims ?? []).filter((c) => c.investigationId === i.id || c.resolvedBy === i.id);
  if (!claims.length) return null;
  const tone = (s: string) => (s === "confirmed" ? "success" : s === "refuted" ? "warn" : "neutral");
  const label = (s: string) => (s === "confirmed" ? "Confirmed" : s === "refuted" ? "Turned out wrong" : "To test");
  return (
    <section className="detail-section" aria-label="What Joule will test">
      <h3 className="detail-heading">
        <FlaskConical size={16} aria-hidden="true" />
        What Joule will test next time
      </h3>
      <ul className="claim-list">
        {claims.map((c) => (
          <li key={c.id}>
            <Chip tone={tone(c.status)}>{label(c.status)}</Chip>
            <div>
              <p>
                <RichText text={c.text} />
              </p>
              {c.test && (
                <p className="muted">
                  How: <RichText text={c.test} />
                </p>
              )}
              {c.reason && c.status !== "open" && (
                <p className="muted">
                  <RichText text={c.reason} />
                </p>
              )}
            </div>
          </li>
        ))}
      </ul>
    </section>
  );
}

/**
 * One check, in full: what it was asked, what it found (or why it didn't finish), what it left for you, what it will
 * watch, the evidence and sources behind it, and a reply thread.
 */
export function InvestigationDetail({
  investigation: i,
  onBack,
  onClose,
}: {
  investigation: Investigation;
  /** Phones and tablets: the "All checks" link above the title. */
  onBack?: () => void;
  /** Two-pane layout: a close button beside the badges. */
  onClose?: () => void;
}) {
  const { api, mutate, data } = useApp();
  const unfinished = isUnfinished(i);
  // What closes with the finding if you dismiss it: its pending suggestions, open to-dos and file edits.
  const openItems =
    data.state.proposals.filter((p) => p.investigationId === i.id && p.status === "Pending").length +
    (i.nextSteps ?? []).filter(isOpenNextStep).length +
    (i.fileChanges ?? []).filter(isOpenFileChange).length;
  const verdict = investigationVerdict(i);
  const impact = impactChipFor(i);
  const repeat = repeatText(i);
  // "Finding: high confidence", so it never reads as the confidence of a suggested change (that one says "Change: …").
  const confidence = /^(high|medium|low)$/i.test(i.confidence ?? "")
    ? `Finding: ${(i.confidence ?? "").toLowerCase()} confidence`
    : null;
  const request = requestLabel(i);
  const range_ = i.request?.from && i.request.to ? range(i.request.from, i.request.to) : null;
  // A card's reply composer being open hides the check's own "Reply to Joule": one place to type at a time.
  const [composers, setComposers] = useState<Set<string>>(() => new Set());
  const watch = useCallback((key: string, open: boolean) => {
    setComposers((current) => {
      if (current.has(key) === open) return current;
      const next = new Set(current);
      if (open) next.add(key);
      else next.delete(key);
      return next;
    });
  }, []);
  const meta = [
    dayTime(i.at),
    providerLabel(i.provider),
    confidence,
    i.attempts && i.attempts > 1 ? `${i.attempts} tries` : null,
  ]
    .filter(Boolean)
    .join(" · ");
  return (
    <ComposerWatch.Provider value={watch}>
      <article className="check-detail" aria-labelledby="check-title">
        {onBack && (
          <a
            className="detail-back"
            href="#/insights"
            onClick={(e) => {
              e.preventDefault();
              onBack();
            }}
          >
            <ArrowLeft size={16} aria-hidden="true" />
            All checks
          </a>
        )}
        <header className="detail-header">
          <div className="detail-badges">
            <VerdictBadge verdict={verdict} investigation={i} />
            {impact && <Chip tone={impact.tone}>{impact.label}</Chip>}
            {repeat && <Chip tone="neutral">{repeat}</Chip>}
            {onClose && (
              <Button
                variant="ghost"
                size="sm"
                className="detail-close"
                aria-label="Close this check"
                onClick={onClose}
              >
                <X size={18} aria-hidden="true" />
              </Button>
            )}
          </div>
          <h2 id="check-title" tabIndex={-1}>
            <RichText text={headlineOf(i)} />
          </h2>
          <p className="detail-meta">{meta}</p>
          {(request !== "Check" || range_) && (
            <p className={`detail-request${isUserQuestion(i) ? " is-question" : ""}`}>
              {request === "Check" ? (
                <>Looked at {range_}</>
              ) : (
                <>
                  <RichText text={request} />
                  {range_ && <> · looked at {range_}</>}
                </>
              )}
            </p>
          )}
        </header>

        {unfinished ? (
          <FailurePanel i={i} />
        ) : (
          <section className="detail-summary" aria-label="What it found">
            <InvestigationSummary text={i.summary} previewCharacters={1200} />
          </section>
        )}

        {!unfinished && <LeftForYou i={i} />}

        {!!i.watching?.length && (
          <section className="detail-section" aria-label="What Joule will keep an eye on">
            <h3 className="detail-heading">
              <Eye size={16} aria-hidden="true" />
              Joule will keep an eye on
            </h3>
            <ul className="watch-list">
              {i.watching.map((w, n) => (
                <li key={n}>
                  <RichText text={w} />
                </li>
              ))}
            </ul>
          </section>
        )}

        <Claims i={i} />

        {i.status === "Completed" && verdict !== "no_change" && (
          <section className="detail-section investigation-reply" aria-label={`Reply to: ${plainText(i.title)}`}>
            {i.dismissedAt && (
              <p className="follow-up-note finding-closed-note">
                {closedNote(i)}{" "}
                <Button
                  variant="link"
                  size="sm"
                  onClick={() =>
                    void mutate(`/investigations/${encodeURIComponent(i.id)}/reopen`, {}, "Findings reopened.")
                  }
                >
                  Reopen
                </Button>
              </p>
            )}
            <RecommendationReply
              title={plainText(i.title)}
              thread={i.thread}
              open={!i.dismissedAt}
              replyLabel="Reply to Joule"
              prominent
              target="check"
              hideReplyButton={composers.size > 0}
              replyPath={`/investigations/${encodeURIComponent(i.id)}/reply`}
              dismissPath={`/investigations/${encodeURIComponent(i.id)}/dismiss`}
              reopenPath={`/investigations/${encodeURIComponent(i.id)}/reopen`}
              api={api}
              mutate={mutate}
              actionsAfter={
                !i.dismissedAt && (
                  <CloseButtons
                    kind="finding"
                    title={plainText(headlineOf(i))}
                    paths={closePaths("finding", i.id)}
                    describedBy="check-title"
                    openItems={openItems}
                  />
                )
              }
            />
          </section>
        )}

        <div className="detail-disclosures">
          {!unfinished && i.evidence?.length > 0 && (
            <Disclosure summary="Evidence" count={i.evidence.length} className="investigation-observations">
              <ol className="investigation-observation-list">
                {i.evidence.map((e, n) => (
                  <li key={n}>
                    <span className="investigation-observation-number" aria-hidden="true">
                      {n + 1}
                    </span>
                    <InvestigationText text={e} />
                  </li>
                ))}
              </ol>
            </Disclosure>
          )}
          <Disclosure summary="Steps Joule took" count={stepsOf(i).filter((s) => !s.minor).length || undefined}>
            <Steps i={i} />
            {!stepsOf(i).length && <p className="muted">No steps were recorded.</p>}
          </Disclosure>
          <EvidencePanel api={api} id={i.id} />
          {i.request?.scheduled && i.request.question && (
            <details className="technical" data-raw>
              <summary>What Joule asked the AI</summary>
              <p className="mono-text">{i.request.question}</p>
            </details>
          )}
        </div>
        <p className="detail-footnote">
          Checks are the AI's reading of your data. Check the evidence before acting on them.
        </p>
      </article>
    </ComposerWatch.Provider>
  );
}

/** The detail page for a check that isn't in the current list (archived): loads it by id. */
export function InvestigationById({ id, onBack }: { id: string; onBack?: () => void }) {
  const { api } = useApp();
  const [record, setRecord] = useState<{ id: string; value: Investigation | "missing" } | null>(null);
  useEffect(() => {
    let live = true;
    api<Investigation>(`/investigations/${encodeURIComponent(id)}`)
      .then((value) => live && setRecord({ id, value }))
      .catch(() => live && setRecord({ id, value: "missing" }));
    return () => {
      live = false;
    };
  }, [api, id]);
  const value = record?.id === id ? record.value : null;
  if (value === "missing")
    return (
      <div className="check-detail">
        {onBack && (
          <a className="detail-back" href="#/insights">
            <ArrowLeft size={16} aria-hidden="true" />
            All checks
          </a>
        )}
        <p className="inbox-empty">
          <strong>This check isn't available.</strong> It may have been removed by the history clean-up.
        </p>
      </div>
    );
  if (!value)
    return (
      <p className="muted" role="status">
        Loading the check…
      </p>
    );
  return <InvestigationDetail investigation={value} onBack={onBack} />;
}
