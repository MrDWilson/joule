import { useId } from "react";
import { ArrowRight, FileCode2, FlaskConical, ListTodo, Sparkles, SlidersHorizontal } from "lucide-react";
import type { LucideIcon } from "lucide-react";
import { useApp } from "../context/AppContext";
import { Button, ButtonLink, Chip, Disclosure, Segmented } from "../components/ui";
import { InvestigationNextStepCard } from "../components/InvestigationNextSteps";
import { ConfigFileChangeCard } from "../components/ConfigFileChangeCard";
import { RecommendationReply } from "../components/RecommendationReply";
import { CloseButtons } from "../components/CloseActions";
import { RichText } from "../components/InvestigationText";
import { ExperimentCard } from "../components/ExperimentCard";
import {
  changeView,
  closedItems,
  headlineOf,
  isStale,
  needsYou,
  waitingToConfirm,
  savingText,
  shorten,
  staleKeys,
  suggestedByWho,
  type ClosedItem,
} from "../lib/insights";
import { dayTime } from "../lib/time";
import { buildHash, navigate } from "../lib/router";
import { plainText } from "../lib/sanitize";
import { closePaths } from "../lib/closing";
import type { Change, Proposal, Setting } from "../types";
import "./insights.css";

/** The changes as a friendly diff: "Charge rate scaling 100% → 67%", with the setting's key in small mono. */
export function FriendlyDiff({ changes, settings }: { changes: Change[]; settings: Setting[] }) {
  return (
    <ul className="friendly-diff">
      {changes.map((c) => {
        const v = changeView(c, settings);
        return (
          <li key={c.key}>
            <span className="friendly-diff-name">
              {v.name}
              <code>{v.key}</code>
            </span>
            <span className="friendly-diff-values">
              <span className="diff-before">{v.before}</span>
              <ArrowRight size={14} aria-label="to" />
              <strong>{v.after}</strong>
            </span>
          </li>
        );
      })}
    </ul>
  );
}

/**
 * One setting change the AI suggests: the change first, the saving honestly, who suggested it, and its reply thread.
 * Same header and footer as the file-edit and to-do cards: kind and date on top; Review, Reply, then Open the check.
 */
export function RecommendationCard({ proposal: p, onSource }: { proposal: Proposal; onSource?: (id: string) => void }) {
  const { api, mutate, data, reviewProposal } = useApp();
  const titleId = useId();
  const settings = data.state.settings;
  const stale = isStale(p);
  const saving = savingText(p);
  const pending = p.status === "Pending";
  return (
    <article className="recommendation suggestion-card" aria-labelledby={titleId}>
      <div className="suggestion-top">
        <span className="suggestion-kind">
          <SlidersHorizontal size={14} aria-hidden="true" />
          Setting change ·{" "}
          {pending ? suggestedByWho(p) : (closedItems({ proposals: [p], investigations: [] })[0]?.status ?? p.status)}
        </span>
        <time dateTime={p.createdAt}>{dayTime(p.createdAt)}</time>
      </div>
      {pending && stale && (
        <p className="suggestion-stale">
          <Chip tone="warn">
            {staleKeys(p).length === 1
              ? "This setting changed since: re-check"
              : "These settings changed since: re-check"}
          </Chip>
        </p>
      )}
      <h3 id={titleId}>
        <RichText text={p.title} />
      </h3>
      <FriendlyDiff changes={p.changes} settings={settings} />
      {p.summary && (
        <p className="suggestion-summary">
          <RichText text={p.summary} />
        </p>
      )}
      <p className="suggestion-saving">
        <strong>{saving.headline}</strong>
        {saving.reason && <span> · {plainText(saving.reason)}</span>}
      </p>
      <RecommendationReply
        title={plainText(p.title)}
        thread={p.thread}
        open={pending}
        replyPath={`/proposals/${p.id}/reply`}
        dismissPath={`/proposals/${p.id}/deny`}
        reopenPath={`/proposals/${p.id}/reopen`}
        api={api}
        mutate={mutate}
        target="proposal"
        actionsBefore={
          pending && (
            <>
              <Button size="sm" onClick={() => reviewProposal(p)} aria-describedby={titleId}>
                Review
              </Button>
              <CloseButtons
                kind="proposal"
                title={plainText(p.title)}
                paths={closePaths("proposal", p.investigationId, p.id)}
                describedBy={titleId}
              />
            </>
          )
        }
        actionsAfter={
          onSource &&
          p.investigationId && (
            <Button variant="link" size="sm" onClick={() => onSource(p.investigationId)} aria-describedby={titleId}>
              Open the check
            </Button>
          )
        }
      />
    </article>
  );
}

