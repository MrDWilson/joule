import { useCallback, useEffect, useId, useState } from "react";
import { Check, RefreshCw, Search, Send, TriangleAlert } from "lucide-react";
import { Button, Chip, Disclosure, Switch } from "../components/ui";
import { LoadingBlock } from "../components/ui/States";
import { EnvironmentNote, SecretInput } from "../components/setup/SetupForms";
import { plainError } from "../lib/errors";
import { dayTime } from "../lib/time";
import {
  eventsValue,
  getPushSettings,
  homeAssistantNotifyServices,
  logStatus,
  quietParts,
  savePushSettings,
  testPush,
  type PushChannel,
  type PushEvent,
  type PushField,
  type PushLogEntry,
  type PushSettingsView,
} from "../lib/pushApi";

/**
 * Setup › Notifications: send what the bell shows to a phone, through ntfy, Pushover, Home Assistant, Telegram, a chat webhook
 * or a JSON webhook. Each channel has an on/off switch, its own events and quiet hours, and a Test button. Saving applies at once.
 */
export default function NotificationsPage() {
  const [view, setView] = useState<PushSettingsView | null>(null);
  const [error, setError] = useState("");
  const load = useCallback(() => {
    getPushSettings()
      .then((v) => (setView(v), setError("")))
      .catch((e) => setError(plainError(e)));
  }, []);
  useEffect(load, [load]);
  if (!view)
    return error ? (
      <p className="sheet-warning" role="alert">
        {error}
      </p>
    ) : (
      <LoadingBlock lines={4} label="Loading notification settings" />
    );
  const on = view.channels.filter((c) => c.enabled);
  return (
    <div className="push-page">
      <p className="muted push-lead">
        The bell at the top lists what needs you. Joule can also send it to your phone, even with this page closed. Turn
        on one or more ways below and use Test to check it arrives.
        {on.length > 0 && ` Sending with ${on.map((c) => c.name).join(", ")}.`}
      </p>
      {view.locked && (
        <p className="sheet-warning" role="status">
          {view.locked}
        </p>
      )}
      <GeneralSettings view={view} onSaved={setView} />
      <h2 className="push-heading">Ways to reach you</h2>
      {view.channels.map((c) => (
        <ChannelCard key={c.id} channel={c} events={view.events} canSave={view.canSave} onSaved={setView} />
      ))}
      <DeliveryLog log={view.log} onRefresh={load} />
    </div>
  );
}

/** Saves values and hands back the new settings; the error stays with the card that saved. */
function useSave(onSaved: (v: PushSettingsView) => void) {
  const [state, setState] = useState<{ busy: boolean; error: string; saved: boolean }>({
    busy: false,
    error: "",
    saved: false,
  });
  async function run(values: Record<string, string | null>) {
    setState({ busy: true, error: "", saved: false });
    try {
      onSaved(await savePushSettings(values));
      setState({ busy: false, error: "", saved: true });
      return true;
    } catch (e) {
      setState({ busy: false, error: plainError(e), saved: false });
      return false;
    }
  }
  return { ...state, run };
}

function TextField({
  field,
  value,
  onChange,
  type = "text",
  disabled,
  onRemove,
}: {
  field: PushField;
  value: string;
  onChange: (v: string) => void;
  type?: string;
  disabled?: boolean;
  /** For a secret saved in Setup: forget it. */
  onRemove?: () => void;
}) {
  const id = useId();
  if (field.source === "environment") return <EnvironmentNote envVar={field.envVar} />;
  if (field.secret)
    return (
      <>
        <SecretInput
          label={field.label}
          value={value}
          onChange={onChange}
          note={field.note ?? undefined}
          placeholder={field.set ? "Saved · type a new one to replace it" : (field.placeholder ?? undefined)}
        />
        {field.set && field.source === "saved" && onRemove && (
          <span className="push-remove">
            <Button type="button" variant="ghost" size="sm" disabled={disabled} onClick={onRemove}>
              Remove saved{" "}
              {field.label
                .replace(/ \(optional\)$/, "")
                .replace(/^Your /, "")
                .toLowerCase()}
            </Button>
          </span>
        )}
      </>
    );
  return (
    <>
      <label className="field" htmlFor={id}>
        {field.label}
        <input
          id={id}
          type={type}
          inputMode={
            type === "number" ? "numeric" : field.kind === "url" || field.kind === "webhook" ? "url" : undefined
          }
          autoComplete="off"
          spellCheck={false}
          placeholder={field.placeholder ?? undefined}
          value={value}
          disabled={disabled}
          onChange={(e) => onChange(e.target.value)}
        />
      </label>
      {field.note && <p className="form-note muted">{field.note}</p>}
    </>
  );
}

