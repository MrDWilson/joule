import { useId, useState, type ReactNode } from "react";
import { ArrowRight, ChevronDown, FileCode2, FlaskConical, ListTodo, SlidersHorizontal } from "lucide-react";
import type { LucideIcon } from "lucide-react";
import { useApp } from "../../context/AppContext";
import { Button, ButtonLink, Chip } from "../ui";
import { RichText } from "../InvestigationText";
import { InvestigationNextStepCard } from "../InvestigationNextSteps";
import { ConfigFileChangeCard } from "../ConfigFileChangeCard";
import {
  changeView,
  impactChipFor,
  isStale,
  needsYou,
  waitingToConfirm,
  savingText,
  shorten,
  trialView,
  type InboxItem,
} from "../../lib/insights";
import { dayTime } from "../../lib/time";
import { buildHash, navigate } from "../../lib/router";
import { plainText } from "../../lib/sanitize";

const KIND: Record<InboxItem["kind"], { label: string; icon: LucideIcon }> = {
  proposal: { label: "Setting change", icon: SlidersHorizontal },
  file: { label: "File edit", icon: FileCode2 },
  todo: { label: "To-do", icon: ListTodo },
  trial: { label: "Trial ready", icon: FlaskConical },
};

function titleOf(item: InboxItem) {
  switch (item.kind) {
    case "proposal":
      return item.proposal.title;
    case "file":
      return item.change.summary;
    case "todo":
      return item.step.title;
    case "trial":
      return item.experiment.title;
  }
}

/** One thing waiting for you: what kind, what it is, one line of detail and the next action. */
function InboxRow({ item }: { item: InboxItem }) {
  const { data, reviewProposal, api, mutate } = useApp();
  const [open, setOpen] = useState(false);
  const bodyId = useId(),
    titleId = useId();
  const { label, icon: Icon } = KIND[item.kind];
  const settings = data.state.settings;
  let detail: ReactNode = null;
  let meta = "";
  let chip: { label: string; tone: string } | null = null;
  if (item.kind === "proposal") {
    const p = item.proposal;
    const first = p.changes[0] ? changeView(p.changes[0], settings) : null;
    detail = first ? (
      <span className="inbox-diff">
        {first.name} <span className="diff-before">{first.before}</span>
        <ArrowRight size={13} aria-hidden="true" /> <strong>{first.after}</strong>
        {p.changes.length > 1 && <span className="muted"> and {p.changes.length - 1} more</span>}
      </span>
    ) : null;
    meta = savingText(p).headline;
    if (isStale(p)) chip = { label: "Setting changed since: re-check", tone: "warn" };
  } else if (item.kind === "file") {
    detail = (
      <span>
        <code className="inline-code">{item.change.file}</code> ·{" "}
        <RichText text={shorten(plainText(item.change.location), 110)} />
      </span>
    );
    meta =
      item.change.status === "applied"
        ? "You applied this · Joule confirms at the next check"
        : `From the check ${dayTime(item.investigation.at)}`;
    chip = impactChipFor(item.investigation);
  } else if (item.kind === "todo") {
    detail = <RichText text={shorten(item.step.suggestedAction, 140)} />;
    meta = `From the check ${dayTime(item.investigation.at)}`;
    chip = impactChipFor(item.investigation);
  } else {
    const view = trialView(item.experiment);
    detail = view.status;
    meta = `Review was due ${dayTime(item.experiment.reviewAt)}`;
  }
  const expandable = item.kind === "file" || item.kind === "todo";
  return (
    <li className={`inbox-row inbox-${item.kind}${open ? " is-open" : ""}`}>
      <div className="inbox-main">
        <span className="inbox-icon" aria-hidden="true">
          <Icon size={17} />
        </span>
        <div className="inbox-copy">
          <p className="inbox-kind">
            {label}
            {chip && <Chip tone={chip.tone}>{chip.label}</Chip>}
          </p>
          <h3 id={titleId}>
            <RichText text={titleOf(item)} />
          </h3>
          {detail && <p className="inbox-detail">{detail}</p>}
          {meta && <p className="inbox-meta">{meta}</p>}
        </div>
        <div className="inbox-action">
          {item.kind === "proposal" && (
            <Button size="sm" onClick={() => reviewProposal(item.proposal)} aria-describedby={titleId}>
              Review
            </Button>
          )}
          {expandable && (
            <Button
              size="sm"
              variant="secondary"
              aria-expanded={open}
              aria-controls={bodyId}
              aria-describedby={titleId}
              onClick={() => setOpen(!open)}
            >
              {open ? "Close" : "Open"}
              <ChevronDown size={14} aria-hidden="true" className="inbox-chevron" />
            </Button>
          )}
          {item.kind === "trial" && (
            <ButtonLink size="sm" variant="secondary" href="#/insights/experiments" aria-describedby={titleId}>
              Decide
            </ButtonLink>
          )}
        </div>
      </div>
      {expandable && open && (
        <div className="inbox-body" id={bodyId}>
          {item.kind === "file" ? (
            <ConfigFileChangeCard
              investigation={item.investigation}
              change={item.change}
              api={api}
              mutate={mutate}
              embedded
              onSource={(id) => navigate(buildHash("insights", "", { id }))}
            />
          ) : (
            <InvestigationNextStepCard
              investigation={item.investigation}
              step={item.step}
              api={api}
              mutate={mutate}
              embedded
              onSource={(id) => navigate(buildHash("insights", "", { id }))}
            />
          )}
        </div>
      )}
    </li>
  );
}

/**
 * "Needs you": every setting change, file edit, to-do and trial waiting for a decision, by money at stake.
 * limit shows the first few with a link to the rest; the empty state is one quiet line.
 */
export function NeedsYou({ limit, closedCount = 0 }: { limit?: number; closedCount?: number }) {
  const { data } = useApp();
  const items = needsYou(data.state);
  const confirming = waitingToConfirm(data.state).length;
  const shown = limit ? items.slice(0, limit) : items;
  const headingId = useId();
  return (
    <section className="insights-section insights-needs" aria-labelledby={headingId}>
      <div className="insights-section-head">
        <h2 id={headingId}>
          Needs you
          {items.length > 0 && <span className="count-pill">{items.length}</span>}
        </h2>
        {limit && items.length > limit && (
          <a className="text-link" href="#/insights/suggestions">
            See all {items.length}
          </a>
        )}
      </div>
      {items.length ? (
        <ul className="inbox" aria-label="Waiting for you">
          {shown.map((item) => (
            <InboxRow key={item.key} item={item} />
          ))}
        </ul>
      ) : (
        <p className="inbox-empty">
          <strong>Nothing needs you right now.</strong> Joule will flag anything worth changing.
          {closedCount > 0 && (
            <>
              {" "}
              <a className="text-link" href="#/insights/suggestions?view=closed">
                {closedCount} earlier {closedCount === 1 ? "item" : "items"}
              </a>
            </>
          )}
        </p>
      )}
      {confirming > 0 && (
        <p className="inbox-confirming">
          <a className="text-link" href="#/insights/suggestions">
            {confirming === 1 ? "1 file edit you made" : `${confirming} file edits you made`}
          </a>{" "}
          · Joule confirms {confirming === 1 ? "it" : "them"} at the next check
        </p>
      )}
    </section>
  );
}
