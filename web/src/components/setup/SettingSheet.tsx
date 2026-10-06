import { useEffect, useMemo, useState } from "react";
import { ArrowUpRight, Minus, Plus, TriangleAlert } from "lucide-react";
import { Button, Chip, Modal, Segmented, Switch } from "../ui";
import { PlainText } from "../PlainText";
import { changedFromDefault, displayValue, riskBadge, sameValue, valueHistory } from "../../lib/settings";
import { previewEdit, trialWarning, type ChangePreview } from "../../lib/setupApi";
import { limitsText } from "./ModePicker";
import { ago, dayLabel } from "../../lib/time";
import { api } from "../../lib/api";
import { useApp } from "../../context/AppContext";
import type { Mutate } from "../../completion-types";
import type { Revision, Setting } from "../../types";

/**
 * Undo from the toast: finds the version that saved this value (the newest one that changed this setting to it) and
 * reverts it, exactly as Undo on the Changes timeline does, so the undo is itself a version that can be undone.
 */
async function undoSave(setting: Setting, value: string, mutate: Mutate) {
  const { state } = await api<{ state: { revision: number; revisions: Revision[] } }>("/state");
  const saved = [...state.revisions]
    .sort((a, b) => b.id - a.id)
    .find((r) => r.reverts == null && r.changes.some((c) => c.key === setting.key && sameValue(c.after, value)));
  if (!saved) return;
  await mutate(
    `/revisions/${saved.id}/revert`,
    { revision: state.revision },
    `${setting.name} is back to its earlier value.`,
  );
}

/** A small step chart of a number setting's past values (oldest left), with the current value as the last point. */
export function Sparkline({ points, label }: { points: { at: string; value: number }[]; label: string }) {
  if (points.length < 2) return null;
  const w = 240,
    h = 44,
    pad = 4;
  const t0 = Date.parse(points[0].at),
    t1 = Math.max(Date.parse(points[points.length - 1].at), t0 + 1);
  const values = points.map((p) => p.value);
  const lo = Math.min(...values),
    hi = Math.max(...values);
  const x = (t: string) => pad + ((Date.parse(t) - t0) / (t1 - t0)) * (w - pad * 2);
  const y = (v: number) => (hi === lo ? h / 2 : h - pad - ((v - lo) / (hi - lo)) * (h - pad * 2));
  let d = `M${x(points[0].at)},${y(points[0].value)}`;
  for (let i = 1; i < points.length; i++) d += ` H${x(points[i].at)} V${y(points[i].value)}`;
  const last = points[points.length - 1];
  return (
    <svg className="sparkline" viewBox={`0 0 ${w} ${h}`} role="img" aria-label={label} preserveAspectRatio="none">
      <path d={d} fill="none" />
      <circle cx={x(last.at)} cy={y(last.value)} r={3} />
    </svg>
  );
}