const inputType = (kind: string) =>
  kind === "time" ? "time" : kind === "minutes" || kind === "perHour" ? "number" : "text";

/** Joule's public address (for links), when something counts as offline, the summary time and the hourly limit. */
function GeneralSettings({ view, onSaved }: { view: PushSettingsView; onSaved: (v: PushSettingsView) => void }) {
  // Unset numbers and times show the value in use (the default), so nothing reads as blank or "--:--".
  const shown = (f: PushField) => f.value ?? (f.kind === "url" ? "" : (f.placeholder ?? ""));
  const initial = () => Object.fromEntries(view.general.map((f) => [f.key, shown(f)]));
  const [values, setValues] = useState<Record<string, string>>(initial);
  const save = useSave(onSaved);
  const changed = Object.fromEntries(
    view.general
      .filter((f) => f.source !== "environment" && (values[f.key] ?? "").trim() !== shown(f))
      .map((f) => [f.key, values[f.key].trim() || null]),
  );
  const dirty = Object.keys(changed).length > 0;
  return (
    <section className="files-card push-card" aria-labelledby="push-general">
      <h2 id="push-general" className="push-card-title">
        For every channel
      </h2>
      <form
        className="push-form"
        onSubmit={async (e) => {
          e.preventDefault();
          if (dirty) await save.run(changed);
        }}
      >
        <div className="push-grid">
          {view.general.map((f) => (
            <div key={f.key} className={f.kind === "url" ? "push-wide" : undefined}>
              <TextField
                field={f}
                type={inputType(f.kind)}
                value={values[f.key] ?? ""}
                disabled={!view.canSave}
                onChange={(v) => setValues((x) => ({ ...x, [f.key]: v }))}
              />
            </div>
          ))}
        </div>
        <SaveLine busy={save.busy} error={save.error} saved={save.saved && !dirty} />
        <Button type="submit" size="sm" disabled={!dirty || save.busy || !view.canSave}>
          Save
        </Button>
      </form>
    </section>
  );
}

function SaveLine({ busy, error, saved }: { busy: boolean; error: string; saved: boolean }) {
  if (error)
    return (
      <p className="sheet-warning" role="alert">
        <TriangleAlert size={14} aria-hidden="true" />
        {error}
      </p>
    );
  if (busy)
    return (
      <p className="setup-saving" role="status">
        <RefreshCw size={14} aria-hidden="true" className="spin" /> Saving…
      </p>
    );
  if (saved)
    return (
      <p className="setup-probe ok" role="status">
        <Check size={14} aria-hidden="true" /> Saved. It applies straight away.
      </p>
    );
  return null;
}

function status(c: PushChannel): { label: string; tone: string } {
  if (c.enabled && c.ready) return { label: "On", tone: "success" };
  if (c.enabled) return { label: "Needs setup", tone: "warn" };
  return { label: "Off", tone: "neutral" };
}

