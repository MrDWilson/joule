import { useEffect, useId, useState, type ReactNode } from "react";
import { Check, Copy, Lock, RefreshCw, Search, TriangleAlert } from "lucide-react";
import { Button } from "../ui";
import { metricLabel } from "../../lib/labels";
import { count as countText } from "../../lib/format";
import { storeAccessKey } from "../../lib/api";
import {
  detectMeters,
  field,
  generateAccessKey,
  normaliseAddress,
  saveAndRestart,
  findPredbat,
  foundFromLabel,
  meterChanges,
  testPredbat,
  type MeterDetection,
  type MeterSuggestion,
  type PredbatProbe,
  type SetupConfig,
} from "../../lib/setupConfig";

/**
 * The forms Setup uses to change Joule's own settings: Predbat's address (with Test and Find), the access key, secrets,
 * meter mappings and a single switch. Every save goes to Joule's data folder and restarts Joule, which takes a few seconds.
 */

/** Saving restarts Joule; this tracks it and reloads the page once Joule is back. */
export function useSaveAndRestart() {
  const [state, setState] = useState<{ busy: boolean; error: string }>({ busy: false, error: "" });
  async function run(values: Record<string, string | null>, beforeReload?: () => void) {
    setState({ busy: true, error: "" });
    try {
      await saveAndRestart(values);
      beforeReload?.();
      window.location.reload();
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  }
  return { ...state, run };
}

/** "Restarting Joule…" while a save applies, or the error that stopped it. */
export function SaveStatus({ busy, error }: { busy: boolean; error: string }) {
  if (busy)
    return (
      <p className="setup-saving" role="status">
        <RefreshCw size={14} aria-hidden="true" className="spin" /> Saving and restarting Joule. This takes a few
        seconds…
      </p>
    );
  if (error)
    return (
      <p className="sheet-warning" role="alert">
        <TriangleAlert size={14} aria-hidden="true" />
        {error}
      </p>
    );
  return null;
}

/** Shown in place of a form when the environment sets the value: Setup can't override it. */
export function EnvironmentNote({ envVar, children }: { envVar: string; children?: ReactNode }) {
  return (
    <p className="setup-env-note muted">
      <Lock size={13} aria-hidden="true" /> Set by <code className="entity-id">{envVar}</code> in Joule's environment,
      which always wins. {children}
    </p>
  );
}

function ProbeResult({ probe }: { probe: PredbatProbe }) {
  return probe.ok ? (
    <p className="setup-probe ok" role="status">
      <Check size={14} aria-hidden="true" /> Predbat{probe.version ? ` ${probe.version}` : ""} answered at{" "}
      <code className="entity-id">{probe.url}</code> · {countText(probe.entities)} entities.
    </p>
  ) : (
    <p className="sheet-warning" role="status">
      <TriangleAlert size={14} aria-hidden="true" />
      <span>
        <code className="entity-id">{probe.url}</code>: {probe.error}
      </span>
    </p>
  );
}

/**
 * Predbat's address with Test and Find. Find tries the usual places (a Docker container called predbat, the Docker host,
 * homeassistant.local for the add-on) and fills in the first that answers.
 */
export function PredbatAddressField({
  value,
  onChange,
  onProbe,
  disabled,
}: {
  value: string;
  onChange: (value: string) => void;
  onProbe?: (probe: PredbatProbe | null) => void;
  disabled?: boolean;
}) {
  const id = useId();
  const [probe, setProbe] = useState<PredbatProbe | null>(null);
  const [tried, setTried] = useState<PredbatProbe[] | null>(null);
  const [working, setWorking] = useState<"" | "test" | "find">("");
  const [error, setError] = useState("");
  const report = (p: PredbatProbe | null) => {
    setProbe(p);
    onProbe?.(p);
  };
  return (
    <div className="setup-address">
      <label className="field" htmlFor={id}>
        Predbat's address
        <span className="setup-address-row">
          <input
            id={id}
            type="text"
            inputMode="url"
            autoComplete="off"
            spellCheck={false}
            placeholder="http://192.168.1.20:5052"
            value={value}
            disabled={disabled}
            onChange={(e) => {
              onChange(e.target.value);
              report(null);
            }}
          />
          <Button
            type="button"
            variant="secondary"
            size="sm"
            disabled={disabled || !!working || !value.trim()}
            onClick={async () => {
              setWorking("test");
              setError("");
              setTried(null);
              try {
                const url = normaliseAddress(value);
                onChange(url);
                report(await testPredbat(url));
              } catch (e) {
                setError((e as Error).message);
              }
              setWorking("");
            }}
          >
            <RefreshCw size={14} aria-hidden="true" className={working === "test" ? "spin" : undefined} />
            {working === "test" ? "Testing…" : "Test"}
          </Button>
          <Button
            type="button"
            variant="secondary"
            size="sm"
            disabled={disabled || !!working}
            onClick={async () => {
              setWorking("find");
              setError("");
              try {
                const results = await findPredbat();
                const found = results.find((r) => r.ok);
                setTried(found ? null : results);
                if (found) onChange(found.url);
                report(found ?? null);
              } catch (e) {
                setError((e as Error).message);
              }
              setWorking("");
            }}
          >
            <Search size={14} aria-hidden="true" />
            {working === "find" ? "Looking…" : "Find Predbat"}
          </Button>
        </span>
      </label>
      <p className="form-note muted">
        Predbat's web interface, usually port 5052, as Joule can reach it. Another computer: its IP address, like
        http://192.168.1.20:5052. The Home Assistant add-on: your Home Assistant's IP address with port 5052 (open the
        port in the add-on's Network settings first).
      </p>
      {probe && <ProbeResult probe={probe} />}
      {tried && (
        <div className="setup-tried" role="status">
          <p className="muted">Predbat didn't answer at any of the usual addresses. Type its address instead.</p>
          <ul>
            {tried.map((t) => (
              <li key={t.url}>
                <code className="entity-id">{t.url}</code> <span className="muted">{t.error}</span>
              </li>
            ))}
          </ul>
        </div>
      )}
      {error && (
        <p className="sheet-warning" role="alert">
          {error}
        </p>
      )}
    </div>
  );
}

/** A generated access key with Copy and "New key", or the person's own. */
export function AccessKeyField({ value, onChange }: { value: string; onChange: (value: string) => void }) {
  const id = useId();
  const [copied, setCopied] = useState(false);
  return (
    <div className="setup-key">
      <label className="field" htmlFor={id}>
        Your access key
        <span className="setup-address-row">
          <input
            id={id}
            type="text"
            autoComplete="off"
            spellCheck={false}
            className="setup-key-input"
            value={value}
            onChange={(e) => onChange(e.target.value.trim())}
          />
          <Button
            type="button"
            variant="secondary"
            size="sm"
            aria-label={copied ? "Copied" : "Copy the access key"}
            onClick={async () => {
              try {
                await navigator.clipboard.writeText(value);
                setCopied(true);
                setTimeout(() => setCopied(false), 2000);
              } catch {
                setCopied(false);
              }
            }}
          >
            {copied ? <Check size={14} aria-hidden="true" /> : <Copy size={14} aria-hidden="true" />}
            {copied ? "Copied" : "Copy"}
          </Button>
          <Button type="button" variant="ghost" size="sm" onClick={() => onChange(generateAccessKey())}>
            New key
          </Button>
        </span>
      </label>
      <p className="form-note muted">
        Joule asks for this once in each new browser, so only you can see your data and approve changes. Keep a copy in
        your password manager. At least 16 characters.
      </p>
    </div>
  );
}

/**
 * Demo mode's "Connect my Predbat": the address (tested), an access key and, optionally, Predbat's MCP secret. One button
 * saves them and restarts Joule live; this browser is then signed in with the new key.
 */
export function GoLiveForm({ config }: { config: SetupConfig }) {
  const [address, setAddress] = useState(field(config, "Predbat:BaseUrl")?.value ?? "");
  const [probe, setProbe] = useState<PredbatProbe | null>(null);
  const [key, setKey] = useState(() => generateAccessKey());
  const [mcp, setMcp] = useState("");
  const save = useSaveAndRestart();
  const addressFromEnv = field(config, "Predbat:BaseUrl")?.source === "environment";
  const mcpField = field(config, "Predbat:McpToken");
  const ready = (addressFromEnv || address.trim().length > 0) && (!config.accessKeyNeeded || key.length >= 16);
  return (
    <form
      className="setup-golive"
      aria-label="Connect my Predbat"
      onSubmit={async (e) => {
        e.preventDefault();
        if (!ready || save.busy) return;
        const values: Record<string, string | null> = { "App:Demo": "false" };
        if (!addressFromEnv) values["Predbat:BaseUrl"] = normaliseAddress(address);
        if (config.accessKeyNeeded) values["App:AccessKey"] = key;
        if (mcp.trim() && mcpField?.source !== "environment") values["Predbat:McpToken"] = mcp.trim();
        await save.run(values, () => {
          if (config.accessKeyNeeded) storeAccessKey(key);
          window.location.hash = "#/setup";
        });
      }}
    >
      <ol className="setup-golive-steps">
        <li>
          {addressFromEnv ? (
            <EnvironmentNote envVar="Predbat__BaseUrl">
              Joule will read Predbat at <code className="entity-id">{field(config, "Predbat:BaseUrl")?.value}</code>.
            </EnvironmentNote>
          ) : (
            <PredbatAddressField value={address} onChange={setAddress} onProbe={setProbe} disabled={save.busy} />
          )}
        </li>
        {config.accessKeyNeeded && (
          <li>
            <AccessKeyField value={key} onChange={setKey} />
          </li>
        )}
        {mcpField?.source !== "environment" && (
          <li>
            <SecretInput
              label="Predbat's MCP secret (optional)"
              value={mcp}
              onChange={setMcp}
              note="The mcp_secret from Predbat's apps.yaml (with mcp_enable: True). It lets the AI read Predbat's logs and entity history. Joule finds your meters without it. You can add it later."
            />
          </li>
        )}
      </ol>
      {!addressFromEnv && address.trim() && probe && !probe.ok && (
        <p className="muted">Predbat didn't answer the test. You can still switch and fix the address afterwards.</p>
      )}
      <SaveStatus busy={save.busy} error={save.error} />
      <Button type="submit" disabled={!ready || save.busy}>
        Switch to my Predbat
      </Button>
      <p className="form-note muted">
        Saved in Joule's data folder (<code className="entity-id">{config.settingsFile}</code>). Joule restarts in a few
        seconds and then shows what's left to set up. The demo's data stays separate.
      </p>
    </form>
  );
}

/** A password-style input for a token or key. */
export function SecretInput({
  label,
  value,
  onChange,
  note,
  placeholder,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  note?: string;
  placeholder?: string;
}) {
  const id = useId();
  return (
    <>
      <label className="field" htmlFor={id}>
        {label}
        <input
          id={id}
          type="password"
          autoComplete="off"
          spellCheck={false}
          placeholder={placeholder}
          value={value}
          onChange={(e) => onChange(e.target.value)}
        />
      </label>
      {note && <p className="form-note muted">{note}</p>}
    </>
  );
}

/** Save one secret (MCP secret, AI API key) from Setup, or show that the environment sets it. */
export function SecretSetting({
  config,
  settingKey,
  label,
  note,
}: {
  config: SetupConfig | null;
  settingKey: string;
  label: string;
  note?: string;
}) {
  const f = field(config, settingKey);
  const [value, setValue] = useState("");
  const save = useSaveAndRestart();
  if (!config || !f) return null;
  if (f.source === "environment") return <EnvironmentNote envVar={f.envVar} />;
  return (
    <form
      className="setup-inline-form"
      onSubmit={async (e) => {
        e.preventDefault();
        if (value.trim()) await save.run({ [settingKey]: value.trim() });
      }}
    >
      <SecretInput
        label={label}
        value={value}
        onChange={setValue}
        note={note}
        placeholder={f.set ? "Saved · type a new one to replace it" : undefined}
      />
      <SaveStatus busy={save.busy} error={save.error} />
      <span className="setup-actions">
        <Button type="submit" size="sm" disabled={!value.trim() || save.busy || !config.canSave}>
          Save and restart
        </Button>
        {f.source === "saved" && (
          <Button
            type="button"
            variant="ghost"
            size="sm"
            disabled={save.busy}
            onClick={() => save.run({ [settingKey]: null })}
          >
            Remove
          </Button>
        )}
      </span>
    </form>
  );
}

/** Predbat's address in a live install: change it, test it, save it. */
export function PredbatAddressSetting({ config }: { config: SetupConfig | null }) {
  const f = field(config, "Predbat:BaseUrl");
  const [address, setAddress] = useState(f?.value ?? "");
  const save = useSaveAndRestart();
  useEffect(() => setAddress(f?.value ?? ""), [f?.value]);
  if (!config || !f) return null;
  if (f.source === "environment") return <EnvironmentNote envVar={f.envVar} />;
  const changed = normaliseAddress(address) !== (f.value ?? "");
  return (
    <form
      className="setup-inline-form"
      onSubmit={async (e) => {
        e.preventDefault();
        if (changed && address.trim()) await save.run({ "Predbat:BaseUrl": normaliseAddress(address) });
      }}
    >
      <PredbatAddressField value={address} onChange={setAddress} disabled={save.busy} />
      <SaveStatus busy={save.busy} error={save.error} />
      {changed && address.trim() && (
        <Button type="submit" size="sm" disabled={save.busy}>
          Save and restart
        </Button>
      )}
    </form>
  );
}

/** One on/off setting saved from Setup (Predbat__WritesEnabled), or the environment note. */
export function SwitchSetting({
  config,
  settingKey,
  on,
  onLabel,
  offLabel,
}: {
  config: SetupConfig | null;
  settingKey: string;
  on: boolean;
  onLabel: string;
  offLabel: string;
}) {
  const f = field(config, settingKey);
  const save = useSaveAndRestart();
  if (!config || !f) return null;
  if (f.source === "environment") return <EnvironmentNote envVar={f.envVar} />;
  return (
    <>
      <SaveStatus busy={save.busy} error={save.error} />
      <Button
        size="sm"
        variant={on ? "secondary" : "primary"}
        disabled={save.busy || !config.canSave}
        onClick={() => save.run({ [settingKey]: on ? "false" : "true" })}
      >
        {on ? offLabel : onLabel}
      </Button>
    </>
  );
}

const NOT_MAPPED = "";

/**
 * Every meter with the sensor Joule would use and the others it could be, so the person can confirm or change any of them
 * before saving. Joule already uses the ones it is sure of (found in Predbat's apps.yaml or among its sensors); this is for
 * changing those and choosing where Predbat offers more than one. Meters set in the environment are shown but can't be changed.
 */
export function MeterMapper({
  config,
  auto,
  focus = null,
  onClose,
}: {
  config: SetupConfig | null;
  /** Look straight away instead of waiting for "Find my meters". */
  auto: boolean;
  /** The meter to put the cursor on once the choices are shown. */
  focus?: string | null;
  /** Shows a Close button. */
  onClose?: () => void;
}) {
  const [detection, setDetection] = useState<MeterDetection | null>(null);
  const [choice, setChoice] = useState<Record<string, string>>({});
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");
  const save = useSaveAndRestart();
  async function detect() {
    setLoading(true);
    setError("");
    try {
      const d = await detectMeters();
      setDetection(d);
      // What is in use now; for a meter with nothing in use, Joule's suggestion (unless it was left unmapped on purpose).
      setChoice(
        Object.fromEntries(
          d.meters.map((m) => [m.key, m.current ?? (m.declined ? NOT_MAPPED : m.entity) ?? NOT_MAPPED]),
        ),
      );
    } catch (e) {
      setError((e as Error).message);
    }
    setLoading(false);
  }
  useEffect(() => {
    if (auto || focus) void detect();
  }, [auto, focus]);
  useEffect(() => {
    if (detection && focus) document.getElementById(`meter-${focus}`)?.focus();
  }, [detection, focus]);
  if (!config) return null;
  const changes = meterChanges(detection?.meters ?? [], choice, (key) => field(config, key)?.source === "environment");
  const count = Object.keys(changes).length;
  return (
    <div className="meter-mapper">
      {!detection ? (
        <Button variant="secondary" size="sm" disabled={loading} onClick={detect}>
          <Search size={14} aria-hidden="true" />
          {loading ? "Looking for your meters…" : "Find my meters"}
        </Button>
      ) : !detection.haveEntities ? (
        <p className="muted">
          Joule hasn't read Predbat yet, so it can't see your sensors. Connect Predbat first, then try again.{" "}
          <Button variant="link" size="sm" onClick={detect}>
            Try again
          </Button>
        </p>
      ) : (
        <form
          onSubmit={async (e) => {
            e.preventDefault();
            if (count) await save.run(changes);
          }}
        >
          <p className="muted">
            {detection.appsSource
              ? `Joule reads your Predbat apps.yaml (through ${detection.appsSource}), then sensor names and units. Change any that are wrong.`
              : "Suggested by name and unit from the sensors Predbat sees. Add Predbat's MCP secret to use your apps.yaml instead. Check each one."}
            {detection.appsError ? ` (apps.yaml: ${detection.appsError})` : ""}
          </p>
          <ul className="meters meter-choices">
            {detection.meters.map((m) => {
              const env = field(config, m.key)?.source === "environment";
              const options = [
                ...new Set([m.current, m.entity, ...m.alternatives.map((a) => a.entity)].filter(Boolean) as string[]),
              ];
              const selected = choice[m.key] ?? NOT_MAPPED;
              return (
                <li key={m.key} className="meter meter-choice">
                  <span className={`meter-tick ${selected ? "ok" : ""}`} aria-hidden="true">
                    {selected ? <Check size={13} /> : null}
                  </span>
                  <label className="meter-name" htmlFor={`meter-${m.metric}`}>
                    {metricLabel(m.metric)}
                    {m.metric === "load" && <span className="meter-required"> · needed</span>}
                  </label>
                  <span className="meter-detail">
                    <select
                      id={`meter-${m.metric}`}
                      value={selected}
                      disabled={env || save.busy}
                      onChange={(e) => setChoice((c) => ({ ...c, [m.key]: e.target.value }))}
                    >
                      <option value={NOT_MAPPED}>Not mapped</option>
                      {options.map((o) => (
                        <option key={o} value={o}>
                          {o}
                        </option>
                      ))}
                    </select>
                    <span className="muted">{choiceNote(m, selected, env)}</span>
                  </span>
                </li>
              );
            })}
          </ul>
          <SaveStatus busy={save.busy} error={save.error} />
          <span className="setup-actions">
            <Button type="submit" size="sm" disabled={!count || save.busy || !config.canSave}>
              {count ? `Use these meters (${count} ${count === 1 ? "change" : "changes"})` : "No changes"}
            </Button>
            <Button type="button" variant="ghost" size="sm" disabled={loading || save.busy} onClick={detect}>
              Look again
            </Button>
            {onClose && (
              <Button type="button" variant="ghost" size="sm" disabled={save.busy} onClick={onClose}>
                Close
              </Button>
            )}
          </span>
        </form>
      )}
      {error && (
        <p className="sheet-warning" role="alert">
          {error}
        </p>
      )}
    </div>
  );
}

/** The line under a meter's choice: where the sensor came from, or what choosing means. */
function choiceNote(m: MeterSuggestion, selected: string, env: boolean) {
  const reading = m.state != null ? ` · now ${m.state} ${m.unit ?? ""}`.trimEnd() : "";
  if (env) return `Set by ${m.envVar}`;
  if (!selected) {
    if (m.current || m.auto) return "Joule won't look for this one again";
    if (m.declined) return "Left unmapped";
    return m.entity || m.alternatives.length ? "" : "Nothing that looks like it";
  }
  if (selected === m.current && m.auto)
    return selected === m.entity ? `${foundFromLabel(m.from)}${reading}` : foundFromLabel(null);
  if (selected === m.current) return "Mapped now";
  if (selected === m.entity && m.from) {
    if (m.confident) return `${foundFromLabel(m.from)}${reading}`;
    const key = /^([a-z0-9_]+) in apps\.yaml$/.exec(m.from)?.[1];
    return `A guess from ${key ? `Predbat's ${key} setting` : "the sensor's name and unit"}: check it${reading}`;
  }
  return "";
}
