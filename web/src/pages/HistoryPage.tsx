import { useEffect, useMemo, useState } from "react";
import {
  ArrowUpCircle,
  Bot,
  Camera,
  Check,
  Download,
  FlaskConical,
  Hand,
  History as HistoryIcon,
  ListPlus,
  Power,
  Redo2,
  RotateCcw,
  Sparkles,
  TriangleAlert,
  Undo2,
  UserRound,
} from "lucide-react";
import type { LucideIcon } from "lucide-react";
import { useApp } from "../context/AppContext";
import { Button, Chip, Modal } from "../components/ui";
import { EmptyState, LoadingBlock } from "../components/ui/States";
import { PlainText } from "../components/PlainText";
import { OverflowMenu } from "../components/setup/OverflowMenu";
import { buildTimeline, groupByDay, type ChangeEntry, type ChangeIcon } from "../lib/changes";
import { displayValue, editLock } from "../lib/settings";
import { previewRestore, previewUndo, trialWarning, type ChangePreview } from "../lib/setupApi";
import { download } from "../lib/api";
import { plural } from "../lib/copy";
import { statusLabel } from "../lib/labels";
import { ago, clock, when } from "../lib/time";
import "./setup.css";

const ICONS: Record<ChangeIcon, LucideIcon> = {
  first: Camera,
  predbat: HistoryIcon,
  you: UserRound,
  approved: Check,
  auto: Bot,
  undo: Undo2,
  restore: RotateCcw,
  software: ArrowUpCircle,
  override: Hand,
  control: Power,
  list: ListPlus,
};
const TONES: Partial<Record<ChangeIcon, string>> = {
  software: "info",
  override: "warn",
  control: "warn",
  approved: "accent",
  you: "accent",
  auto: "accent",
};

interface Pending {
  kind: "undo" | "restore";
  entry: ChangeEntry;
  revision: number;
  preview: ChangePreview | null;
  error: string;
}

