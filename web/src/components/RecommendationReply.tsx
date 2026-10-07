import {
  createContext,
  useContext,
  useEffect,
  useId,
  useLayoutEffect,
  useRef,
  useState,
  useSyncExternalStore,
} from "react";
import type { ReactNode } from "react";
import { createPortal } from "react-dom";
import { ArrowUp, LoaderCircle, MessageSquareReply, RotateCw, X } from "lucide-react";
import { Button, Chip, Modal } from "./ui";
import { RichText } from "./InvestigationText";
import type { Api, Mutate } from "../completion-types";
import type { ReplyMessage, ReplyOutcome, ReplyVerdict } from "../types";
import { useMediaQuery, breakpoints } from "../lib/useMediaQuery";
import { dayTime } from "../lib/time";
import { providerLabel } from "../lib/labels";
import { useCloseItem } from "./CloseActions";
import type { ClosableKind } from "../lib/closing";
import "./RecommendationReply.css";

/*
 * A reply can close the item it belongs to (the AI agreed), and the item then leaves the list. That outcome (the AI's
 * answer and anything it remembered) is shown by one host at the app root, which also owns the state refresh.
 * Everything else (an answer, a disagreement, a failed reply) stays inline in the thread.
 */
type Outcome = ReplyOutcome & { title: string; reopenPath?: string };
let shown: Outcome | null = null;
let refresh: (() => unknown) | null = null;
const listeners = new Set<() => void>();
const subscribe = (listener: () => void) => {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
};
function publish(outcome: Outcome | null) {
  shown = outcome;
  listeners.forEach((listener) => listener());
}

const verdictLabel: Record<ReplyVerdict, string> = {
  accept: "Agrees with you",
  answer: "Answer",
  disagree: "Disagrees",
  clarify: "Question for you",
  unavailable: "Couldn't answer",
  reopened: "Reopened",
};
const verdictTone: Record<ReplyVerdict, string> = {
  accept: "success",
  answer: "info",
  disagree: "warn",
  clarify: "info",
  unavailable: "neutral",
  reopened: "neutral",
};

/**
 * Lets a page hear when any reply composer inside it opens or closes: a check's page hides its own "Reply to Joule"
 * while you are writing a reply on one of its cards, so there is only ever one place to type.
 */
export const ComposerWatch = createContext<((key: string, open: boolean) => void) | null>(null);

/** What a thread is about: it decides which quick replies make sense after Joule answers. */
export type ReplyTarget = "check" | "todo" | "proposal" | "file";
const closableKind: Record<ReplyTarget, ClosableKind> = {
  check: "finding",
  todo: "todo",
  proposal: "proposal",
  file: "file",
};

/** The conversation as chat: your messages on the right, the AI's on the left with what it concluded. */
export function ReplyThread({ thread, label = "Replies" }: { thread?: ReplyMessage[]; label?: string }) {
  if (!thread?.length) return null;
  return (
    <ol className="chat" aria-label={label}>
      {thread.map((message, index) => (
        <li key={`${message.at}-${index}`} className={`chat-message chat-${message.role}`}>
          <div className="chat-meta">
            <strong>
              {message.role === "user"
                ? "You"
                : message.role === "ai"
                  ? message.provider === "Demo"
                    ? "Joule (demo)"
                    : message.provider
                      ? `Joule · ${providerLabel(message.provider)}`
                      : "Joule"
                  : "Joule"}
            </strong>
            {message.role !== "user" && message.verdict && (
              <Chip tone={verdictTone[message.verdict]}>{verdictLabel[message.verdict]}</Chip>
            )}
            <time dateTime={message.at}>{dayTime(message.at)}</time>
          </div>
          <div className="chat-bubble">
            {message.role === "user" ? (
              <p>{message.text}</p>
            ) : (
              <p>
                <RichText text={message.text} />
              </p>
            )}
            {message.action && (
              <p className="chat-action">
                <strong>Still to do:</strong> <RichText text={message.action} />
              </p>
            )}
            {message.memory && <p className="chat-memory">Remembered: “{message.memory}”</p>}
          </div>
        </li>
      ))}
    </ol>
  );
}

/** Keeps a bottom sheet above the on-screen keyboard: the distance from the layout viewport's bottom to the visual one. */
function useKeyboardInset(active: boolean) {
  const [inset, setInset] = useState(0);
  useEffect(() => {
    const vv = window.visualViewport;
    if (!active || !vv) return;
    const update = () => setInset(Math.max(0, window.innerHeight - vv.height - vv.offsetTop));
    update();
    vv.addEventListener("resize", update);
    vv.addEventListener("scroll", update);
    return () => {
      vv.removeEventListener("resize", update);
      vv.removeEventListener("scroll", update);
    };
  }, [active]);
  return inset;
}

