import { useEffect, useId, useState } from "react";
import {
  ArrowUpRight,
  BellRing,
  CalendarClock,
  Check,
  Clock,
  Gauge,
  LoaderCircle,
  PlugZap,
  Sun,
  Target,
  Unplug,
  Zap,
} from "lucide-react";
import type { LucideIcon } from "lucide-react";
import { useApp } from "../context/AppContext";
import { Button, Chip, Disclosure, Switch } from "../components/ui";
import { McpConnection } from "../components/McpConnection";
import { RichText } from "../components/InvestigationText";
import { compact, count, usd } from "../lib/format";
import { clock, dayTime } from "../lib/time";
import { providerLabel } from "../lib/labels";
import {
  groupUsageByCheck,
  headlineOf,
  investigationVerdict,
  isQuiet,
  isUnfinished,
  mcpLastUsed,
  outcomeLabels,
  outcomeOf,
  todaySummary,
  usageByDay,
  type RunOutcome,
  type UsageDayBar,
  type UsageGroup,
  optionDetail,
} from "../lib/insights";
import { buildHash } from "../lib/router";
import type { Investigation, InvestigationPage, Preferences, UsageReport } from "../types";
import "./insights.css";

const OUTCOMES: RunOutcome[] = ["found", "quiet", "unfinished"];

/**
 * Tokens per day, from the first day with any (at most two weeks, at least a week), each bar split by what the checks
 * found; hover or focus a bar to read it. The axis names the top of the scale so a bar's height means something.
 */
