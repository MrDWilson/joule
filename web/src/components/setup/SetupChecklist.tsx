import { useState, type ReactNode } from "react";
import { Bot, Check, CircleDashed, Database, PlugZap, RefreshCw, ScrollText, ShieldCheck, Timer } from "lucide-react";
import type { LucideIcon } from "lucide-react";
import { Button, ButtonLink, Chip, Disclosure } from "../ui";
import { PlainText } from "../PlainText";
import { EnvSnippet } from "./EnvSnippet";
import { metricLabel, providerLabel, readingStatus } from "../../lib/labels";
import { ago } from "../../lib/time";
import type { Mutate } from "../../completion-types";
import type { Payload } from "../../types";
import type { SetupMeter, SetupStatus } from "../../lib/setupApi";
import type { SetupConfig } from "../../lib/setupConfig";
import { MeterMapper, PredbatAddressSetting, SecretSetting, SwitchSetting } from "./SetupForms";

const PROFILE_LABELS: Record<string, string> = {
  daily_counter: "resets daily",
  solar_daily: "solar, resets daily",
  session_counter: "per charging session",
  lifetime_counter: "running total",
  price: "price",
  state: "state",
};

interface Step {
  key: string;
  icon: LucideIcon;
  title: string;
  done: boolean;
  required: boolean;
  summary: string;
  body: ReactNode;
}

function MeterRow({ meter }: { meter: SetupMeter }) {
  const ok = !!meter.entity && (meter.status === "observed" || meter.status === "idle");
  const top = meter.candidates[0];
  return (
    <li className={`meter ${meter.entity ? "mapped" : "missing"}`}>
      <span className={`meter-tick ${ok ? "ok" : meter.entity ? "warn" : ""}`} aria-hidden="true">
        {ok ? <Check size={13} /> : <CircleDashed size={13} />}
      </span>
      <span className="meter-name">
        {metricLabel(meter.metric)}
        {meter.required && !meter.entity && <span className="meter-required"> · needed</span>}
      </span>
      <span className="meter-detail">
        {meter.entity ? (
          <>
            <code className="entity-id">{meter.entity}</code>
            <span className="muted">
              {meter.status ? readingStatus(meter.status) : "No reading yet"}
              {meter.value != null ? ` · ${meter.value} ${meter.unit ?? ""}` : ""}
              {meter.profile ? ` · ${PROFILE_LABELS[meter.profile] ?? meter.profile}` : ""}
            </span>
          </>
        ) : top ? (
          <span className="muted">
            Not mapped. Looks like <code className="entity-id">{top.entity}</code>
            {top.state != null ? ` (now ${top.state} ${top.unit ?? ""})` : ""}
          </span>
        ) : (
          <span className="muted">Not mapped</span>
        )}
      </span>
    </li>
  );
}

/**
 * The setup checklist: each step with a tick and a form to finish it here (saved in Joule's data folder, then Joule
 * restarts). Values set in Joule's environment are shown read-only, and each step still offers the environment lines for
 * people who prefer them. Required steps come first; the optional ones say what they add.
 */