/** One channel: its switch, then (behind "Settings") its fields, events, quiet hours, Save and Test. */
function ChannelCard({
  channel: c,
  events,
  canSave,
  onSaved,
}: {
  channel: PushChannel;
  events: PushEvent[];
  canSave: boolean;
  onSaved: (v: PushSettingsView) => void;
}) {
  const headingId = useId();
  const [values, setValues] = useState<Record<string, string>>(() =>
    Object.fromEntries(c.fields.map((f) => [f.key, f.secret ? "" : (f.value ?? "")])),
  );
  const [chosen, setChosen] = useState<string[]>(c.events);
  const [quiet, setQuiet] = useState(!!c.quietHours);
  const [[from, to], setWindow] = useState<[string, string]>(quietParts(c.quietHours));
  const [test, setTest] = useState<{ busy: boolean; ok?: boolean; message?: string }>({ busy: false });
  const save = useSave(onSaved);
  const toggle = useSave(onSaved);
  const remove = useSave(onSaved);

  const changes: Record<string, string | null> = {};
  for (const f of c.fields) {
    if (f.source === "environment") continue;
    const v = (values[f.key] ?? "").trim();
    if (f.secret ? v.length > 0 : v !== (f.value ?? "")) changes[f.key] = v || null;
  }
  if (c.eventsField.source !== "environment" && [...chosen].sort().join() !== [...c.events].sort().join())
    changes[c.eventsField.key] = eventsValue(events.map((e) => e.id).filter((id) => chosen.includes(id)));
  const window = quiet ? `${from}-${to}` : null;
  if (c.quietField.source !== "environment" && window !== c.quietHours) changes[c.quietField.key] = window;
  const dirty = Object.keys(changes).length > 0;
  const s = status(c);
  const env = c.enabledField.source === "environment";

  async function saveChanges() {
    if (!dirty) return true;
    const ok = await save.run(changes);
    // Secrets are written once; the box empties and the placeholder says it's saved.
    if (ok)
      setValues((x) =>
        Object.fromEntries(Object.entries(x).map(([k, v]) => [k, c.fields.find((f) => f.key === k)?.secret ? "" : v])),
      );
    return ok;
  }

  return (
    <section className="files-card push-card push-channel" aria-labelledby={headingId} data-channel={c.id}>
      <div className="push-channel-head">
        <div>
          <h3 id={headingId}>
            {c.name}{" "}
            <Chip tone={s.tone} dot>
              {s.label}
            </Chip>
          </h3>
          <p className="muted">{c.description}</p>
        </div>
        {env ? null : (
          <Switch
            checked={c.enabled}
            label={`Send notifications with ${c.name}`}
            disabled={!canSave || toggle.busy || (!c.enabled && !c.ready)}
            onCheckedChange={(v) => void toggle.run({ [c.enabledField.key]: v ? "true" : null })}
          />
        )}
      </div>
      {env && <EnvironmentNote envVar={c.enabledField.envVar} />}
      {/* What's missing, once you've started on this channel; untouched ones stay quiet. */}
      {!c.ready && c.problem && (c.enabled || c.fields.some((f) => f.set)) && (
        <p className="push-problem muted">{c.problem}</p>
      )}
      {c.ready && !c.enabled && !env && <p className="push-problem muted">Ready. Turn it on with the switch.</p>}
      {(toggle.error || remove.error) && (
        <p className="sheet-warning" role="alert">
          {toggle.error || remove.error}
        </p>
      )}
      <Disclosure summary="Settings" defaultOpen={false}>
        <form
          className="push-form"
          aria-label={`${c.name} settings`}
          onSubmit={async (e) => {
            e.preventDefault();
            await saveChanges();
          }}
        >
          {c.fields.map((f) =>
            c.id === "HomeAssistant" && f.kind === "service" && f.source !== "environment" ? (
              <ServicePicker
                key={f.key}
                field={f}
                value={values[f.key] ?? ""}
                onChange={(v) => setValues((x) => ({ ...x, [f.key]: v }))}
                canSave={canSave}
              />
            ) : (
              <TextField
                key={f.key}
                field={f}
                value={values[f.key] ?? ""}
                disabled={!canSave || remove.busy}
                onChange={(v) => setValues((x) => ({ ...x, [f.key]: v }))}
                onRemove={() => void remove.run({ [f.key]: null })}
              />
            ),
          )}
          <fieldset className="push-events" disabled={!canSave || c.eventsField.source === "environment"}>
            <legend>Send me</legend>
            {c.eventsField.source === "environment" && <EnvironmentNote envVar={c.eventsField.envVar} />}
            {events.map((e) => (
              <label key={e.id} className="check-field push-event">
                <input
                  type="checkbox"
                  checked={chosen.includes(e.id)}
                  onChange={(x) =>
                    setChosen((list) => (x.target.checked ? [...list, e.id] : list.filter((id) => id !== e.id)))
                  }
                />
                <span>
                  {e.label}
                  <span className="muted push-event-detail">{e.description}</span>
                </span>
              </label>
            ))}
          </fieldset>
          <fieldset className="push-quiet" disabled={!canSave || c.quietField.source === "environment"}>
            <legend>Quiet hours</legend>
            {c.quietField.source === "environment" ? (
              <EnvironmentNote envVar={c.quietField.envVar} />
            ) : (
              <>
                <label className="check-field">
                  <input type="checkbox" checked={quiet} onChange={(e) => setQuiet(e.target.checked)} />
                  <span>
                    Hold messages overnight
                    <span className="muted push-event-detail">
                      Sent when quiet hours end, and only if it still needs you. The daily summary isn't held.
                    </span>
                  </span>
                </label>
                {quiet && (
                  <span className="push-window">
                    <label className="field">
                      From
                      <input type="time" value={from} onChange={(e) => setWindow([e.target.value, to])} />
                    </label>
                    <label className="field">
                      Until
                      <input type="time" value={to} onChange={(e) => setWindow([from, e.target.value])} />
                    </label>
                  </span>
                )}
              </>
            )}
          </fieldset>
          <SaveLine busy={save.busy} error={save.error} saved={save.saved && !dirty} />
          {test.message !== undefined && (
            <p className={test.ok ? "setup-probe ok" : "sheet-warning"} role="status">
              {test.ok ? <Check size={14} aria-hidden="true" /> : <TriangleAlert size={14} aria-hidden="true" />}
              {test.message}
            </p>
          )}
          <span className="setup-actions">
            <Button type="submit" size="sm" disabled={!dirty || save.busy || !canSave}>
              Save
            </Button>
            <Button
              type="button"
              variant="secondary"
              size="sm"
              disabled={test.busy || save.busy || !canSave}
              onClick={async () => {
                setTest({ busy: true });
                if (!(await saveChanges())) return setTest({ busy: false });
                try {
                  const r = await testPush(c.id);
                  setTest({
                    busy: false,
                    ok: r.ok,
                    message: r.ok ? "Sent. Check that it arrived." : (r.error ?? "It didn't send."),
                  });
                } catch (e) {
                  setTest({ busy: false, ok: false, message: plainError(e) });
                }
              }}
            >
              <Send size={14} aria-hidden="true" />
              {test.busy ? "Sending…" : dirty ? "Save and test" : "Test"}
            </Button>
          </span>
        </form>
      </Disclosure>
    </section>
  );
}