/**
 * Reply to a suggestion, to-do, file edit or finding, as a chat. Sending asks the AI to read the reply: it may answer,
 * agree (and close the item), disagree or ask back. Nothing is ever dismissed unless you choose to: a reply the AI can't
 * read stays here with "Try again". The card's own actions go in `actionsBefore` / `actionsAfter`, in one row with Reply.
 * On phones the composer is a bottom sheet that stays above the keyboard.
 */
export function RecommendationReply({
  title,
  thread = [],
  open,
  replyPath,
  dismissPath,
  reopenPath,
  api,
  mutate,
  replyLabel = "Reply",
  actionsBefore,
  actionsAfter,
  target = "check",
  quickReplies = true,
  prominent = false,
  hideReplyButton = false,
}: {
  title: string;
  thread?: ReplyMessage[];
  open: boolean;
  replyPath: string;
  dismissPath: string;
  /** Reopens the item: the Undo after a close (from a quick reply, the composer, or a reply the AI agreed with). */
  reopenPath?: string;
  api: Api;
  mutate: Mutate;
  replyLabel?: string;
  actionsBefore?: ReactNode;
  actionsAfter?: ReactNode;
  /** What the thread is about: a to-do offers "I'll do it", the others don't. */
  target?: ReplyTarget;
  /** False once the item is applied (it waits for Joule to confirm): no quick replies then. */
  quickReplies?: boolean;
  /** The one main "Reply to Joule" of a page (a button); cards get a quiet ghost Reply. */
  prominent?: boolean;
  /** Hides the Reply button (another composer on the page is open). */
  hideReplyButton?: boolean;
}) {
  const fieldId = useId(),
    contextId = useId(),
    sheetTitleId = useId();
  const phone = useMediaQuery(breakpoints.phone);
  const [composing, setComposing] = useState(false),
    [note, setNote] = useState(""),
    [sending, setSending] = useState(false),
    [failed, setFailed] = useState<{ note: string; message: string } | null>(null),
    [acknowledged, setAcknowledged] = useState<{ at: string; text: string } | null>(null);
  const field = useRef<HTMLTextAreaElement>(null);
  const close = useCloseItem();
  const trimmed = note.trim();
  const lastMessage = thread.at(-1);
  const verdict = lastMessage?.role === "ai" ? lastMessage.verdict : null;
  // Quick replies follow Joule's last answer, and only while the item is still waiting for you.
  const ackShown = acknowledged && acknowledged.at === lastMessage?.at ? acknowledged.text : "";
  const answered =
    open &&
    quickReplies &&
    !composing &&
    !ackShown &&
    (verdict === "answer" || verdict === "clarify" || verdict === "disagree");
  const sheet = phone && composing;
  const inset = useKeyboardInset(sheet);
  const watch = useContext(ComposerWatch);
  const watchKey = useId();
  useEffect(() => {
    if (!watch) return;
    watch(watchKey, composing);
    return () => watch(watchKey, false);
  }, [watch, watchKey, composing]);

  // Grow the box with its text, up to about six lines.
  useLayoutEffect(() => {
    const el = field.current;
    if (!el) return;
    el.style.height = "auto";
    el.style.height = `${Math.min(el.scrollHeight, 160)}px`;
  }, [note, composing]);
  useEffect(() => {
    if (composing) field.current?.focus({ preventScroll: !sheet });
    if (composing && !sheet) field.current?.scrollIntoView({ block: "nearest" });
  }, [composing, sheet]);

  async function send(text = trimmed) {
    if (!text) return;
    setSending(true);
    setFailed(null);
    try {
      const outcome = await api<ReplyOutcome>(replyPath, { note: text });
      if (outcome.verdict === "unavailable") {
        // Nothing was dismissed; keep the note so "Try again" can send it as it was.
        setFailed({
          note: text,
          message: outcome.reply || "The AI couldn't read your reply just now. Nothing was dismissed.",
        });
      } else {
        setNote("");
        setComposing(false);
        if (outcome.retired || outcome.notice) publish({ ...outcome, title, reopenPath });
      }
      await refresh?.();
    } catch (e) {
      setFailed({ note: text, message: `${(e as Error).message} Nothing was dismissed.` });
    } finally {
      setSending(false);
    }
  }
  /** Closes the item with a note (and Undo in the toast when it can be reopened). */
  async function dismiss(text: string, outcome: "dismissed" | "not_needed") {
    const closed = reopenPath
      ? await close(closableKind[target], { close: dismissPath, reopen: reopenPath }, outcome, text)
      : await mutate(dismissPath, { note: text, outcome }, "Dismissed with your note.");
    if (closed) {
      setNote("");
      setComposing(false);
      setFailed(null);
    }
  }
  const lastUserNote = [...thread].reverse().find((m) => m.role === "user")?.text ?? "";
  const startReply = (prefill = "") => {
    setNote(prefill);
    setAcknowledged(null);
    setComposing(true);
  };
  const acknowledge = (text: string) => setAcknowledged({ at: lastMessage?.at ?? "", text });

  const replyButtons = open && !composing && !hideReplyButton && (
    <Button
      variant={prominent ? "secondary" : "ghost"}
      size="sm"
      className={prominent ? "reply-main" : "reply-quiet"}
      onClick={() => startReply()}
      aria-describedby={contextId}
    >
      <MessageSquareReply size={14} aria-hidden="true" />
      {thread.length ? (prominent ? "Reply to Joule again" : "Reply again") : replyLabel}
    </Button>
  );
  const hasRow = !!(actionsBefore || actionsAfter || replyButtons);

  // A reply the AI couldn't read: shown inside the composer while it is open (so it is visible in the phone sheet,
  // not behind it), otherwise in the card.
  const failedNotice = failed && (
    <div className="reply-failed" role="alert">
      <p>{failed.message}</p>
      <Button size="sm" variant="secondary" disabled={sending} onClick={() => void send(failed.note)}>
        <RotateCw size={13} aria-hidden="true" />
        Try again
      </Button>
    </div>
  );

  const composer = open && composing && (
    <form
      className={`reply-form${sheet ? " is-sheet" : ""}`}
      onSubmit={(e) => {
        e.preventDefault();
        void send();
      }}
    >
      <div className="reply-form-head">
        <label className="reply-label" htmlFor={fieldId}>
          Your reply
        </label>
        {sheet && (
          <Button
            type="button"
            variant="ghost"
            size="sm"
            className="reply-close"
            aria-label="Close reply"
            onClick={() => setComposing(false)}
          >
            <X size={18} aria-hidden="true" />
          </Button>
        )}
      </div>
      <div className="reply-field">
        <textarea
          id={fieldId}
          ref={field}
          rows={1}
          maxLength={1000}
          value={note}
          disabled={sending}
          aria-describedby={contextId}
          placeholder="Ask a question or tell Joule what it got wrong"
          onChange={(e) => setNote(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter" && (e.metaKey || e.ctrlKey)) {
              e.preventDefault();
              void send();
            }
          }}
        />
        <Button type="submit" className="reply-send" disabled={sending || !trimmed} aria-label="Send reply">
          {sending ? (
            <LoaderCircle size={16} className="spin" aria-hidden="true" />
          ) : (
            <ArrowUp size={17} aria-hidden="true" />
          )}
        </Button>
      </div>
      {!sending && failedNotice}
      {sending ? (
        <p role="status" className="reply-pending">
          Joule is reading your reply. This can take up to a minute.
        </p>
      ) : (
        <div className="reply-form-actions">
          <Button
            type="button"
            variant="ghost"
            size="sm"
            disabled={!trimmed}
            onClick={() => void dismiss(trimmed, "dismissed")}
          >
            Dismiss with this note
          </Button>
          {!sheet && (
            <Button type="button" variant="ghost" size="sm" onClick={() => setComposing(false)}>
              Cancel
            </Button>
          )}
        </div>
      )}
    </form>
  );

  return (
    <div className="recommendation-reply">
      <span id={contextId} className="sr-only">
        About “<RichText text={title} />”
      </span>
      <ReplyThread thread={thread} />
      {!composing && failedNotice}
      {answered && !failed && (
        <div className="quick-replies" role="group" aria-label="Quick replies">
          {/* "Thanks" only acknowledges: it never dismisses anything. */}
          <button
            type="button"
            className="suggestion-chip"
            onClick={() =>
              acknowledge(target === "check" ? "Noted." : "Noted. It stays here until you've dealt with it.")
            }
          >
            Thanks
          </button>
          {target === "todo" && (
            <button
              type="button"
              className="suggestion-chip"
              onClick={() => acknowledge("Kept on your list. Press Done once you've done it.")}
            >
              I'll do it
            </button>
          )}
          {verdict === "disagree" ? (
            <button type="button" className="suggestion-chip" onClick={() => startReply("I still disagree: ")}>
              Still disagree
            </button>
          ) : (
            <button type="button" className="suggestion-chip" onClick={() => startReply()}>
              {verdict === "clarify" ? "Answer Joule" : "Ask something else"}
            </button>
          )}
          {/* Talked it through and it isn't worth doing: close it here, with your last reply as the note. */}
          {reopenPath && (
            <button
              type="button"
              className="suggestion-chip"
              onClick={() => void dismiss(lastUserNote || "Not needed", "not_needed")}
            >
              {target === "check" ? "Not needed: close this finding" : "Not needed: close it"}
            </button>
          )}
        </div>
      )}
      {ackShown && open && (
        <p className="reply-ack" role="status">
          {ackShown}
        </p>
      )}
      {hasRow && (
        <div className="reply-actions card-action-row">
          {actionsBefore}
          {replyButtons}
          {actionsAfter}
        </div>
      )}
      {sheet
        ? createPortal(
            <div className="reply-sheet-layer">
              <div
                className="reply-sheet-backdrop"
                onClick={() => !sending && setComposing(false)}
                aria-hidden="true"
              />
              <div
                className="reply-sheet"
                role="dialog"
                aria-modal="true"
                aria-labelledby={sheetTitleId}
                style={{ bottom: inset }}
                onKeyDown={(e) => {
                  if (e.key === "Escape" && !sending) setComposing(false);
                }}
              >
                <p className="reply-sheet-title" id={sheetTitleId}>
                  Reply about “<RichText text={title} />”
                </p>
                <div className="reply-sheet-thread">
                  <ReplyThread thread={thread} label="Conversation" />
                </div>
                {composer}
              </div>
            </div>,
            document.body,
          )
        : composer}
    </div>
  );
}