function Editor({
  setting,
  value,
  setValue,
  disabled,
}: {
  setting: Setting;
  value: string;
  setValue: (v: string) => void;
  disabled: boolean;
}) {
  const label = `New value for ${setting.name}`;
  if (setting.type === "boolean")
    return (
      <Segmented
        label={label}
        value={/^on$/i.test(value) ? "on" : "off"}
        onChange={setValue}
        options={[
          { value: "on", label: "On", disabled },
          { value: "off", label: "Off", disabled },
        ]}
      />
    );
  if (setting.options.length)
    return (
      <select aria-label={label} value={value} disabled={disabled} onChange={(e) => setValue(e.target.value)}>
        {setting.options.map((o) => (
          <option key={o} value={o}>
            {displayValue(setting, o)}
          </option>
        ))}
      </select>
    );
  if (setting.type !== "number")
    return <input aria-label={label} value={value} disabled={disabled} onChange={(e) => setValue(e.target.value)} />;
  const step = setting.step > 0 ? setting.step : 0.01;
  const decimals = Math.max(0, (String(step).split(".")[1] ?? "").length);
  const n = Number(value);
  // An input value, not a display figure: rounded to the setting's own step.
  const fixed = (x: number) => String(Math.round(x * 10 ** decimals) / 10 ** decimals);
  const bump = (direction: number) => {
    const base = Number.isFinite(n) ? n : (setting.min ?? 0);
    let next = Math.round((base + direction * step) / step) * step;
    if (setting.min != null) next = Math.max(setting.min, next);
    if (setting.max != null) next = Math.min(setting.max, next);
    setValue(fixed(next));
  };
  const ranged = setting.min != null && setting.max != null && setting.max > setting.min;
  return (
    <div className="number-editor">
      {ranged && (
        <input
          type="range"
          aria-label={`${label} (slider)`}
          min={setting.min!}
          max={setting.max!}
          step={step}
          value={Number.isFinite(n) ? n : setting.min!}
          disabled={disabled}
          onChange={(e) => setValue(fixed(Number(e.target.value)))}
        />
      )}
      <div className="stepper">
        <Button
          type="button"
          variant="secondary"
          size="sm"
          aria-label="Decrease"
          disabled={disabled}
          onClick={() => bump(-1)}
        >
          <Minus size={14} aria-hidden="true" />
        </Button>
        <input
          aria-label={label}
          type="number"
          inputMode="decimal"
          min={setting.min ?? undefined}
          max={setting.max ?? undefined}
          step={step}
          value={value}
          disabled={disabled}
          onChange={(e) => setValue(e.target.value)}
        />
        <Button
          type="button"
          variant="secondary"
          size="sm"
          aria-label="Increase"
          disabled={disabled}
          onClick={() => bump(1)}
        >
          <Plus size={14} aria-hidden="true" />
        </Button>
        {setting.unit && <span className="muted">{setting.unit}</span>}
      </div>
      {ranged && (
        <p className="muted">
          Allowed {setting.min}–{setting.max}
          {setting.unit === "%" ? "%" : setting.unit ? ` ${setting.unit}` : ""}
        </p>
      )}
    </div>
  );
}

/**
 * One setting in a sheet: what it does, Predbat's default, its history, an editor and (for low-risk numbers) whether the
 * AI may change it by itself. `lock` explains why saving isn't possible right now; the draft is still kept.
 */