function TokenBars({ bars: allBars, title }: { bars: UsageDayBar[]; title: string }) {
  const [active, setActive] = useState<number | null>(null);
  const titleId = useId();
  const first = allBars.findIndex((b) => b.total > 0);
  const bars = allBars.slice(Math.max(0, Math.min(first < 0 ? 0 : first, allBars.length - 7)));
  const max = Math.max(1, ...bars.map((b) => b.total));
  const shown = active ?? bars.length - 1;
  const readout = bars[shown];
  const runs = (b: UsageDayBar) => OUTCOMES.reduce((n, o) => n + b.runs[o], 0);
  return (
    <figure className="token-chart" aria-labelledby={titleId}>
      <figcaption id={titleId} className="token-chart-title">
        {title}
      </figcaption>
      <ul className="chart-legend" aria-label="Key">
        {OUTCOMES.map((o) => (
          <li key={o}>
            <span className={`legend-swatch outcome-${o}`} aria-hidden="true" />
            {outcomeLabels[o]}
          </li>
        ))}
      </ul>
      <p className="token-readout" aria-live="polite">
        {readout && (
          <>
            <strong>{readout.label}</strong> · {readout.total ? `${compact(readout.total)} tokens` : "no AI checks"}
            {readout.total > 0 &&
              ` · ${OUTCOMES.filter((o) => readout.runs[o])
                .map((o) => `${readout.runs[o]} ${outcomeLabels[o].toLowerCase()}`)
                .join(", ")}`}
          </>
        )}
      </p>
      <div className="token-plot">
        <div className="token-axis" aria-hidden="true">
          <span>{compact(max)}</span>
          <span>0</span>
        </div>
        <div className="token-bars" onMouseLeave={() => setActive(null)}>
          {bars.map((b, i) => (
            <button
              key={b.date}
              type="button"
              className={`token-bar${i === shown ? " is-active" : ""}`}
              aria-label={`${b.label}: ${b.total ? `${compact(b.total)} tokens, ${runs(b)} checks` : "no AI checks"}`}
              onMouseEnter={() => setActive(i)}
              onFocus={() => setActive(i)}
              onBlur={() => setActive(null)}
            >
              <span className="token-stack" style={{ height: `${(b.total / max) * 100}%` }}>
                {OUTCOMES.filter((o) => b.tokens[o] > 0).map((o) => (
                  <span key={o} className={`outcome-${o}`} style={{ flexGrow: b.tokens[o] }} />
                ))}
              </span>
              <span className="token-day" aria-hidden="true">
                {b.label.split(" ")[1] ?? b.label}
              </span>
            </button>
          ))}
        </div>
      </div>
      <Disclosure summary="Show as a table">
        <div className="table-wrap">
          <table className="numeric-table">
            <thead>
              <tr>
                <th>Day</th>
                {OUTCOMES.map((o) => (
                  <th key={o}>{outcomeLabels[o]}</th>
                ))}
                <th>Tokens</th>
              </tr>
            </thead>
            <tbody>
              {bars.map((b) => (
                <tr key={b.date}>
                  <td>{b.label}</td>
                  {OUTCOMES.map((o) => (
                    <td key={o}>{b.runs[o]}</td>
                  ))}
                  <td>{count(b.total)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Disclosure>
    </figure>
  );
}

const TRIGGERS: { icon: LucideIcon; text: string }[] = [
  { icon: Zap, text: "About 5 minutes after each cheap or export window ends" },
  { icon: BellRing, text: "When Predbat logs a new warning or error" },
  { icon: Gauge, text: "When the battery ends a half-hour more than 10 points off the plan" },
  { icon: PlugZap, text: "When a sensor has been offline for over 30 minutes" },
  { icon: Sun, text: "A morning (07:30) and evening (21:30) round-up" },
  { icon: Clock, text: "At least every 3 hours, as a heartbeat" },
];

/** The saved AI preferences with some fields changed: each card saves only its own fields onto the latest state. */
const withPrefs = (current: Preferences, change: Partial<Preferences>): Preferences => ({
  provider: current.provider,
  model: current.model,
  scheduled: current.scheduled,
  intervalMinutes: current.intervalMinutes,
  maxRunsPerDay: current.maxRunsPerDay,
  inputUsdPerMillion: current.inputUsdPerMillion,
  outputUsdPerMillion: current.outputUsdPerMillion,
  ...change,
});

/**
 * When checks run, and the controls for it: the Automatic checks switch (saved as you flip it), how far apart and how
 * many a day. Off, the triggers read as what would happen once it is on, and the limits are disabled.
 */
function Schedule() {
  const { data, mutate, busy } = useApp();
  const schedule = data.ai.schedule;
  const saved = data.state.ai;
  const on = saved.scheduled;
  const [interval, setInterval_] = useState(String(saved.intervalMinutes)),
    [perDay, setPerDay] = useState(String(saved.maxRunsPerDay));
  useEffect(() => {
    setInterval_(String(saved.intervalMinutes));
    setPerDay(String(saved.maxRunsPerDay));
  }, [saved.intervalMinutes, saved.maxRunsPerDay]);
  const dirty = Number(interval) !== saved.intervalMinutes || Number(perDay) !== saved.maxRunsPerDay;
  const triggers = (
    <ul className="trigger-list">
      {TRIGGERS.map(({ icon: Icon, text }) => (
        <li key={text}>
          <Icon size={15} aria-hidden="true" />
          {text}
        </li>
      ))}
    </ul>
  );
  return (
    <section className={`ai-card schedule-card${on ? "" : " is-off"}`} aria-labelledby="schedule-heading">
      <div className="ai-card-head">
        <h3 id="schedule-heading">
          <CalendarClock size={16} aria-hidden="true" />
          When Joule checks
        </h3>
        <Chip tone={on ? "success" : "neutral"} dot={on}>
          {on ? "Automatic" : "Only when you ask"}
        </Chip>
      </div>
      <label className="switch-row">
        <span>
          <strong>Automatic checks</strong>
          <small>Readings are collected either way; this only controls the AI.</small>
        </span>
        <Switch
          label="Automatic checks"
          checked={on}
          disabled={busy}
          onCheckedChange={(v) =>
            void mutate(
              "/ai/preferences",
              withPrefs(saved, { scheduled: v }),
              v ? "Automatic checks are on." : "Automatic checks are off. Joule checks when you ask.",
            )
          }
        />
      </label>
      {on ? (
        <>
          {schedule?.reason && (
            <p className="ai-card-lead">{schedule.reason.replace(/\bQuiet checks\b/g, "Quick checks")}</p>
          )}
          {triggers}
          <p className="ai-card-copy">
            In between, a quick check every {saved.intervalMinutes} minutes reads Predbat's log and the battery without
            the AI, and only calls it when something is new.
          </p>
        </>
      ) : (
        <Disclosure summary="When turned on, Joule checks:" className="schedule-off-triggers">
          {triggers}
        </Disclosure>
      )}
      <form
        className="schedule-limits"
        onSubmit={(e) => {
          e.preventDefault();
          void mutate(
            "/ai/preferences",
            withPrefs(saved, { intervalMinutes: Number(interval), maxRunsPerDay: Number(perDay) }),
            "Check limits saved.",
          );
        }}
      >
        <div className="form-row">
          <label className="field">
            At least this far apart (minutes)
            <input
              type="number"
              min="15"
              max="1440"
              required
              disabled={!on}
              value={interval}
              onChange={(e) => setInterval_(e.target.value)}
            />
          </label>
          <label className="field">
            Most AI checks a day
            <input
              type="number"
              min="1"
              max="96"
              required
              disabled={!on}
              value={perDay}
              onChange={(e) => setPerDay(e.target.value)}
            />
          </label>
        </div>
        {on && dirty && (
          <div className="card-action-row">
            <Button type="submit" size="sm" disabled={busy}>
              Save limits
            </Button>
          </div>
        )}
      </form>
      {on && schedule && (
        <p className="ai-card-meta">
          {schedule.runsToday} of {schedule.maxRunsPerDay} AI checks used today
          {schedule.lastQuietCheckAt && ` · last quick check ${clock(schedule.lastQuietCheckAt)}`}
          {schedule.nextCheckAt && ` · next quick check ${clock(schedule.nextCheckAt)}`}
        </p>
      )}
    </section>
  );
}

interface Objective {
  objective: string;
  label: string;
  description: string;
  options: { value: string; label: string; description: string }[];
}

/** What every check aims for: save the most money, limit battery wear or use as little grid as possible. */
function ObjectivePicker() {
  const { api, notify, reportError, load } = useApp();
  const [objective, setObjective] = useState<Objective | null>(null);
  const [saving, setSaving] = useState("");
  useEffect(() => {
    api<Objective>("/ai/objective")
      .then(setObjective)
      .catch(() => setObjective(null));
  }, [api]);
  if (!objective) return null;
  async function choose(value: string) {
    if (value === objective?.objective) return;
    setSaving(value);
    try {
      setObjective(await api<Objective>("/ai/objective", { objective: value }));
      notify("Checks will aim for that from now on.");
      await load();
    } catch (e) {
      reportError((e as Error).message);
    } finally {
      setSaving("");
    }
  }
  return (
    <section className="ai-card" aria-labelledby="objective-heading">
      <div className="ai-card-head">
        <h3 id="objective-heading">
          <Target size={16} aria-hidden="true" />
          What checks aim for
        </h3>
      </div>
      <div className="objective-options" role="radiogroup" aria-labelledby="objective-heading">
        {objective.options.map((o) => (
          <button
            key={o.value}
            type="button"
            role="radio"
            aria-checked={o.value === objective.objective}
            className="objective-option"
            disabled={!!saving}
            onClick={() => void choose(o.value)}
          >
            <span className="objective-radio" aria-hidden="true" />
            <span>
              <strong>{o.label}</strong>
              <small>{optionDetail(o.label, o.description)}</small>
            </span>
            {saving === o.value && <LoaderCircle size={14} className="spin" aria-hidden="true" />}
          </button>
        ))}
      </div>
    </section>
  );
}

/** Why the preferences can't be saved yet, or "" when they can. */
function blockReason(prefs: Preferences, ai: { apiConfigured: boolean; chatGptConnected: boolean }) {
  if (prefs.provider === "Api" && !ai.apiConfigured)
    return "The AI API isn't set up on the server yet (Ai__ApiBaseUrl and Ai__ApiKey).";
  if (prefs.provider === "ChatGpt" && !ai.chatGptConnected) return "Connect your ChatGPT account first.";
  if (prefs.provider !== "Demo" && !prefs.model.trim()) return "Choose a model.";
  return "";
}

/** The AI provider, model and schedule limits, with a one-call connection test. */
function ProviderForm() {
  const { api, mutate, busy, setBusy, data, load, notify, reportError } = useApp();
  const browserIsLoopback = ["127.0.0.1", "localhost", "[::1]"].includes(window.location.hostname);
  const [prefs, setPrefs] = useState<Preferences>(() => data.state.ai),
    [models, setModels] = useState<{ id: string; name: string }[]>([]),
    [test, setTest] = useState<{ ok: boolean; message: string } | null>(null),
    [testing, setTesting] = useState(false),
    [tried, setTried] = useState(false);
  const blocked = blockReason(prefs, data.ai);
  const subscription = prefs.provider === "ChatGpt";
  async function disconnectChat() {
    setBusy(true);
    try {
      const result = await api<{ remoteRevocationConfirmed?: boolean }>("/ai/chatgpt/disconnect", {});
      await load();
      notify(
        result.remoteRevocationConfirmed === false
          ? "Disconnected here. Check connected apps in ChatGPT's settings to be sure."
          : "ChatGPT disconnected.",
      );
    } catch (e) {
      reportError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  async function connectChat() {
    setBusy(true);
    try {
      const result = await api<{ url: string }>("/ai/chatgpt/start", {});
      window.location.assign(result.url);
    } catch (e) {
      reportError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  async function getModels() {
    try {
      setModels((await api<{ models: { id: string; name: string }[] }>("/ai/chatgpt/models")).models);
    } catch (e) {
      reportError((e as Error).message);
    }
  }
  async function testConnection() {
    setTesting(true);
    setTest(null);
    try {
      setTest(
        await api<{ ok: boolean; message: string }>("/ai/test", { provider: prefs.provider, model: prefs.model }),
      );
    } catch (e) {
      setTest({ ok: false, message: (e as Error).message });
    } finally {
      setTesting(false);
    }
  }
  return (
    <section className="ai-card provider-card" aria-labelledby="provider-heading">
      <div className="ai-card-head">
        <h3 id="provider-heading">AI provider</h3>
        <Chip tone={blockReason(data.state.ai, data.ai) ? "warn" : "success"} dot>
          {blockReason(data.state.ai, data.ai)
            ? "Needs setting up"
            : data.state.ai.provider === "Demo"
              ? "Demo · sample answers"
              : `${providerLabel(data.state.ai.provider)} · ${data.state.ai.model || "no model"}`}
        </Chip>
      </div>
      <form
        onSubmit={(e) => {
          e.preventDefault();
          setTried(true);
          if (blocked) return;
          // Only this card's fields: the schedule card saves its own, so neither undoes the other.
          void mutate(
            "/ai/preferences",
            withPrefs(data.state.ai, {
              provider: prefs.provider,
              model: prefs.model,
              inputUsdPerMillion: prefs.inputUsdPerMillion,
              outputUsdPerMillion: prefs.outputUsdPerMillion,
            }),
            "AI settings saved.",
          );
        }}
      >
        <label className="field">
          Provider
          <select value={prefs.provider} onChange={(e) => setPrefs({ ...prefs, provider: e.target.value })}>
            {data.connection.demo && <option value="Demo">Demo · sample answers</option>}
            <option value="ChatGpt">ChatGPT · your plan</option>
            <option value="Api">AI API · key on the server</option>
          </select>
        </label>
        {prefs.provider === "ChatGpt" && (
          <div className="provider-note">
            <p>
              <strong>
                {data.ai.chatGptConnected
                  ? `Connected${data.ai.chatGptEmail ? ` as ${data.ai.chatGptEmail}` : ""}`
                  : "Not connected yet"}
              </strong>{" "}
              Checks use your plan's allowance; there is no paid fallback.
            </p>
            <div className="card-action-row">
              {data.ai.chatGptConnected ? (
                <>
                  <Button type="button" size="sm" variant="secondary" onClick={() => void getModels()}>
                    Load my models
                  </Button>
                  <Button type="button" size="sm" variant="ghost" disabled={busy} onClick={() => void disconnectChat()}>
                    <Unplug size={14} aria-hidden="true" />
                    Disconnect
                  </Button>
                </>
              ) : data.ai.chatGptLocalSignInAvailable && browserIsLoopback ? (
                <Button type="button" size="sm" disabled={busy} onClick={() => void connectChat()}>
                  Continue with ChatGPT <ArrowUpRight size={14} aria-hidden="true" />
                </Button>
              ) : null}
            </div>
            {!data.ai.chatGptConnected && !(data.ai.chatGptLocalSignInAvailable && browserIsLoopback) && (
              <Disclosure summary="Setup help: connecting from another computer">
                <p className="body-copy">
                  ChatGPT's sign-in returns to a local loopback callback on the computer running your browser, so a
                  server elsewhere connects like this:
                </p>
                <ol className="setup-steps">
                  <li>
                    Run Joule on the computer with your browser, open <code>http://127.0.0.1:5080</code> and connect the
                    same ChatGPT account.
                  </li>
                  <li>
                    Stop it without disconnecting, then copy <code>auth/chatgpt-credentials.json</code> from its data
                    folder to the server's <code>auth</code> folder over SSH. Keep the server's own{" "}
                    <code>chatgpt-host.json</code>.
                  </li>
                  <li>
                    Make the file owned by the service user with permissions <code>0600</code>, delete any copies, and
                    restart the server. Treat the file as a secret.
                  </li>
                </ol>
                <a
                  className="text-link"
                  href="https://developers.openai.com/siwc/token-sharing-open-source/self-hosted-vms"
                  target="_blank"
                  rel="noreferrer"
                >
                  Official remote setup guide (opens in new tab)
                </a>
              </Disclosure>
            )}
          </div>
        )}
        {prefs.provider === "Api" && (
          <p className="provider-note">
            <strong>
              {data.ai.apiConfigured ? "The AI API is set up on the server." : "The AI API isn't set up yet."}
            </strong>{" "}
            Set <code>Ai__ApiBaseUrl</code> and <code>Ai__ApiKey</code> in the server environment; the key never reaches
            the browser.
          </p>
        )}
        {prefs.provider === "Demo" ? (
          <p className="provider-note">
            Demo checks are scripted and never call an AI, so there is no model to choose.
          </p>
        ) : (
          <label className="field">
            Model
            {models.length && subscription ? (
              <select value={prefs.model} onChange={(e) => setPrefs({ ...prefs, model: e.target.value })}>
                <option value="">Choose a model</option>
                {models.map((m) => (
                  <option key={m.id} value={m.id}>
                    {m.name}
                  </option>
                ))}
              </select>
            ) : (
              <input
                maxLength={150}
                placeholder="Model name"
                value={prefs.model}
                onChange={(e) => setPrefs({ ...prefs, model: e.target.value })}
              />
            )}
          </label>
        )}
        {prefs.provider === "Api" && (
          <div className="form-row">
            <label className="field">
              Input price, US$ per 1M tokens
              <input
                type="number"
                min="0"
                max="10000"
                step="any"
                value={prefs.inputUsdPerMillion}
                onChange={(e) => setPrefs({ ...prefs, inputUsdPerMillion: Number(e.target.value) })}
              />
            </label>
            <label className="field">
              Output price, US$ per 1M tokens
              <input
                type="number"
                min="0"
                max="10000"
                step="any"
                value={prefs.outputUsdPerMillion}
                onChange={(e) => setPrefs({ ...prefs, outputUsdPerMillion: Number(e.target.value) })}
              />
            </label>
          </div>
        )}
        {tried && blocked && (
          <p className="ask-error" role="alert">
            {blocked}
          </p>
        )}
        {test && (
          <p className={test.ok ? "test-ok" : "ask-error"} role="status">
            {test.ok ? <Check size={14} aria-hidden="true" /> : null} {test.message}
          </p>
        )}
        <div className="card-action-row">
          <Button
            type="button"
            variant="secondary"
            disabled={testing || !!blocked}
            onClick={() => void testConnection()}
          >
            {testing ? <LoaderCircle size={14} className="spin" aria-hidden="true" /> : null}
            {testing ? "Testing…" : "Test connection"}
          </Button>
          <Button type="submit" disabled={busy}>
            Save
          </Button>
        </div>
      </form>
    </section>
  );
}

/** One AI check as a row: when it started, what it found (linking to the check), its tries and tokens. */
function UsageRow({ g }: { g: UsageGroup }) {
  const { data } = useApp();
  const u = g.records[0];
  const check = g.check;
  const outcome = outcomeOf(u, data.state.investigations);
  const priced = u.provider === "Api" && g.estimatedUsd != null;
  const tries = g.records.length;
  const tokens =
    g.inputTokens || g.outputTokens
      ? `${compact(g.inputTokens)} in · ${compact(g.outputTokens)} out`
      : "Tokens not reported";
  const body = (
    <>
      <span className={`legend-swatch outcome-${outcome}`} aria-hidden="true" />
      <span className="usage-what">
        <span className="usage-headline">{check ? <RichText text={headlineOf(check)} /> : outcomeLabels[outcome]}</span>
        <span className="usage-sub">
          {dayTime(g.at)} · {providerLabel(u.provider)}
          {u.model && <span className="usage-model"> · {u.model}</span>}
          {tries > 1 && ` · ${tries} tries`}
        </span>
      </span>
      <span className="usage-tokens">
        {tokens}
        {priced && <small>{usd(g.estimatedUsd)}</small>}
      </span>
    </>
  );
  return (
    <li className="usage-row">
      {check ? (
        <a href={buildHash("insights", "", { id: check.id })} className="usage-link">
          {body}
        </a>
      ) : (
        <div className="usage-link">{body}</div>
      )}
    </li>
  );
}

/** A scripted demo check: no AI was called, so there are no tokens to show. */
function DemoRunRow({ i }: { i: Investigation }) {
  const outcome: RunOutcome = isUnfinished(i) ? "unfinished" : isQuiet(i) ? "quiet" : "found";
  return (
    <li className="usage-row">
      <a href={buildHash("insights", "", { id: i.id })} className="usage-link is-demo">
        <span className={`legend-swatch outcome-${outcome}`} aria-hidden="true" />
        <span className="usage-what">
          <span className="usage-headline">
            <RichText text={headlineOf(i)} />
          </span>
          <span className="usage-sub">{dayTime(i.at)} · Demo check · sample answer</span>
        </span>
      </a>
    </li>
  );
}

/** The last 10 AI checks (a check's tries on one row), with "Show all" reading 30 days from /api/usage. */
function RecentRuns() {
  const { api, data } = useApp();
  const [all, setAll] = useState<UsageGroup[] | null>(null),
    [loading, setLoading] = useState(false),
    [error, setError] = useState(""),
    [limit, setLimit] = useState(20);
  const demo = data.state.ai.provider === "Demo";
  const recent = groupUsageByCheck(
    data.state.usage.filter((u) => u.provider !== "Demo"),
    data.state.investigations,
  ).slice(0, 10);
  const demoChecks = demo
    ? [...data.state.investigations]
        .filter((i) => investigationVerdict(i) !== "running")
        .sort((a, b) => Date.parse(b.at) - Date.parse(a.at))
        .slice(0, 10)
    : [];
  async function showAll() {
    setLoading(true);
    setError("");
    try {
      const report = await api<UsageReport>("/usage?days=30");
      setAll(groupUsageByCheck(report.records, data.state.investigations));
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setLoading(false);
    }
  }
  const list = all ? all.slice(0, limit) : recent;
  return (
    <section className="ai-card" aria-labelledby="runs-heading">
      <div className="ai-card-head">
        <h3 id="runs-heading">{demo ? "Recent demo checks" : all ? "AI checks, last 30 days" : "Last 10 AI checks"}</h3>
      </div>
      {demo && !list.length ? (
        demoChecks.length ? (
          <ul className="usage-list">
            {demoChecks.map((i) => (
              <DemoRunRow key={i.id} i={i} />
            ))}
          </ul>
        ) : (
          <p className="muted">No demo checks yet. Ask a question on Insights to see one here.</p>
        )
      ) : list.length ? (
        <ul className="usage-list">
          {list.map((g) => (
            <UsageRow key={g.key} g={g} />
          ))}
        </ul>
      ) : (
        <p className="muted">No AI checks yet. Ask a question on Insights to see one here.</p>
      )}
      {error && (
        <p className="callout" role="alert">
          {error}
        </p>
      )}
      {((!all && !demo && data.state.usage.length > 0) || (all && all.length > limit)) && (
        <div className="card-action-row">
          {!all && !demo && data.state.usage.length > 0 && (
            <Button variant="ghost" size="sm" disabled={loading} onClick={() => void showAll()}>
              {loading ? "Loading…" : "Show all"}
            </Button>
          )}
          {all && all.length > limit && (
            <Button variant="ghost" size="sm" onClick={() => setLimit(limit + 20)}>
              Show 20 more
            </Button>
          )}
        </div>
      )}
    </section>
  );
}

/**
 * Checks from the last 15 days that have moved to the archive (the state keeps about 8 days), read from the paged list
 * only when the chart has tokens older than the state's checks, so it can still tell "found something" from "nothing
 * new" for those days. Read once per visit, not on every new check.
 */
function useArchivedChecks(needed: boolean) {
  const { api } = useApp();
  const [items, setItems] = useState<Investigation[]>([]);
  useEffect(() => {
    if (!needed) return;
    let cancelled = false;
    (async () => {
      const since = Date.now() - 15 * 86400000;
      const found: Investigation[] = [];
      let cursor: string | null = null;
      for (let page = 0; page < 5; page++) {
        const result: InvestigationPage = await api<InvestigationPage>(
          `/investigations?limit=100${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ""}`,
        );
        found.push(...(result.items.filter((i) => i.archived) as unknown as Investigation[]));
        const oldest = result.items.at(-1);
        if (!result.nextCursor || !oldest || Date.parse(oldest.at) < since) break;
        cursor = result.nextCursor;
      }
      if (!cancelled) setItems(found);
    })().catch(() => {
      // Without the archive the older days fall back to each run's own status; the chart still draws.
    });
    return () => {
      cancelled = true;
    };
  }, [api, needed]);
  return items;
}

/**
 * Setup › AI checks: what the AI did in the last day, tokens over two weeks by what checks found, when it checks
 * and what for, the provider and Predbat's live tools, and the recent runs. Dollar figures appear only for a priced API;
 * a ChatGPT plan is never shown as a cost.
 */
export function AiReviewsPanel() {
  const { api, data, timeZone } = useApp();
  const s = data.state;
  const demo = s.ai.provider === "Demo";
  const day = todaySummary(s, { timeZone });
  const oldestInState = Math.min(...s.investigations.map((i) => Date.parse(i.at)), Date.now());
  const since = Date.now() - 15 * 86400000;
  const archiveNeeded = s.usage.some(
    (u) => u.provider !== "Demo" && Date.parse(u.at) >= since && Date.parse(u.at) < oldestInState,
  );
  const archived = useArchivedChecks(archiveNeeded);
  const bars = usageByDay(s.usage, [...s.investigations, ...archived], { timeZone });
  const anyTokens = bars.some((b) => b.total > 0);
  const priced = s.usage.filter((u) => u.provider === "Api" && u.estimatedUsd != null);
  const spent = priced.reduce((n, u) => n + (u.estimatedUsd ?? 0), 0);
  return (
    <div className="ai-page">
      <div className="ai-columns">
        <div className="ai-column">
          <ProviderForm />
          <Schedule />
        </div>
        <div className="ai-column">
          <ObjectivePicker />
          <McpConnection
            api={api}
            demo={data.connection.demo}
            recorded={data.ai.mcp}
            lastUsed={mcpLastUsed(s.investigations)}
          />
        </div>
      </div>
      <section className="ai-summary" aria-labelledby="summary-heading">
        <h2 id="summary-heading" className="ai-summary-title">
          Today
        </h2>
        <dl className={`ai-stats${demo ? " is-three" : ""}`}>
          <div>
            <dt>AI checks</dt>
            <dd>{day.checks}</dd>
          </div>
          <div>
            <dt>Found something</dt>
            <dd>{day.found}</dd>
          </div>
          <div>
            <dt>Didn't finish</dt>
            <dd>{day.unfinished}</dd>
          </div>
          {!demo && (
            <div>
              <dt>Tokens</dt>
              <dd>{day.tokens ? compact(day.tokens) : "0"}</dd>
            </div>
          )}
        </dl>
        <p className="ai-summary-note">
          {day.quick > 0 && `Plus ${day.quick} quick ${day.quick === 1 ? "check" : "checks"} that didn't need the AI. `}
          {s.usage.some((u) => /fail/i.test(u.status) && !u.inputTokens && !u.outputTokens) &&
            "Checks that didn't finish may still use some of your allowance without reporting tokens. "}
          {s.ai.provider === "ChatGpt"
            ? "Included in your ChatGPT plan: token counts help you stay inside its limits."
            : priced.length
              ? `About ${usd(spent)} so far at your token prices (an estimate, not your bill).`
              : s.ai.provider === "Demo"
                ? "Demo checks are free."
                : "Add your token prices below to see an estimate in US dollars."}
        </p>
      </section>
      {anyTokens ? (
        <TokenBars bars={bars} title="Tokens per day" />
      ) : (
        !demo && <p className="token-empty">No AI tokens used in the last 14 days.</p>
      )}
      <RecentRuns />
    </div>
  );
}

/** Kept as the default export: SetupPage renders it for #/setup/ai. */
export default AiReviewsPanel;
