import { useEffect, useState } from "react";
import { TriangleAlert } from "lucide-react";
import { Button, Chip, Modal } from "./ui";
import { LoadingBlock } from "./ui/States";
import { PlainText } from "./PlainText";
import type { ConfigFileChange } from "../types";
import { api } from "../lib/api";
import {
  applyPath,
  refreshConfigEditStatus,
  reviewPath,
  type ConfigEditReview,
  type DiffLine,
} from "../lib/configEdits";

const SIGN: Record<DiffLine["kind"], string> = { " ": " ", "-": "-", "+": "+", "…": "⋯" };
const CHANGE: Record<string, string> = { added: "added", removed: "removed", changed: "changed" };

/** The real file against the file with the edit made: masked lines, numbered, with removed and added lines marked. */
export function FileDiffView({ review }: { review: ConfigEditReview }) {
  return (
    <figure className="config-diff config-review-diff">
      <figcaption>
        <code>{review.file}</code> {review.placement ? `· ${review.placement}` : ""}
      </figcaption>
      <pre tabIndex={0} aria-label={`Changes to ${review.file}`}>
        <code>
          {review.lines.map((line, index) => (
            <span
              key={index}
              className={
                line.kind === "+"
                  ? "diff-add"
                  : line.kind === "-"
                    ? "diff-remove"
                    : line.kind === "…"
                      ? "diff-gap"
                      : "diff-same"
              }
            >
              <span className="diff-number" aria-hidden="true">
                {line.kind === "…" ? "" : (line.new ?? line.old)}
              </span>
              <span className="diff-sign" aria-hidden="true">
                {SIGN[line.kind]}
              </span>
              <span className="sr-only">{line.kind === "+" ? "Added: " : line.kind === "-" ? "Removed: " : ""}</span>
              {line.kind === "…" ? "" : line.text || " "}
              {"\n"}
            </span>
          ))}
        </code>
      </pre>
    </figure>
  );
}

/**
 * "Review and apply": the diff Joule would write, what happens next, and Apply. When Joule won't make the edit (the lines it
 * replaces aren't in the file, the result wouldn't be valid YAML…) it says why and offers the copy-it-yourself path instead.
 */
export function ConfigEditReviewDialog({
  open,
  onOpenChange,
  investigationId,
  change,
  onApplied,
  onCopy,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  investigationId: string;
  change: ConfigFileChange;
  onApplied: () => void;
  onCopy: () => void;
}) {
  const [review, setReview] = useState<ConfigEditReview | null>(null);
  const [error, setError] = useState("");
  const [applying, setApplying] = useState(false);
  useEffect(() => {
    if (!open) return;
    let live = true;
    setReview(null);
    setError("");
    api<ConfigEditReview>(reviewPath(investigationId, change.id))
      .then((r) => live && setReview(r))
      .catch((e: Error) => live && setError(e.message));
    return () => {
      live = false;
    };
  }, [open, investigationId, change.id]);

  async function apply() {
    if (!review?.hash) return;
    setApplying(true);
    setError("");
    try {
      await api(applyPath(investigationId, change.id), { hash: review.hash });
      refreshConfigEditStatus();
      onApplied();
      onOpenChange(false);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setApplying(false);
    }
  }

  const file = review?.file ?? change.file;
  return (
    <Modal
      open={open}
      onOpenChange={(v) => !applying && onOpenChange(v)}
      title={`Review the edit to ${file}`}
      description="This is your real file with the edit made. Nothing changes until you apply it."
      footer={
        <>
          <Button variant="secondary" onClick={() => onOpenChange(false)} disabled={applying}>
            Cancel
          </Button>
          {review?.problem ? (
            <Button
              variant="secondary"
              onClick={() => {
                onCopy();
                onOpenChange(false);
              }}
            >
              Copy the snippet instead
            </Button>
          ) : (
            <Button onClick={() => void apply()} disabled={!review?.canApply || applying}>
              {applying ? "Applying…" : `Apply to ${file}`}
            </Button>
          )}
        </>
      }
    >
      <div className="config-review">
        {!review && !error && <LoadingBlock label={`Reading ${file}…`} />}
        {error && (
          <p role="alert" className="config-review-alert">
            <TriangleAlert size={14} aria-hidden="true" />
            <PlainText text={error} />
          </p>
        )}
        {review?.problem && (
          <p role="status" className="config-review-alert">
            <TriangleAlert size={14} aria-hidden="true" />
            <span>
              <strong>Joule won't make this edit. </strong>
              <PlainText text={review.problem} />
            </span>
          </p>
        )}
        {review && !review.problem && !review.canApply && review.reason && (
          <p role="status" className="config-review-alert">
            <TriangleAlert size={14} aria-hidden="true" />
            <PlainText text={review.reason} />
          </p>
        )}
        {review && review.keys.length > 0 && (
          <ul className="config-review-keys" aria-label="Settings this edit changes">
            {review.keys.map((k) => (
              <li key={k.key}>
                <code>{k.name}</code>
                <Chip tone={k.change === "removed" ? "warn" : "info"}>{CHANGE[k.change] ?? k.change}</Chip>
              </li>
            ))}
          </ul>
        )}
        {review && review.lines.length > 0 && <FileDiffView review={review} />}
        {review && <p className="config-change-note">{review.note}</p>}
        {review && !review.problem && review.canApply && (
          <div className="config-review-steps">
            <h3>When you apply it</h3>
            <ol>
              <li>Joule saves a copy of {file} exactly as it is now.</li>
              <li>It writes only these lines, and checks the file is still valid and nothing else changed.</li>
              <li>
                Predbat notices the change within seconds and restarts with it. Joule watches it come back and reads its
                log for errors.
              </li>
              <li>If Predbat has a problem, Joule puts the copy back by itself. You can restore it any time too.</li>
            </ol>
          </div>
        )}
      </div>
    </Modal>
  );
}