const CLOSED_ICON: Record<ClosedItem["kind"], LucideIcon> = {
  proposal: SlidersHorizontal,
  file: FileCode2,
  todo: ListTodo,
  finding: Sparkles,
};
const CLOSED_KIND: Record<ClosedItem["kind"], string> = {
  proposal: "Setting change",
  file: "File edit",
  todo: "To-do",
  finding: "Finding",
};

function ClosedRow({ item }: { item: ClosedItem }) {
  const { mutate } = useApp();
  const titleId = useId();
  const Icon = CLOSED_ICON[item.kind];
  const title =
    item.kind === "proposal"
      ? item.proposal.title
      : item.kind === "file"
        ? item.change.summary
        : item.kind === "finding"
          ? headlineOf(item.investigation)
          : item.step.title;
  const note =
    item.kind === "proposal"
      ? item.proposal.decisionNote
      : item.kind === "file"
        ? item.change.decisionNote
        : item.kind === "finding"
          ? item.investigation.decisionNote
          : item.step.decisionNote;
  // "Done" or "Thanks" is a button's word, not something you wrote: no "Your note" for it.
  const ownNote = note && !/^(done|thanks|not needed)\.?$/i.test(note.trim()) ? note : null;
  let reopen: string | null = null;
  // Declined, done in Predbat, or applied and then undone: each can go back on your list.
  if (item.kind === "proposal" && ["Denied", "Done", "Reverted"].includes(item.proposal.status))
    reopen = `/proposals/${item.proposal.id}/reopen`;
  if (item.kind === "file" && item.change.status !== "verified")
    reopen = `/investigations/${encodeURIComponent(item.investigation.id)}/filechanges/${encodeURIComponent(item.change.id)}/reopen`;
  if (item.kind === "todo" && item.step.id)
    reopen = `/investigations/${encodeURIComponent(item.investigation.id)}/followups/${encodeURIComponent(item.step.id)}/reopen`;
  if (item.kind === "finding") reopen = closePaths("finding", item.investigation.id).reopen;
  const investigationId = item.kind === "proposal" ? item.proposal.investigationId : item.investigation.id;
  return (
    <li className="closed-row">
      <span className="inbox-icon" aria-hidden="true">
        <Icon size={16} />
      </span>
      <div className="closed-copy">
        <p className="inbox-kind">
          {CLOSED_KIND[item.kind]} · {item.status} · {dayTime(item.at)}
        </p>
        <h3 id={titleId}>
          <RichText text={title} />
        </h3>
        {ownNote && <p className="closed-note">Your note: “{ownNote}”</p>}
        {item.lastAi && (
          <p className="closed-ai">
            <strong>Joule:</strong> <RichText text={shorten(item.lastAi.text, 220)} />
          </p>
        )}
      </div>
      <div className="closed-actions">
        {reopen && (
          <Button
            variant="secondary"
            size="sm"
            aria-describedby={titleId}
            onClick={() => void mutate(reopen!, {}, "Back on your list.")}
          >
            Reopen
          </Button>
        )}
        {investigationId && (
          <Button
            variant="ghost"
            size="sm"
            aria-describedby={titleId}
            onClick={() => navigate(buildHash("insights", "", { id: investigationId }))}
          >
            Open the check
          </Button>
        )}
      </div>
    </li>
  );
}

