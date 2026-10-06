import { useId, useState } from "react";
import { CalendarPlus, NotebookPen } from "lucide-react";
import { useApp } from "../context/AppContext";
import { Button, Chip, Disclosure, Modal } from "./ui";
import { OverflowMenu } from "./setup/OverflowMenu";
import { RichText } from "./InvestigationText";
import type { Api } from "../completion-types";
import type { Experiment } from "../types";
import { trialDecisionText, trialHeading, trialView } from "../lib/insights";
import { gbp, kwh, MINUS } from "../lib/format";
import { dayLabel, dayTime } from "../lib/time";

/** Who made the change, in words. */
function sourceText(source: string) {
  if (/approved/i.test(source)) return "You approved this";
  if (/^auto/i.test(source)) return "Joule changed this within your limits";
  if (/predbat|outside/i.test(source)) return "Changed in Predbat";
  if (/demo/i.test(source)) return "Demo change";
  if (/^you$/i.test(source)) return "You changed this in Joule";
  return source;
}

/** "£1.20 → £0.95 a day" with the better side clear; a dash when a side wasn't measured. */
function Figure({
  label,
  before,
  after,
  format,
}: {
  label: string;
  before: number | null;
  after: number | null;
  format: (n: number) => string;
}) {
  if (before == null && after == null) return null;
  const delta = before != null && after != null ? after - before : null;
  return (
    <div className="trial-figure">
      <dt>{label}</dt>
      <dd>
        <span className="diff-before">{before == null ? "—" : format(before)}</span> →{" "}
        <strong>{after == null ? "—" : format(after)}</strong>
        {delta != null && Math.abs(delta) > 1e-9 && (
          <span className={delta < 0 ? "trial-better" : "trial-worse"}>
            {" "}
            ({delta < 0 ? MINUS : "+"}
            {format(Math.abs(delta))})
          </span>
        )}
      </dd>
    </div>
  );
}

/**
 * A trial of one change: the change itself first, one status line with progress, the before-and-after figures when
 * there are any, what makes the comparison shaky as chips, and the method behind "How we compare".
 * Actions: Keep it, Undo, and a ⋯ menu (another week, add a note); each goes through the app's mutate, with a toast.
 */