export function SetupChecklist({
  status,
  data,
  mutate,
  busy,
  onRefresh,
  config: setupConfig = null,
}: {
  status: SetupStatus;
  /** Setup's saved settings (GET /api/setup/config); null while loading or on an older server, which shows env lines only. */
  config?: SetupConfig | null;
  data: Payload;
  mutate: Mutate;
  busy: boolean;
  onRefresh: () => void;
}) {
  // Forms only where Setup may save (always, once live); otherwise the environment lines.
  const config = setupConfig?.canSave ? setupConfig : null;
  const steps = new Map(status.progress.steps.map((s) => [s.key, s]));
  const done = (key: string) => steps.get(key)?.done ?? false;
  const p = status.predbat,
    sensors = status.sensors,
    mcp = status.mcp;
  const ai = data.state.ai;
  const missing = sensors.meters.filter((m) => !m.entity);
  const suggested = missing.filter((m) => m.candidates.length).map((m) => `${m.envVar}=${m.candidates[0].entity}`);
  const [testing, setTesting] = useState(false);

  const list: Step[] = [
    {
      key: "predbat",
      icon: PlugZap,
      title: "Connect to Predbat",
      required: true,
      done: done("predbat") && done("collecting"),
      summary: !p.configured
        ? "Joule doesn't know where Predbat is yet."
        : p.error
          ? "Joule can't read Predbat right now."
          : p.lastCollection
            ? `Read ${ago(p.lastCollection)}${p.version ? ` · Predbat ${p.version}` : ""}`
            : "Waiting for the first reading.",
      body: (
        <>
          {p.configured ? (
            <p className="body-copy">
              Joule reads Predbat at <code className="entity-id">{p.address ?? "the configured address"}</code>
              {p.lastCollection ? `, last ${ago(p.lastCollection)}` : ""}.
            </p>
          ) : (
            <p className="body-copy">
              Tell Joule the address of Predbat's web interface (usually port 5052), as Joule's container can reach it.
            </p>
          )}
          {p.configured && p.error && (
            <p className="sheet-warning" role="status">
              <PlainText text={p.error} />
            </p>
          )}
          {config && <PredbatAddressSetting config={config} />}
          {!p.configured && (
            <Disclosure
              summary={config ? "Or set it in Joule's environment" : "Set it in Joule's environment"}
              defaultOpen={!config}
            >
              <EnvSnippet
                label="Predbat address"
                lines={[
                  "Predbat__BaseUrl=http://predbat:5052",
                  "# Predbat on the Docker host: http://host.docker.internal:5052",
                  "# Another machine or the Home Assistant add-on: http://192.168.1.20:5052",
                ]}
              />
            </Disclosure>
          )}
          {p.configured && (
            <Button
              variant="secondary"
              size="sm"
              disabled={busy || testing}
              onClick={async () => {
                setTesting(true);
                await mutate("/collect", {}, "Predbat answered: its plan and settings were read.");
                setTesting(false);
                onRefresh();
              }}
            >
              <RefreshCw size={14} aria-hidden="true" className={testing ? "spin" : undefined} />
              {testing ? "Reading…" : "Read Predbat now"}
            </Button>
          )}
        </>
      ),
    },
    {
      key: "meters",
      icon: Database,
      title: "Map your Home Assistant meters",
      required: true,
      done: done("meters"),
      summary: missing.length
        ? `${sensors.meters.length - missing.length} of ${sensors.meters.length} meters mapped${missing.some((m) => m.required) ? " · Home use is needed" : ""}`
        : "Every meter is mapped",
      body: (
        <>
          <p className="body-copy">
            Joule compares Predbat's plan with what your meters measured. Readings come{" "}
            {sensors.direct
              ? "straight from Home Assistant"
              : sensors.viaPredbat
                ? "through Predbat (it mirrors Home Assistant)"
                : "from Home Assistant"}
            {sensors.lastCollection ? `, last ${ago(sensors.lastCollection)}` : ""}.
          </p>
          {/* While meters are missing, the mapper lists every meter with its choices instead. */}
          {(!config || missing.length === 0) && (
            <ul className="meters">
              {sensors.meters.map((m) => (
                <MeterRow key={m.metric} meter={m} />
              ))}
            </ul>
          )}
          {config && <MeterMapper config={config} auto={missing.length > 0 && !!p.lastCollection} />}
          {missing.length > 0 && (
            <Disclosure
              summary={config ? "Or set them in Joule's environment" : "Set them in Joule's environment"}
              defaultOpen={!config}
            >
              <p className="muted">
                {suggested.length
                  ? "These look like the sensors Predbat already uses. Check them, then add the lines and restart Joule:"
                  : "Add a line for each meter, using the entity id from Home Assistant, then restart Joule:"}
              </p>
              <EnvSnippet
                label="Meter mappings"
                lines={
                  suggested.length
                    ? suggested
                    : missing.slice(0, 4).map((m) => `${m.envVar}=sensor.your_${m.metric}_energy_today`)
                }
              />
              {!sensors.direct && !sensors.viaPredbat && (
                <p className="muted">
                  Joule also needs to reach Home Assistant: set{" "}
                  <code className="entity-id">HomeAssistant__BaseUrl</code> and{" "}
                  <code className="entity-id">HomeAssistant__AccessToken</code>, or connect Predbat first and Joule
                  reads through it.
                </p>
              )}
            </Disclosure>
          )}
          <ButtonLink href="#/setup/sensors" size="sm">
            Sensor details
          </ButtonLink>
        </>
      ),
    },
    {
      key: "ai",
      icon: Bot,
      title: "Choose an AI provider",
      required: true,
      done: done("ai"),
      summary: done("ai")
        ? `${providerLabel(ai.provider)}${ai.model ? ` · ${ai.model}` : ""}`
        : "No AI provider is ready yet.",
      body: (
        <>
          <p className="body-copy">
            Joule's checks use an AI model: sign in with ChatGPT, or use an OpenAI-compatible API key (
            <code className="entity-id">Ai__ApiKey</code>). Then choose it in AI checks.
          </p>
          <ButtonLink href="#/setup/ai" size="sm" variant={done("ai") ? "secondary" : "primary"}>
            Open AI checks
          </ButtonLink>
          {config && (
            <Disclosure summary="Add an API key">
              <SecretSetting
                config={config}
                settingKey="Ai:ApiKey"
                label="OpenAI-compatible API key"
                note="Billed per use by your provider. Another provider's address goes in Ai__ApiBaseUrl. The key stays on the server and is never shown again."
              />
            </Disclosure>
          )}
        </>
      ),
    },
    {
      key: "mcp",
      icon: ScrollText,
      title: "Let the AI read Predbat's logs (MCP)",
      required: false,
      done: done("mcp"),
      summary: !mcp.configured
        ? "Optional · lets checks read Predbat's logs, entity history and apps.yaml"
        : mcp.connected
          ? `Working · ${mcp.tools} tools`
          : mcp.error
            ? "Set up, but the last check failed"
            : "Set up · not checked yet",
      body: (
        <>
          <p className="body-copy">
            With Predbat's MCP secret, checks can read Predbat's own logs and history, and you can see apps.yaml under
            Files.
          </p>
          {mcp.error && <p className="sheet-warning">{mcp.error}</p>}
          {config && (
            <SecretSetting
              config={config}
              settingKey="Predbat:McpToken"
              label="Predbat's MCP secret"
              note="The mcp_secret from Predbat's apps.yaml (turn on mcp_enable there too). Joule reaches MCP on port 8199 of Predbat's address."
            />
          )}
          {!mcp.configured ? (
            !config && <EnvSnippet label="MCP secret" lines={["Predbat__McpToken=<Predbat's mcp_secret>"]} />
          ) : (
            <Button
              variant="secondary"
              size="sm"
              disabled={busy}
              onClick={async () => {
                await mutate("/mcp/discover", {}, "Predbat's MCP connection checked.");
                onRefresh();
              }}
            >
              <RefreshCw size={14} aria-hidden="true" />
              Check connection
            </Button>
          )}
        </>
      ),
    },
    {
      key: "reviews",
      icon: Timer,
      title: "Turn on automatic checks",
      required: false,
      done: done("reviews"),
      summary: ai.scheduled
        ? `On · at most ${ai.maxRunsPerDay} a day`
        : "Optional · Joule checks Predbat's plan by itself after cheap and export windows",
      body: (
        <>
          <p className="body-copy">
            Off by default. When on, Joule runs its checks on its own and tells you what it found.
          </p>
          <ButtonLink href="#/setup/ai" size="sm">
            Schedule checks
          </ButtonLink>
        </>
      ),
    },
    {
      key: "writes",
      icon: ShieldCheck,
      title: "Allow Joule to change Predbat",
      required: false,
      done: done("writes"),
      summary: p.writesEnabled
        ? "On · changes you approve are applied"
        : "Optional · off: you make changes in Predbat yourself",
      body: (
        <>
          <p className="body-copy">
            Off by default, and Joule works fine without it: suggestions tell you what to change in Predbat. With it on,
            Joule applies the changes you approve (and, in Automatic, small changes you've allowed). Every change is
            saved and can be undone from Changes.
          </p>
          {config ? (
            <SwitchSetting
              config={config}
              settingKey="Predbat:WritesEnabled"
              on={p.writesEnabled}
              onLabel="Allow Joule to change Predbat"
              offLabel="Stop Joule changing Predbat"
            />
          ) : (
            !p.writesEnabled && <EnvSnippet label="Allow changes" lines={["Predbat__WritesEnabled=true"]} />
          )}
        </>
      ),
    },
  ];
  const firstOpen = list.find((s) => s.required && !s.done)?.key;

  return (
    <ol className="checklist">
      {list.map((s, i) => {
        const Icon = s.icon;
        return (
          <li key={s.key} className={`check-step ${s.done ? "done" : ""}`}>
            <details open={s.key === firstOpen || undefined}>
              <summary>
                <span className={`check-mark ${s.done ? "done" : s.required ? "todo" : "optional"}`} aria-hidden="true">
                  {s.done ? <Check size={15} /> : i + 1}
                </span>
                <span className="check-copy">
                  <span className="check-title">
                    <Icon size={15} aria-hidden="true" />
                    {s.title}
                    <span className="sr-only">{s.done ? " (done)" : s.required ? " (to do)" : " (optional)"}</span>
                  </span>
                  <span className="check-summary">{s.summary}</span>
                </span>
                {!s.done && s.required && <Chip tone="warn">To do</Chip>}
              </summary>
              <div className="check-body">{s.body}</div>
            </details>
          </li>
        );
      })}
    </ol>
  );
}
