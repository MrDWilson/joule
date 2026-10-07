import { ArrowRight, FileCode2, FlaskConical, ListTodo, SlidersHorizontal } from "lucide-react";
import type { LucideIcon } from "lucide-react";
import { Button, ButtonLink } from "../ui";
import { PlainText } from "../PlainText";
import { plainText } from "../../lib/sanitize";
import { inboxClosePaths } from "../../lib/closing";
import { NotNeededButton } from "../CloseActions";
import { trialHeading, type InboxItem } from "../../lib/insights";
import { useMediaQuery, breakpoints } from "../../lib/useMediaQuery";
import type { Proposal, Revision, Setting } from "../../types";

// The same kinds, words and icons as Insights' "Needs you" (components/insights/NeedsYou.tsx), kept here so Today doesn't
// depend on the Insights components.
const KIND: Record<InboxItem["kind"], { label: string; icon: LucideIcon }> = {
  proposal: { label: "Setting change", icon: SlidersHorizontal },
  file: { label: "File edit", icon: FileCode2 },
  todo: { label: "To-do", icon: ListTodo },
  trial: { label: "Trial ready", icon: FlaskConical },
};

/** The row's title: the suggestion, the file edit's summary, the to-do or the trial as its friendly setting diff. */
export function needTitle(item: InboxItem, revisions?: Revision[], settings?: Setting[]) {
  switch (item.kind) {
    case "proposal":
      return item.proposal.title;
    case "file":
      return item.change.summary;
    case "todo":
      return item.step.title;
    case "trial":
      return trialHeading(item.experiment, revisions, settings);
  }
}

/**
 * "Needs you (n)": the same list as Insights (lib/insights needsYou: setting changes, file edits, to-dos and trials due a
 * decision, by money at stake), one compact row each with its own action, directly under the hero. Hidden when nothing is
 * waiting. On a phone only the first row shows, with the rest counted.
 */
export function NeedsYou({
  items,
  revisions,
  settings,
  onReview,
  onOpen,
}: {
  items: InboxItem[];
  revisions?: Revision[];
  settings?: Setting[];
  onReview: (p: Proposal) => void;
  onOpen: (investigationId: string) => void;
}) {
  const phone = useMediaQuery(breakpoints.phone);
  if (!items.length) return null;
  const shown = items.slice(0, phone ? 1 : 3);
  const more = items.length - shown.length;
  return (
    <section className="needs-you" aria-labelledby="needs-heading">
      <div className="needs-head">
        <h2 id="needs-heading">
          Needs you <span className="needs-count">{items.length}</span>
        </h2>
        <a className="text-link" href="#/insights/suggestions">
          {more > 0 ? `See all ${items.length}` : "Open Insights"} <ArrowRight size={14} aria-hidden="true" />
        </a>
      </div>
      <ul>
        {shown.map((item) => {
          const { label, icon: Icon } = KIND[item.kind];
          const title = needTitle(item, revisions, settings);
          const verb = item.kind === "proposal" ? "Review" : item.kind === "trial" ? "Decide" : "Open";
          const closable = inboxClosePaths(item);
          return (
            <li key={item.key} className={`needs-row needs-${item.kind}`} data-key={item.key}>
              <span className="needs-kind">
                <Icon size={15} aria-hidden="true" />
                {label}
              </span>
              <span className="needs-title">
                <PlainText text={title} />
              </span>
              <span className="needs-actions">
                {item.kind === "trial" ? (
                  <ButtonLink
                    size="sm"
                    variant="secondary"
                    href="#/insights/experiments"
                    aria-label={`${verb}: ${plainText(title)}`}
                  >
                    {verb}
                  </ButtonLink>
                ) : (
                  <Button
                    size="sm"
                    variant={item.kind === "proposal" ? "primary" : "secondary"}
                    onClick={() => (item.kind === "proposal" ? onReview(item.proposal) : onOpen(item.investigation.id))}
                    aria-label={`${verb}: ${plainText(title)}`}
                  >
                    {verb}
                  </Button>
                )}
                {/* Closes it here, with Undo in the toast; the count above drops at once. */}
                {closable && (
                  <NotNeededButton
                    kind={item.kind === "proposal" ? "proposal" : item.kind === "file" ? "file" : "todo"}
                    paths={closable}
                    label={`Not needed: ${plainText(title)}`}
                    className="needs-not-needed"
                  />
                )}
              </span>
            </li>
          );
        })}
      </ul>
    </section>
  );
}
