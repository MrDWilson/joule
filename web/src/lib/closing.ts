import type { InboxItem } from "./insights";

/** Anything that can be closed: a setting suggestion, a to-do, a file edit, or a check's findings as a whole. */
export type ClosableKind = "proposal" | "todo" | "file" | "finding";
/** How it closes: done (you did it), not_needed (fine, but not worth doing) or dismissed (wrong, or doesn't apply). */
export type CloseOutcome = "done" | "not_needed" | "dismissed";

export interface ClosePaths {
  /** POST { note?, outcome } closes it. */
  close: string;
  /** POST {} brings it back (the toast's Undo, and Reopen under Closed). */
  reopen: string;
}

const enc = encodeURIComponent;

/** The close and reopen endpoints for one item. Suggestions need only their own id; the rest belong to a check. */
export function closePaths(kind: ClosableKind, investigationId: string, itemId?: string): ClosePaths {
  const check = `/investigations/${enc(investigationId)}`;
  switch (kind) {
    case "proposal":
      return { close: `/proposals/${enc(itemId ?? "")}/deny`, reopen: `/proposals/${enc(itemId ?? "")}/reopen` };
    case "todo":
      return {
        close: `${check}/followups/${enc(itemId ?? "")}/dismiss`,
        reopen: `${check}/followups/${enc(itemId ?? "")}/reopen`,
      };
    case "file":
      return {
        close: `${check}/filechanges/${enc(itemId ?? "")}/dismiss`,
        reopen: `${check}/filechanges/${enc(itemId ?? "")}/reopen`,
      };
    case "finding":
      return { close: `${check}/dismiss`, reopen: `${check}/reopen` };
  }
}

/** The endpoints for a row of "Needs you"; trials are decided on their own page and have none. Old to-dos without an id can't close. */
export function inboxClosePaths(item: InboxItem): ClosePaths | null {
  switch (item.kind) {
    case "proposal":
      return closePaths("proposal", item.proposal.investigationId, item.proposal.id);
    case "file":
      return closePaths("file", item.investigation.id, item.change.id);
    case "todo":
      return item.step.id ? closePaths("todo", item.investigation.id, item.step.id) : null;
    case "trial":
      return null;
  }
}

const AGAIN = "Joule won't raise it again for 30 days.";

/** What the toast says once it has closed. */
export function closedMessage(kind: ClosableKind, outcome: CloseOutcome): string {
  if (outcome === "done") return kind === "file" ? "Marked as applied. The next check confirms it." : "Marked as done.";
  if (outcome === "not_needed") return `Closed as not needed. ${AGAIN}`;
  return kind === "finding" ? `Finding dismissed, with everything from it. ${AGAIN}` : `Dismissed. ${AGAIN}`;
}

/** The body sent to close: the note only when there is one. */
export function closeBody(outcome: CloseOutcome, note?: string | null) {
  const text = note?.trim();
  return text ? { outcome, note: text.slice(0, 1000) } : { outcome };
}

/** The item's name in the dismiss dialog: "Dismiss this to-do?". */
export const kindWord: Record<ClosableKind, string> = {
  proposal: "suggestion",
  todo: "to-do",
  file: "file edit",
  finding: "finding",
};
