import { useId } from "react";
import { Check, ListTodo } from "lucide-react";
import { Button } from "./ui";
import { InvestigationSummary, InvestigationText, RichText } from "./InvestigationText";
import { EvidencePanel } from "./InvestigationSources";
import { RecommendationReply } from "./RecommendationReply";
import { CloseButtons, useCloseItem } from "./CloseActions";
import { closePaths } from "../lib/closing";
import type { Api, Mutate } from "../completion-types";
import type { Investigation, InvestigationNextStep } from "../types";
import { closedStatus, isOpenNextStep } from "../lib/insights";
import { dayTime } from "../lib/time";
import { plainText } from "../lib/sanitize";

export { isOpenNextStep } from "../lib/insights";

const normaliseTitle = (title: string) => title.trim().replace(/\s+/g, " ").toLowerCase();

/** Open to-dos across every check, newest first, one card per distinct title. */
export function openInvestigationNextSteps(investigations: Investigation[], limit = 8) {
  const seen = new Set<string>();
  const newestFirst = [...investigations].sort((a, b) => Date.parse(b.at) - Date.parse(a.at));
  return newestFirst
    .flatMap((investigation) =>
      (investigation.nextSteps ?? []).flatMap((step, index) => {
        if (!isOpenNextStep(step)) return [];
        const key = normaliseTitle(step.title);
        if (seen.has(key)) return [];
        seen.add(key);
        return [{ investigation, step, index }];
      }),
    )
    .slice(0, limit);
}

/**
 * A to-do from a check: what to do, why and how to check it (behind a disclosure), and a reply thread.
 * embedded drops the card frame for use inside an inbox row or a check's page.
 */
export function InvestigationNextStepCard({
  investigation,
  step,
  api,
  mutate,
  onSource,
  embedded = false,
}: {
  investigation: Investigation;
  step: InvestigationNextStep;
  api: Api;
  mutate?: Mutate;
  onSource?: (id: string) => void;
  embedded?: boolean;
}) {
  const titleId = useId();
  const close = useCloseItem();
  const open = isOpenNextStep(step);
  const stepId = step.id;
  const base = stepId
    ? `/investigations/${encodeURIComponent(investigation.id)}/followups/${encodeURIComponent(stepId)}`
    : "";
  const paths = closePaths("todo", investigation.id, stepId);
  // Only to-dos the server has given an id can be closed; older records stay read-only. Done sends no note: it is
  // not something you wrote, so it must never show up later as "Your note: Done".
  const done = mutate && open && stepId ? () => void close("todo", paths, "done") : null;
  const reopen = mutate && !open && stepId ? () => void mutate(`${base}/reopen`, {}, "Back on your list.") : null;
  const source = onSource && (
    <Button variant="link" size="sm" onClick={() => onSource(investigation.id)} aria-describedby={titleId}>
      Open the check
    </Button>
  );
  return (
    <article
      className={`investigation-next-step${embedded ? " is-embedded" : ""}${open ? "" : " is-closed"}`}
      aria-labelledby={titleId}
    >
      {!embedded && (
        <div className="follow-up-meta">
          <span>
            <ListTodo size={14} aria-hidden="true" />
            To-do
            {open
              ? ""
              : ` · ${closedStatus("todo", step.status ?? "closed", step.thread, step.closedReason, step.decisionNote)}`}
          </span>
          <time dateTime={investigation.at}>{dayTime(investigation.at)}</time>
        </div>
      )}
      <h3 id={titleId} className={embedded ? "sr-only" : undefined}>
        <RichText text={step.title} />
      </h3>
      <InvestigationSummary text={step.suggestedAction} previewCharacters={400} />
      {!open && (step.closedAt || step.decisionNote) && (
        <p className="follow-up-note">
          {step.closedAt ? `Closed ${dayTime(step.closedAt)}` : "Closed"}
          {step.decisionNote && !/^(done|thanks)\.?$/i.test(step.decisionNote.trim())
            ? ` · Your note: “${step.decisionNote}”`
            : ""}
        </p>
      )}
      <details className="follow-up-details">
        <summary>Why, and how to check it</summary>
        {step.rationale && (
          <>
            <h4>Why</h4>
            <InvestigationText text={step.rationale} />
          </>
        )}
        {step.verification && (
          <>
            <h4>How to check</h4>
            <InvestigationText text={step.verification} />
          </>
        )}
        {step.uncertainty && (
          <>
            <h4>What isn't certain</h4>
            <InvestigationText text={step.uncertainty} />
          </>
        )}
        <EvidencePanel api={api} id={investigation.id} references={step.evidenceReferences} />
      </details>
      {mutate && stepId && (open || !!step.thread?.length) ? (
        <RecommendationReply
          title={plainText(step.title)}
          thread={step.thread}
          open={open}
          replyPath={`${base}/reply`}
          dismissPath={`${base}/dismiss`}
          reopenPath={`${base}/reopen`}
          api={api}
          mutate={mutate}
          target="todo"
          actionsBefore={
            done && (
              <>
                <Button size="sm" onClick={done} aria-describedby={titleId}>
                  <Check size={14} aria-hidden="true" />
                  Done
                </Button>
                <CloseButtons kind="todo" title={plainText(step.title)} paths={paths} describedBy={titleId} />
              </>
            )
          }
          actionsAfter={
            <>
              {reopen && (
                <Button variant="secondary" size="sm" onClick={reopen} aria-describedby={titleId}>
                  Reopen
                </Button>
              )}
              {source}
            </>
          }
        />
      ) : (
        (reopen || source) && (
          <div className="follow-up-actions card-action-row">
            {reopen && (
              <Button variant="secondary" size="sm" onClick={reopen} aria-describedby={titleId}>
                Reopen
              </Button>
            )}
            {source}
          </div>
        )
      )}
    </article>
  );
}