/** Rendered once at the app root: shows the outcome of a reply that closed its item, and refreshes state after replies. */
export function ReplyOutcomeHost({ mutate, reload }: { mutate: Mutate; reload: () => unknown }) {
  useEffect(() => {
    refresh = reload;
    return () => {
      if (refresh === reload) refresh = null;
    };
  }, [reload]);
  const outcome = useSyncExternalStore(subscribe, () => shown);
  const [saved, setSaved] = useState(false);
  useEffect(() => setSaved(false), [outcome]);
  const offer = outcome?.suggestedMemory;
  const title = !outcome ? "" : outcome.verdict === "accept" ? "The AI agreed" : "Reply recorded";
  return (
    <Modal
      open={!!outcome}
      onOpenChange={(open) => {
        if (!open) publish(null);
      }}
      title={title}
      description={outcome ? `Your reply to “${outcome.title}”` : ""}
    >
      {outcome && (
        <div className="reply-outcome">
          <p className="reply-outcome-text">
            <RichText text={outcome.reply} />
          </p>
          {outcome.memory && (
            <p className="chat-memory">Added to what Joule knows about your home: “{outcome.memory}”</p>
          )}
          {outcome.retired && (
            <p className="muted">
              You'll find {outcome.findingClosed ? "both" : "it"} under Suggestions › Closed if you change your mind.
            </p>
          )}
          {outcome.notice && <p className="reply-error">{outcome.notice}</p>}
          {offer && !saved && <p className="muted">Save it so future checks know:</p>}
          {offer && !saved && <blockquote className="reply-offer">{offer}</blockquote>}
          <div className="reply-actions dialog-actions">
            {offer && !saved && (
              <Button
                onClick={async () => {
                  if (await mutate("/memory", { text: offer }, "Remembered.")) setSaved(true);
                }}
              >
                Remember this
              </Button>
            )}
            {saved && (
              <span className="reply-saved" role="status">
                Remembered.
              </span>
            )}
            {outcome.retired && outcome.reopenPath && (
              <Button
                variant="secondary"
                onClick={async () => {
                  if (await mutate(outcome.reopenPath!, {}, "Back on your list.")) publish(null);
                }}
              >
                Undo
              </Button>
            )}
            <Button variant={offer && !saved ? "ghost" : "primary"} onClick={() => publish(null)}>
              Close
            </Button>
          </div>
        </div>
      )}
    </Modal>
  );
}
