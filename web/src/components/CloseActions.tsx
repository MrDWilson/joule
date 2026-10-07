import { useCallback, useContext, useState } from "react";
import { X } from "lucide-react";
import { Button, Modal } from "./ui";
import { AppContext } from "../context/AppContext";
import "./CloseActions.css";
import { plainError } from "../lib/errors";
import {
  closeBody,
  closedMessage,
  kindWord,
  type ClosableKind,
  type ClosePaths,
  type CloseOutcome,
} from "../lib/closing";

/**
 * Closes an item and offers Undo in the toast, which reopens it. Returns true once it has closed. Every list re-reads the
 * state straight away, so counts and badges change at once.
 */
export function useCloseItem() {
  const app = useContext(AppContext);
  return useCallback(
    async (kind: ClosableKind, paths: ClosePaths, outcome: CloseOutcome, note?: string | null) => {
      if (!app) return false;
      const { api, load, notify, reportError, setBusy } = app;
      setBusy(true);
      try {
        await api(paths.close, closeBody(outcome, note));
        await load();
        notify(closedMessage(kind, outcome), {
          label: "Undo",
          onClick: () =>
            void (async () => {
              try {
                // Undo of "applied" takes it back; everything else reopens.
                await api(
                  kind === "file" && outcome === "done" ? paths.reopen.replace(/\/reopen$/, "/unapply") : paths.reopen,
                  {},
                );
                await load();
                notify("Back on your list.");
              } catch (e) {
                reportError(plainError(e));
              }
            })(),
        });
        return true;
      } catch (e) {
        reportError(plainError(e));
        return false;
      } finally {
        setBusy(false);
      }
    },
    [app],
  );
}

/** The dismiss dialog: an optional reason, then Dismiss. A finding's dialog says that what came from it closes too. */
export function DismissDialog({
  kind,
  title,
  open,
  onOpenChange,
  onDismiss,
  openItems = 0,
}: {
  kind: ClosableKind;
  title: string;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onDismiss: (note: string) => Promise<boolean>;
  /** For a finding: how many suggestions, to-dos and file edits from it are still open. */
  openItems?: number;
}) {
  const [note, setNote] = useState("");
  const [sending, setSending] = useState(false);
  return (
    <Modal
      open={open}
      onOpenChange={(next) => {
        if (!sending) onOpenChange(next);
        if (!next) setNote("");
      }}
      title={`Dismiss this ${kindWord[kind]}?`}
      description={`“${title}”`}
    >
      <form
        onSubmit={async (e) => {
          e.preventDefault();
          setSending(true);
          const ok = await onDismiss(note);
          setSending(false);
          if (ok) {
            setNote("");
            onOpenChange(false);
          }
        }}
      >
        <p className="body-copy">
          {kind === "finding" && openItems > 0
            ? `${openItems === 1 ? "The 1 thing" : `The ${openItems} things`} from it still waiting for you close too. `
            : ""}
          Joule won't raise it again for 30 days. You can bring it back from Closed.
        </p>
        <label className="field">
          Why? (optional)
          <textarea
            maxLength={1000}
            value={note}
            disabled={sending}
            placeholder="For example: it doesn't apply to my setup"
            onChange={(e) => setNote(e.target.value)}
          />
        </label>
        <p className="muted close-dialog-hint">Your reason is passed to Joule's next check.</p>
        <div className="dialog-actions">
          <Button variant="ghost" type="button" disabled={sending} onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button type="submit" disabled={sending}>
            Dismiss
          </Button>
        </div>
      </form>
    </Modal>
  );
}

/** One click to close an item as not needed, with Undo in the toast. `label` names it for screen readers in a list row. */
export function NotNeededButton({
  kind,
  paths,
  describedBy,
  label,
  className,
}: {
  kind: ClosableKind;
  paths: ClosePaths;
  describedBy?: string;
  label?: string;
  className?: string;
}) {
  const close = useCloseItem();
  const busy = useContext(AppContext)?.busy ?? false;
  return (
    <Button
      variant="ghost"
      size="sm"
      className={`close-not-needed${className ? ` ${className}` : ""}`}
      disabled={busy}
      aria-describedby={describedBy}
      aria-label={label}
      title="Not needed"
      onClick={() => void close(kind, paths, "not_needed")}
    >
      <X size={14} aria-hidden="true" />
      <span className="close-not-needed-label">Not needed</span>
    </Button>
  );
}

/**
 * "Not needed" and "Dismiss…" for one item, beside its own main action (Done, Review, Mark as applied). Both close it and offer
 * Undo; Dismiss asks for an optional reason first. A finding has only "Dismiss finding…".
 */
export function CloseButtons({
  kind,
  title,
  paths,
  describedBy,
  openItems,
  notNeeded = kind !== "finding",
}: {
  kind: ClosableKind;
  /** Plain text, for the dialog. */
  title: string;
  paths: ClosePaths;
  /** The id of the item's title, so "Not needed" is announced with what it closes. */
  describedBy?: string;
  openItems?: number;
  notNeeded?: boolean;
}) {
  const close = useCloseItem();
  const app = useContext(AppContext);
  const [asking, setAsking] = useState(false);
  const busy = app?.busy ?? false;
  return (
    <>
      {notNeeded && <NotNeededButton kind={kind} paths={paths} describedBy={describedBy} />}
      <Button
        variant="ghost"
        size="sm"
        className="close-dismiss"
        disabled={busy}
        aria-describedby={describedBy}
        aria-haspopup="dialog"
        onClick={() => setAsking(true)}
      >
        {kind === "finding" ? "Dismiss finding…" : "Dismiss…"}
      </Button>
      <DismissDialog
        kind={kind}
        title={title}
        open={asking}
        onOpenChange={setAsking}
        openItems={openItems}
        onDismiss={(note) => close(kind, paths, "dismissed", note)}
      />
    </>
  );
}