export function ExperimentCard({
  experiment: e,
  revision,
  runtimeWritesBlocked,
}: {
  experiment: Experiment;
  revision: number;
  runtimeWritesBlocked: boolean;
  /** Kept for older callers; decisions go through the app's mutate. */
  api?: Api;
  onChanged?: () => Promise<void>;
}) {
  const { mutate, busy, data } = useApp();
  const titleId = useId();
  const [dialog, setDialog] = useState<"" | "revert" | "notes">("");
  const [notes, setNotes] = useState("");
  const view = trialView(e);
  const last = [...(e.decisions ?? [])].reverse().find((d) => d.decision !== "notes");
  const canUndo = !runtimeWritesBlocked && e.revertEligible;
  const undoReason = runtimeWritesBlocked
    ? "Live writes are off: undo it in Predbat yourself"
    : !e.revertEligible
      ? "Joule can't undo this one"
      : "";
  const decide = (decision: string, message: string, extra: { notes?: string; extendDays?: number } = {}) =>
    mutate(
      `/experiments/${e.id}/decision`,
      { decision, notes: extra.notes ?? "", extendDays: extra.extendDays ?? 7, revision },
      message,
    );
  const haveFigures =
    e.baselineCostGbpPerDay != null ||
    e.currentCostGbpPerDay != null ||
    e.baselineError != null ||
    e.currentError != null;
  const nextWeek = new Date(Math.max(Date.parse(e.reviewAt), Date.now()) + 7 * 86400000);
  // The same friendly diff the suggestion showed ("House load scaling 108% → 100%"), not the server's raw values.
  const title = trialHeading(e, data.state.revisions, data.state.settings);
  const started = dayLabel(e.startedAt, { relative: true });
  const lastText = last ? trialDecisionText(last) : null;
  const userNotes = (e.decisions ?? []).filter((d) => d.notes && trialDecisionText(d).byYou);

  // A closed trial is history: one row with what changed and how it ended; the detail folds away.
  if (view.closed)
    return (
      <article className="trial-row" aria-labelledby={titleId}>
        <div className="trial-row-main">
          <h3 id={titleId} className="trial-row-title">
            <RichText text={title} />
          </h3>
          <p className="trial-row-meta">
            {sourceText(e.source)} · {started}
            {lastText && (
              <>
                {" · "}
                {lastText.byYou ? `${lastText.text} ${dayTime(last!.at)}` : lastText.text}
              </>
            )}
          </p>
        </div>
        <Chip tone={view.tone}>{view.status}</Chip>
        <Disclosure summary="Details" className="trial-row-details">
          <p className="body-copy">
            <RichText text={e.result} />
          </p>
          {userNotes.length > 0 && (
            <ul className="evidence">
              {userNotes.map((d, i) => (
                <li key={i}>
                  {dayTime(d.at)}: {d.notes}
                </li>
              ))}
            </ul>
          )}
        </Disclosure>
      </article>
    );

  return (
    <article className={`trial-card${view.closed ? " is-closed" : ""}`} aria-labelledby={titleId}>
      <div className="trial-top">
        <Chip tone={view.tone} dot={!view.closed}>
          {view.closed ? view.status : "Trial running"}
        </Chip>
        <span className="trial-source">
          {sourceText(e.source)} · {started}
        </span>
      </div>
      <h3 id={titleId} className="trial-title">
        <RichText text={title} />
      </h3>
      {view.closed ? (
        <p className="trial-status">{lastText ? `${lastText.text} · ${dayTime(last!.at)}` : `${view.status}.`}</p>
      ) : (
        <div className="trial-progress">
          <p className="trial-status">{view.status}</p>
          {view.progress != null && (
            <div
              className="progress-bar"
              role="progressbar"
              aria-label="Trial progress"
              aria-valuemin={0}
              aria-valuemax={100}
              aria-valuenow={Math.round(view.progress * 100)}
            >
              <span style={{ width: `${Math.round(view.progress * 100)}%` }} />
            </div>
          )}
        </div>
      )}
      {haveFigures ? (
        <dl className="trial-figures">
          <Figure
            label="Net cost a day"
            before={e.baselineCostGbpPerDay}
            after={e.currentCostGbpPerDay}
            format={(n) => gbp(n)}
          />
          <Figure
            label="Forecast miss per half-hour"
            before={e.baselineError}
            after={e.currentError}
            format={(n) => kwh(n, { precision: "table" })}
          />
        </dl>
      ) : (
        !view.closed && <p className="trial-note">Not enough clean readings yet to compare before and after.</p>
      )}
      {view.confounders.length > 0 && !view.closed && (
        <ul className="trial-chips" aria-label="What makes this comparison less reliable">
          {view.confounders.map((c) => (
            <li key={c}>
              <Chip tone="neutral">{c}</Chip>
            </li>
          ))}
        </ul>
      )}
      {e.hypothesis && !view.closed && (
        <p className="trial-hypothesis">
          <span>Expected: </span>
          <RichText text={e.hypothesis} />
        </p>
      )}
      <Disclosure summary="How we compare">
        <p className="body-copy">
          <RichText text={e.result} />
        </p>
        {!!e.confounders?.length && (
          <ul className="evidence">
            {e.confounders.map((c) => (
              <li key={c}>
                <RichText text={c} />
              </li>
            ))}
          </ul>
        )}
        <p className="muted">
          Joule compares days before and after the change with matching coverage of your meters and prices. It is an
          observation, not proof that the change caused the difference.
        </p>
        {!view.closed && <p className="muted">Check-in {dayTime(e.reviewAt)}.</p>}
      </Disclosure>
      {!!e.decisions?.length && (
        <Disclosure summary="Notes and decisions" count={e.decisions.length}>
          <ul className="evidence">
            {e.decisions.map((d, i) => (
              <li key={i}>
                <strong>{trialDecisionText(d).text}</strong> · {dayTime(d.at)}
                {d.notes && trialDecisionText(d).byYou && <p>{d.notes}</p>}
              </li>
            ))}
          </ul>
        </Disclosure>
      )}
      <div className="trial-actions card-action-row">
        {!view.closed && (
          <>
            <Button
              size="sm"
              disabled={busy}
              aria-describedby={titleId}
              onClick={() => void decide("keep", "Kept. Trial closed.")}
            >
              Keep it
            </Button>
            <Button
              size="sm"
              variant="danger"
              disabled={busy || !canUndo}
              title={undoReason || undefined}
              aria-describedby={titleId}
              onClick={() => {
                setNotes("");
                setDialog("revert");
              }}
            >
              Undo
            </Button>
          </>
        )}
        <OverflowMenu
          label={`More actions for ${title}`}
          items={[
            ...(!view.closed
              ? [
                  {
                    label: "Give it another week",
                    icon: <CalendarPlus size={15} aria-hidden="true" />,
                    onSelect: () =>
                      void decide("extend", `Review extended to ${dayLabel(nextWeek)}.`, { extendDays: 7 }),
                  },
                ]
              : []),
            {
              label: "Add a note",
              icon: <NotebookPen size={15} aria-hidden="true" />,
              onSelect: () => {
                setNotes("");
                setDialog("notes");
              },
            },
          ]}
        />
        {!view.closed && !canUndo && <span className="card-action-meta">{undoReason}</span>}
      </div>
      <Modal
        open={!!dialog}
        onOpenChange={(v) => {
          if (!v && !busy) setDialog("");
        }}
        title={dialog === "revert" ? "Undo this change?" : "Add a note"}
        description={
          dialog === "revert"
            ? "Puts the setting back as it was before, saved as a new version. Later edits to the same setting stop it."
            : "Notes are kept with this trial."
        }
      >
        <form
          onSubmit={async (event) => {
            event.preventDefault();
            const ok =
              dialog === "revert"
                ? await decide("revert", "Undone. The setting is back as it was.", { notes })
                : await decide("notes", "Note added.", { notes });
            if (ok) setDialog("");
          }}
        >
          <label className="field">
            {dialog === "revert" ? "Why (optional)" : "Note"}
            <textarea
              value={notes}
              maxLength={4000}
              rows={3}
              required={dialog === "notes"}
              onChange={(event) => setNotes(event.target.value)}
            />
          </label>
          <div className="dialog-actions">
            <Button variant="ghost" type="button" disabled={busy} onClick={() => setDialog("")}>
              Cancel
            </Button>
            <Button variant={dialog === "revert" ? "danger" : "primary"} disabled={busy} type="submit">
              {dialog === "revert" ? "Undo the change" : "Save note"}
            </Button>
          </div>
        </form>
      </Modal>
    </article>
  );
}