export function SettingSheet({
  setting,
  revision,
  revisions,
  busy,
  lock,
  mutate,
  onClose,
  onLimits,
}: {
  setting: Setting;
  revision: number;
  revisions: Revision[];
  busy: boolean;
  lock: string;
  mutate: Mutate;
  onClose: () => void;
  onLimits: (s: Setting) => void;
}) {
  const { notify } = useApp();
  const [value, setValue] = useState(setting.value);
  const [opened] = useState(revision);
  const [preview, setPreview] = useState<ChangePreview | null>(null);
  const changed = !sameValue(value, setting.value) && value.trim() !== "";
  useEffect(() => {
    setPreview(null);
    if (!changed || lock) return;
    let live = true;
    const timer = setTimeout(() => {
      previewEdit(setting.key, value)
        .then((p) => live && setPreview(p))
        .catch(() => {});
    }, 350);
    return () => {
      live = false;
      clearTimeout(timer);
    };
  }, [value, changed, lock, setting.key]);

  const history = useMemo(() => valueHistory(setting.key, revisions), [setting.key, revisions]);
  const numeric = setting.type === "number" && history.every((p) => Number.isFinite(Number(p.value)));
  const points = numeric
    ? [...history, { at: new Date().toISOString(), value: setting.value }].map((p) => ({
        at: p.at,
        value: Number(p.value),
      }))
    : [];
  const risk = riskBadge(setting);
  const show = (v: string | null | undefined) => displayValue(setting, v);
  const stale = revision !== opened;

  return (
    <Modal
      open
      onOpenChange={(open) => {
        if (!open) onClose();
      }}
      title={setting.name}
      description={[setting.section ?? setting.category, risk?.label].filter(Boolean).join(" · ")}
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>
            {changed ? "Cancel" : "Close"}
          </Button>
          <Button
            disabled={!changed || busy || !!lock || preview?.allowed === false}
            title={lock || undefined}
            onClick={async () => {
              if (await mutate(`/settings/${setting.key}`, { value, revision: opened }, "")) {
                onClose();
                // The toast offers Undo straight away; it reverts the version this save made (also undoable in Changes).
                notify(`${setting.name} saved as ${displayValue(setting, value)}.`, {
                  label: "Undo",
                  onClick: () => void undoSave(setting, value, mutate),
                });
              }
            }}
          >
            Save change
          </Button>
        </>
      }
    >
      <div className="sheet-body">
        {setting.description && (
          <p className="body-copy">
            <PlainText text={setting.description} />
          </p>
        )}
        <dl className="sheet-facts">
          <div>
            <dt>Now</dt>
            <dd className="sheet-value">{show(setting.value)}</dd>
          </div>
          {setting.default != null && setting.default !== "" && (
            <div>
              <dt>Predbat default</dt>
              <dd>
                {show(setting.default)}
                {changedFromDefault(setting) && (
                  <Chip tone="info" className="sheet-changed">
                    Changed
                  </Chip>
                )}
              </dd>
            </div>
          )}
          {history.length > 1 && (
            <div className="sheet-history">
              <dt>History</dt>
              <dd>
                <Sparkline
                  points={points}
                  label={`${setting.name} over time: ${history.map((p) => show(p.value)).join(", ")}, now ${show(setting.value)}`}
                />
                <span className="muted">
                  Changed {history.length - 1} {history.length === 2 ? "time" : "times"} since {dayLabel(history[0].at)}{" "}
                  · last {ago(history[history.length - 1].at)}
                </span>
              </dd>
            </div>
          )}
        </dl>

        <div className="sheet-edit">
          <h3 className="sheet-subhead">Change it</h3>
          <Editor setting={setting} value={value} setValue={setValue} disabled={busy} />
          {lock && (
            <p className="sheet-lock" role="status">
              {lock}
              {changed ? ". Your draft is kept." : "."}
            </p>
          )}
          {stale && (
            <p className="callout">
              Predbat's settings changed while this was open. Saving checks the value is still {show(setting.value)}.
            </p>
          )}
          {preview?.allowed === false && preview.reason && (
            <p className="sheet-warning" role="alert">
              <TriangleAlert size={14} aria-hidden="true" />
              <PlainText text={preview.reason} />
            </p>
          )}
          {preview?.affectedExperiments.map((t) => (
            <p key={t.id} className="sheet-warning">
              <TriangleAlert size={14} aria-hidden="true" />
              <span>{trialWarning(t, ago(t.startedAt))}</span>
            </p>
          ))}
          {changed && !lock && <p className="muted">Saved as a new version you can undo from Changes.</p>}
        </div>

        {setting.autoEligible ? (
          <div className="sheet-auto">
            <div className="sheet-auto-row">
              <Switch
                checked={setting.autoAllowed}
                label={`Allow automatic changes to ${setting.name}`}
                disabled={busy}
                onCheckedChange={(on) => {
                  if (on) onLimits(setting);
                  else void mutate(`/permissions/${setting.key}`, { allowed: false }, "Automatic changes turned off.");
                }}
              />
              <div>
                <strong>Let the AI change this by itself</strong>
                <p className="muted">
                  {setting.autoAllowed
                    ? `Allowed: ${limitsText(setting)}. Only in Automatic mode.`
                    : "Off. Only in Automatic mode, within limits you set."}
                </p>
              </div>
              {setting.autoAllowed && (
                <Button variant="secondary" size="sm" disabled={busy} onClick={() => onLimits(setting)}>
                  Limits
                </Button>
              )}
            </div>
          </div>
        ) : (
          <p className="muted sheet-note">
            Always needs your approval: the AI only changes low-risk numbers by itself.
          </p>
        )}

        <div className="sheet-meta">
          <code className="entity-id">{setting.entityId}</code>
          {setting.predbatName && <span className="muted">Called “{setting.predbatName.trim()}” in Predbat</span>}
          <a href={setting.documentation} target="_blank" rel="noreferrer" className="text-link">
            Predbat docs <ArrowUpRight size={13} aria-hidden="true" />
            <span className="sr-only"> (opens in new tab)</span>
          </a>
        </div>
      </div>
    </Modal>
  );
}