/** The notify service, typed or picked from the ones Home Assistant has (the phone apps first). */
function ServicePicker({
  field,
  value,
  onChange,
  canSave,
}: {
  field: PushField;
  value: string;
  onChange: (v: string) => void;
  canSave: boolean;
}) {
  const id = useId();
  const [services, setServices] = useState<string[] | null>(null);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  return (
    <div className="push-service">
      <label className="field" htmlFor={id}>
        {field.label}
        <span className="setup-address-row">
          {services?.length ? (
            <select id={id} value={value} onChange={(e) => onChange(e.target.value)} disabled={!canSave}>
              {!services.includes(value) && <option value={value}>{value || "Choose a service"}</option>}
              {services.map((s) => (
                <option key={s} value={s}>
                  {s}
                </option>
              ))}
            </select>
          ) : (
            <input
              id={id}
              type="text"
              autoComplete="off"
              spellCheck={false}
              placeholder={field.placeholder ?? undefined}
              value={value}
              disabled={!canSave}
              onChange={(e) => onChange(e.target.value)}
            />
          )}
          <Button
            type="button"
            variant="secondary"
            size="sm"
            disabled={busy || !canSave}
            onClick={async () => {
              setBusy(true);
              setError("");
              try {
                const r = await homeAssistantNotifyServices();
                setServices(r.services);
                if (r.error) setError(r.error);
                else if (!r.services.length) setError("Home Assistant has no notify services.");
                else if (!value) onChange(r.services[0]);
              } catch (e) {
                setError(plainError(e));
              }
              setBusy(false);
            }}
          >
            <Search size={14} aria-hidden="true" />
            {busy ? "Looking…" : "Find services"}
          </Button>
        </span>
      </label>
      <p className="form-note muted">
        The companion app's service is notify.mobile_app_ and your phone's name. Find services lists what Home Assistant
        has.
      </p>
      {error && (
        <p className="sheet-warning" role="status">
          {error}
        </p>
      )}
    </div>
  );
}

function DeliveryLog({ log, onRefresh }: { log: PushLogEntry[]; onRefresh: () => void }) {
  return (
    <section className="files-card push-card" aria-labelledby="push-log">
      <div className="push-channel-head">
        <h2 id="push-log" className="push-card-title">
          Sent recently
        </h2>
        <Button variant="ghost" size="sm" onClick={onRefresh}>
          <RefreshCw size={14} aria-hidden="true" /> Refresh
        </Button>
      </div>
      {log.length ? (
        <ul className="push-log">
          {log.map((l) => {
            const st = logStatus(l);
            return (
              <li key={l.id}>
                <span className="push-log-head">
                  <strong>{l.test ? `Test · ${l.channelName}` : l.title.replace(/^Joule · /, "")}</strong>
                  <Chip tone={st.tone}>{st.label}</Chip>
                </span>
                <span className="muted push-log-detail">
                  {l.test ? "" : `${l.channelName} · `}
                  {dayTime(l.at)}
                  {l.attempts > 1 ? ` · ${l.attempts} tries` : ""}
                  {l.nextAt && l.status !== "queued" ? ` · next ${dayTime(l.nextAt)}` : ""}
                </span>
                {l.error && <span className="push-log-error">{l.error}</span>}
              </li>
            );
          })}
        </ul>
      ) : (
        <p className="muted">Nothing sent yet. Turn on a channel and press Test to try it.</p>
      )}
    </section>
  );
}