/** The Changes timeline: what changed in Predbat's settings, by whom, and how to undo it. */
export default function HistoryPage() {
  const { mutate, busy, data, reportError, timeZone } = useApp();
  const s = data.state;
  const lock = editLock(data);
  const entries = useMemo(
    () =>
      buildTimeline({
        revisions: s.revisions,
        settings: s.settings,
        experiments: s.experiments,
        settingEvents: s.settingEvents,
      }),
    [s.revisions, s.settings, s.experiments, s.settingEvents],
  );
  const days = useMemo(() => groupByDay(entries, { timeZone }), [entries, timeZone]);
  const [pending, setPending] = useState<Pending | null>(null);
  const [showAll, setShowAll] = useState(false);
  const DAY_LIMIT = 7;
  const visibleDays = showAll ? days : days.slice(0, DAY_LIMIT);

  useEffect(() => {
    if (!pending || pending.preview || pending.error) return;
    let live = true;
    const id = pending.entry.revision!.id;
    (pending.kind === "undo" ? previewUndo(id) : previewRestore(id))
      .then((preview) => live && setPending((p) => (p && p.entry.id === pending.entry.id ? { ...p, preview } : p)))
      .catch((e: Error) => live && setPending((p) => (p ? { ...p, error: e.message } : p)));
    return () => {
      live = false;
    };
  }, [pending]);

  const undoable = entries.filter((e) => e.undoable).length;
  const pendingRedo = pending?.kind === "undo" && pending.entry.icon === "undo";
  // Values as the settings page shows them: "108%", "07:00", "On".
  const byKey = useMemo(() => new Map(s.settings.map((x) => [x.key, x])), [s.settings]);
  const show = (key: string, v: string) => displayValue(byKey.get(key) ?? { type: "text", unit: "" }, v);
  if (!s.revisions.length && !(s.settingEvents ?? []).length)
    return (
      <EmptyState title="No changes yet">
        Every change to Predbat's settings appears here: ones you make, ones the AI suggests and ones made in Predbat
        itself. Changes to settings can be undone.
      </EmptyState>
    );

  return (
    <div className="changes-page">
      <p className="changes-summary">
        <span>
          {plural(entries.length, "change")} since{" "}
          {when(entries[entries.length - 1]?.at ?? s.revisions[0].at, timeZone)}
        </span>
        {undoable > 0 && lock && (
          <span className="changes-lock">
            <TriangleAlert size={14} aria-hidden="true" /> {lock}, so Undo is off.{" "}
            {lock.startsWith("Saving needs live writes") && (
              <a className="text-link" href="#/setup">
                How to turn them on
              </a>
            )}
          </span>
        )}
      </p>
      {visibleDays.map((day) => (
        <section key={day.date} className="changes-day" aria-labelledby={`day-${day.date}`}>
          <h2 id={`day-${day.date}`}>{day.label}</h2>
          <ol className="change-list">
            {day.entries.map((e) => {
              const Icon = ICONS[e.icon];
              const r = e.revision;
              const current = !!r && r.id === s.revision;
              // An undo, undone again, is a redo.
              const redo = e.icon === "undo";
              const items = r
                ? [
                    // Restoring the current settings would do nothing, so it isn't offered there at all.
                    ...(e.restorable && !current
                      ? [
                          {
                            label: "Restore settings to this point",
                            icon: <RotateCcw size={15} aria-hidden="true" />,
                            disabled: !!lock || busy,
                            reason: lock,
                            onSelect: () =>
                              setPending({ kind: "restore", entry: e, revision: s.revision, preview: null, error: "" }),
                          },
                        ]
                      : []),
                    {
                      label: "Download this settings snapshot (JSON)",
                      icon: <Download size={15} aria-hidden="true" />,
                      onSelect: () =>
                        void download(`/revisions/${r.id}/export`, `predbat-settings-${r.id}.json`).catch((x: Error) =>
                          reportError(x.message),
                        ),
                    },
                  ]
                : [];
              return (
                <li key={e.id} className="change" data-revision={r?.id}>
                  <span className={`change-icon tone-${TONES[e.icon] ?? "neutral"}`} aria-hidden="true">
                    <Icon size={16} />
                  </span>
                  <div className="change-body">
                    <div className="change-head">
                      <p className="change-title">
                        <PlainText text={e.title} />
                      </p>
                      {items.length > 0 && (
                        <OverflowMenu label={`More actions for ${when(e.at, timeZone)}`} items={items} />
                      )}
                    </div>
                    <p className="change-meta">
                      <time dateTime={e.at}>{clock(e.at, { timeZone })}</time> · {e.via}
                      {current && (
                        <Chip tone="success" className="change-current">
                          Current
                        </Chip>
                      )}
                    </p>
                    {e.detail && (
                      <p className="change-detail">
                        <PlainText text={e.detail} />
                      </p>
                    )}
                    {e.lines.length > 1 && (
                      <ul className="change-lines">
                        {e.lines.map((l) => (
                          <li key={l.key}>
                            <span className="change-line-name">{l.name}</span>
                            <span className="change-line-values">
                              <span className="before">{l.before}</span> → <strong>{l.after}</strong>
                            </span>
                          </li>
                        ))}
                      </ul>
                    )}
                    {e.trials.map((t) => (
                      <a key={t.id} className="change-trial" href="#/insights/experiments">
                        <FlaskConical size={13} aria-hidden="true" /> Started a trial · {statusLabel(t.status).label}
                      </a>
                    ))}
                    {e.undoNote && e.lines.length > 0 && <p className="change-note">{e.undoNote}</p>}
                    {e.undoable && (
                      <div className="change-actions">
                        <Button
                          variant="secondary"
                          size="sm"
                          disabled={busy || !!lock}
                          title={lock || undefined}
                          aria-label={`${redo ? "Redo" : "Undo"}: ${e.title}`}
                          onClick={() =>
                            setPending({ kind: "undo", entry: e, revision: s.revision, preview: null, error: "" })
                          }
                        >
                          {redo ? <Redo2 size={14} aria-hidden="true" /> : <Undo2 size={14} aria-hidden="true" />}
                          {redo ? "Redo" : "Undo"}
                        </Button>
                      </div>
                    )}
                  </div>
                </li>
              );
            })}
          </ol>
        </section>
      ))}
      {days.length > DAY_LIMIT && !showAll && (
        <Button variant="secondary" onClick={() => setShowAll(true)}>
          Show {plural(days.length - DAY_LIMIT, "earlier day")}
        </Button>
      )}

      <Modal
        open={!!pending}
        onOpenChange={(open) => {
          if (!open) setPending(null);
        }}
        title={
          pending?.kind === "restore"
            ? "Restore settings to this point?"
            : pendingRedo
              ? "Redo this change?"
              : "Undo this change?"
        }
        description={
          pending?.kind === "restore"
            ? `Settings go back to how they were ${pending ? when(pending.entry.at, timeZone) : ""}. Saved as a new settings snapshot.`
            : pendingRedo
              ? "The setting goes back to the value you undid. Saved as a new settings snapshot, so this can be undone too."
              : "The setting goes back to its earlier value. Saved as a new settings snapshot, so this can be undone too."
        }
        footer={
          <>
            <Button variant="ghost" onClick={() => setPending(null)}>
              Cancel
            </Button>
            <Button
              variant="danger"
              disabled={busy || !!lock || !pending?.preview?.allowed}
              onClick={async () => {
                if (!pending) return;
                const id = pending.entry.revision!.id;
                // "Later changes were kept" only when there were later changes to keep.
                const later = s.revisions.some((x) => x.id > id);
                const done = pendingRedo ? "Change redone." : "Change undone.";
                const ok =
                  pending.kind === "undo"
                    ? await mutate(
                        `/revisions/${id}/revert`,
                        { revision: pending.revision },
                        later ? `${done} Later, unrelated changes were kept.` : done,
                      )
                    : await mutate(
                        `/revisions/${id}/restore`,
                        { revision: pending.revision },
                        "Settings restored and saved as a new settings snapshot.",
                      );
                if (ok) setPending(null);
              }}
            >
              {pending?.kind === "restore" ? "Restore settings" : pendingRedo ? "Redo change" : "Undo change"}
            </Button>
          </>
        }
      >
        {pending && !pending.preview && !pending.error && <LoadingBlock lines={2} label="Working out what changes" />}
        {pending?.error && (
          <p role="alert" className="sheet-warning">
            <TriangleAlert size={14} aria-hidden="true" />
            {pending.error}
          </p>
        )}
        {pending?.preview && (
          <div className="sheet-body">
            {!pending.preview.allowed ? (
              <p role="alert" className="sheet-warning">
                <TriangleAlert size={14} aria-hidden="true" />
                <PlainText text={pending.preview.reason ?? "This can't be done right now."} />
              </p>
            ) : (
              <div>
                <h3 className="sheet-subhead">{plural(pending.preview.changes.length, "setting")} will change</h3>
                <ul className="change-lines confirm">
                  {pending.preview.changes.map((c) => (
                    <li key={c.key}>
                      <span className="change-line-name">{c.name}</span>
                      <span className="change-line-values">
                        <span className="before">{show(c.key, c.before)}</span> →{" "}
                        <strong>{show(c.key, c.after)}</strong>
                      </span>
                    </li>
                  ))}
                </ul>
              </div>
            )}
            {pending.preview.affectedExperiments.map((t) => (
              <p key={t.id} className="sheet-warning">
                <FlaskConical size={14} aria-hidden="true" />
                <span>{trialWarning(t, ago(t.startedAt))}</span>
              </p>
            ))}
            {pending.preview.notRestored.length > 0 && (
              <p className="muted">
                <Sparkles size={13} aria-hidden="true" /> Left alone:{" "}
                {pending.preview.notRestored.map((x) => x.name).join(", ")}. Joule never restores Predbat's own controls
                (its version, manual overrides or mode).
              </p>
            )}
            {lock && <p className="sheet-lock">{lock}.</p>}
          </div>
        )}
      </Modal>
    </div>
  );
}