/** Suggestions: everything waiting for you in full (setting changes, file edits, to-dos, trials due), and what closed. */
export default function RecommendationsPage() {
  const { api, mutate, data, route, load } = useApp();
  const s = data.state;
  const view = route.query.get("view") === "closed" ? "closed" : "open";
  const items = needsYou(s);
  const confirming = waitingToConfirm(s);
  const closed = closedItems(s);
  // Items Joule closed on its own fold away; what you decided stays in view.
  const retiredClosed = closed.filter(
      (c) =>
        c.status === "No longer needed" ||
        c.status === "Replaced by a newer suggestion" ||
        c.status === "About Joule's own connection, not your system",
    ),
    decidedClosed = closed.filter((c) => !retiredClosed.includes(c));
  const declined = closed.filter((c) => c.kind === "proposal" && c.proposal.status === "Denied").length;
  const openCheck = (id: string) => navigate(buildHash("insights", "", { id }));
  const runtimeWritesBlocked = !data.connection.demo && !data.connection.writesEnabled;
  const proposals = items.filter((i) => i.kind === "proposal");
  const rest = items.filter((i) => i.kind !== "proposal");
  return (
    <div className="insights-page suggestions-page">
      <div className="suggestions-toolbar">
        <Segmented
          label="Show"
          value={view}
          onChange={(v) =>
            navigate(v === "closed" ? "#/insights/suggestions?view=closed" : "#/insights/suggestions", {
              replace: true,
            })
          }
          options={[
            { value: "open", label: `Waiting (${items.length + confirming.length})` },
            { value: "closed", label: `Closed (${closed.length})` },
          ]}
        />
        {view === "open" && runtimeWritesBlocked && proposals.length > 0 && (
          <p className="suggestions-note">
            This install is read-only: you make each change in Predbat yourself, then mark it done.
          </p>
        )}
      </div>
      {view === "open" ? (
        items.length || confirming.length ? (
          <>
            {items.length > 0 && (
              <section aria-label="Waiting for you" className="suggestions-group">
                <div className="suggestion-list">
                  {[...proposals, ...rest].map((i) =>
                    i.kind === "proposal" ? (
                      <RecommendationCard key={i.key} proposal={i.proposal} onSource={openCheck} />
                    ) : i.kind === "file" ? (
                      <ConfigFileChangeCard
                        key={i.key}
                        investigation={i.investigation}
                        change={i.change}
                        api={api}
                        mutate={mutate}
                        onSource={openCheck}
                      />
                    ) : i.kind === "todo" ? (
                      <InvestigationNextStepCard
                        key={i.key}
                        investigation={i.investigation}
                        step={i.step}
                        api={api}
                        mutate={mutate}
                        onSource={openCheck}
                      />
                    ) : i.kind === "trial" ? (
                      <ExperimentCard
                        key={i.key}
                        experiment={i.experiment}
                        revision={s.revision}
                        api={api}
                        onChanged={load}
                        runtimeWritesBlocked={runtimeWritesBlocked}
                      />
                    ) : null,
                  )}
                </div>
              </section>
            )}
            {confirming.length > 0 && (
              <section aria-labelledby="confirming-heading" className="suggestions-group">
                <h2 id="confirming-heading" className="suggestions-group-title">
                  Waiting for the next check
                  <span>You've made these; Joule confirms they took effect.</span>
                </h2>
                <div className="suggestion-list">
                  {confirming.map((i) => (
                    <ConfigFileChangeCard
                      key={i.key}
                      investigation={i.investigation}
                      change={i.change}
                      api={api}
                      mutate={mutate}
                      onSource={openCheck}
                    />
                  ))}
                </div>
              </section>
            )}
          </>
        ) : (
          <p className="inbox-empty">
            <strong>Nothing waiting for you.</strong>
            {closed.length ? (
              <>
                {" "}
                {declined ? `${declined} earlier ${declined === 1 ? "suggestion" : "suggestions"} declined · ` : ""}
                <a className="text-link" href="#/insights/suggestions?view=closed">
                  See what closed
                </a>
              </>
            ) : (
              " Joule will flag anything worth changing."
            )}
          </p>
        )
      ) : closed.length ? (
        <>
          {decidedClosed.length > 0 && (
            <ul className="closed-list" aria-label="Closed">
              {decidedClosed.map((item) => (
                <ClosedRow key={item.key} item={item} />
              ))}
            </ul>
          )}
          {retiredClosed.length > 0 && (
            <Disclosure summary="No longer needed" count={retiredClosed.length} className="closed-retired">
              <p className="muted">Joule closed these itself: later checks found they weren't needed.</p>
              <ul className="closed-list" aria-label="No longer needed">
                {retiredClosed.map((item) => (
                  <ClosedRow key={item.key} item={item} />
                ))}
              </ul>
            </Disclosure>
          )}
        </>
      ) : (
        <p className="inbox-empty">
          <strong>Nothing closed yet.</strong> Suggestions, to-dos and file edits you finish with appear here.
        </p>
      )}
      {view === "open" && (
        <p className="suggestions-footer">
          <FlaskConical size={14} aria-hidden="true" /> Every setting change you apply is tracked as a trial.{" "}
          <ButtonLink variant="link" href="#/insights/experiments">
            See trials
          </ButtonLink>
        </p>
      )}
    </div>
  );
}
