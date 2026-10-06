/**
 * Energy › figures and sensors. What the meters recorded for a period (presets that load straight away), two headline
 * figures, six compact chips in the chart colours, the day-by-day chart, plain notes about this period, and the sensors as a
 * one-line health strip. The wording rules live in lib/energy.ts; this file only lays things out.
 */
import { Fragment, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { Check, ChevronDown, Info, LoaderCircle, RefreshCw } from "lucide-react";
import { useApp } from "../context/AppContext";
import { Button, Chip, Disclosure, EmptyState, ErrorNotice, LoadingBlock, Segmented } from "./ui";
import { Section } from "./ui/Section";
import { DailyChart } from "./charts/DailyCharts";
import { coverage, energy, money } from "./charts/chartUtils";
import { stamp, clock, dayLabel } from "../lib/time";
import { Delta } from "./Delta";
import { metricLabel, sourceName } from "../lib/labels";
import { navigate, buildHash } from "../lib/router";
import { kwh } from "../lib/format";
import {
  PERIOD_PRESETS,
  addDays,
  clippedNote,
  customRangeError,
  periodFromQuery,
  periodQuery,
  presetDays,
  previousOf,
  resolvePeriod,
  zonedDayOf,
  type EnergyPeriod,
  type PeriodPreset,
} from "../lib/period";
import {
  CHIP_METRICS,
  change,
  homeStatus,
  homeUse,
  metricColor,
  metricStatus,
  metricValue,
  netCost,
  periodNotes,
  reconcile,
  sensorHeadline,
  sensorRows,
  type Change,
  type SensorRow,
} from "../lib/energy";
import type { EnergySummary, TelemetryStatus } from "../completion-types";
import "../pages/energy.css";

// Shared helpers, re-exported for the components that still import them from here.
export { DailyChart, Delta, Section, stamp, metricLabel, sourceName };
export { when } from "../lib/time";
export { coverage, energy, money };
export const kwhText = (value: unknown) => (typeof value === "number" ? kwh(value, { precision: "table" }) : "—");

/** The period's days, short, for phones: "5 Oct", "2–5 Oct", "29 Sep – 5 Oct". */
export function shortPeriodText(from: Date, end: Date, timeZone?: string) {
  // "Mon 5 Oct" → "5 Oct".
  const short = (d: Date) => dayLabel(d, { timeZone }).replace(/^\S+ /, "");
  const a = short(from),
    b = short(new Date(end.getTime() - 1));
  if (a === b) return a;
  const [dayA, monthA] = a.split(" ");
  return monthA === b.split(" ")[1] ? `${dayA}–${b}` : `${a} – ${b}`;
}

/** A period in local time: "Sun 4 Oct", "Fri 2 Oct – Mon 5 Oct", "Mon 5 Oct, 00:00–10:43". */
export function periodText(from: string, to: string, timeZone?: string) {
  const a = new Date(from),
    b = new Date(to);
  const midnight = (d: Date) => clock(d, { timeZone }) === "00:00";
  const first = dayLabel(a, { timeZone }),
    last = dayLabel(new Date(b.getTime() - 1), { timeZone });
  if (midnight(a) && midnight(b)) return first === last ? first : `${first} – ${last}`;
  if (first === dayLabel(b, { timeZone })) return `${first}, ${clock(a, { timeZone })}–${clock(b, { timeZone })}`;
  return `${first} – ${dayLabel(b, { timeZone })}, ${clock(b, { timeZone })}`;
}

interface Figures {
  key: string;
  period: EnergyPeriod;
  summary: EnergySummary;
  days: EnergySummary[];
  previous: { summary: EnergySummary | null; days: EnergySummary[]; label: string } | null;
  /** The earlier period was asked for but couldn't be loaded (not the same as there being none). */
  previousFailed: boolean;
}

/**
 * Why the meter readings couldn't be fetched, when they couldn't (the shell's telemetry snapshot carries `error` once a
 * request fails), so a failure reads as a failure rather than a loading placeholder that never ends.
 */
function telemetryError(measured: object): unknown {
  const e = (measured as { error?: unknown }).error;
  return e == null || e === "" ? null : e;
}

/**
 * Whether the meter readings failed to load, with a way to try again. Uses the shell's error when it has one; otherwise,
 * if no readings have arrived after a few seconds, asks the server once itself, so a failure never looks like loading.
 */
function useTelemetryFailure() {
  const { api, measured } = useApp();
  const has = !!measured.telemetry;
  const [failure, setFailure] = useState<unknown>(null);
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    setFailure(null);
    if (has) return;
    let live = true;
    const timer = setTimeout(() => {
      api("/telemetry/status").catch((e) => live && setFailure(e));
    }, 3000);
    return () => {
      live = false;
      clearTimeout(timer);
    };
  }, [api, has, attempt]);
  const error = has ? null : (telemetryError(measured) ?? failure);
  const retry = () => {
    setAttempt((n) => n + 1);
    measured.retry();
  };
  return { error, retry };
}

/** The longest period a saved report can cover (the server's limit). */
const REPORT_MAX_DAYS = 32;

/** The period chosen in the address bar, plus the custom dates being typed. */
function usePeriod(
  timeZone: string,
  firstObservationAt: string | null | undefined,
  lastCollection: string | null | undefined,
) {
  const { route } = useApp();
  const choice = periodFromQuery(route.query);
  const today = zonedDayOf(Date.now(), timeZone);
  const days =
    choice.preset === "custom"
      ? { fromDay: choice.fromDay ?? addDays(today, -6), toDay: choice.toDay ?? today }
      : presetDays(choice.preset, today);
  // "Now" moves on only when a new reading arrives, so the window (and its requests) stays put between collections.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const now = useMemo(() => new Date(), [lastCollection, choice.preset, days.fromDay, days.toDay, today]);
  const period = useMemo(
    () => resolvePeriod(choice.preset, days.fromDay, days.toDay, timeZone, now, firstObservationAt, lastCollection),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [choice.preset, days.fromDay, days.toDay, timeZone, now, firstObservationAt],
  );
  // A custom range from the address bar (a bookmark, or Back to a range that is no longer valid) is checked like typed dates.
  const rangeError =
    choice.preset === "custom" ? customRangeError(choice.fromDay ?? "", choice.toDay ?? "", today) : null;
  const select = (preset: PeriodPreset, fromDay?: string, toDay?: string) => {
    const q =
      preset === "custom" ? periodQuery("custom", fromDay ?? days.fromDay, toDay ?? days.toDay) : periodQuery(preset);
    navigate(buildHash("energy", "", {}, q), { replace: true });
  };
  return { period, today, select, rangeError, asked: days };
}

/** Measured figures for the chosen period. Keeps the last figures on screen while a refresh or a new period loads. */
export function EnergyFigures({
  onSaveReport,
  onOpenReport,
  saving = false,
  onReady,
}: {
  onSaveReport?: (p: EnergyPeriod) => void;
  /** Opens a report already saved for exactly this period (instead of saving the same one again). */
  onOpenReport?: (id: string) => void;
  saving?: boolean;
  /** Called once the first figures (or a message instead of them) are on screen, so the page can scroll below them. */
  onReady?: () => void;
}) {
  const { api, measured, data, timeZone: appZone } = useApp();
  const status = measured.telemetry;
  const failure = useTelemetryFailure();
  const timeZone = status?.timeZone ?? appZone;
  const first = status?.firstObservationAt;
  const { period, today, select, rangeError, asked } = usePeriod(timeZone, first, status?.lastCollection);
  const [figures, setFigures] = useState<Figures | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const firstDay = first ? zonedDayOf(new Date(first), timeZone) : undefined;
  // The From box never shows a day before records began, so it agrees with the dates the figures cover.
  const clampDay = (day: string) => (firstDay && day < firstDay && asked.toDay >= firstDay ? firstDay : day);
  const [draft, setDraft] = useState({ from: clampDay(asked.fromDay), to: asked.toDay });
  const latest = useRef(0);
  const key = `${period.from.toISOString()}|${period.to.toISOString()}`;
  const saved = (data.state.reports ?? []).find(
    (r) => Date.parse(r.from) === period.from.getTime() && Date.parse(r.to) === period.to.getTime(),
  );

  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => setDraft({ from: clampDay(asked.fromDay), to: asked.toDay }), [asked.fromDay, asked.toDay, firstDay]);

  useEffect(() => {
    if (!status || period.beforeRecords || rangeError) return;
    const request = ++latest.current;
    const params = (from: Date, to: Date) => new URLSearchParams({ from: from.toISOString(), to: to.toISOString() });
    const prev = previousOf(period, timeZone, first);
    const many = period.days > 1;
    setLoading(true);
    setError(null);
    Promise.all([
      api<EnergySummary>(`/telemetry/summary?${params(period.from, period.to)}`),
      many ? api<EnergySummary[]>(`/telemetry/daily?${params(period.from, period.to)}`) : Promise.resolve(null),
      prev
        ? Promise.all([
            api<EnergySummary>(`/telemetry/summary?${params(prev.from, prev.to)}`),
            many ? api<EnergySummary[]>(`/telemetry/daily?${params(prev.from, prev.to)}`) : Promise.resolve([]),
          ])
            .then(([summary, days]) => ({ summary, days, label: prev.label }))
            .catch(() => "failed" as const)
        : Promise.resolve(null),
    ])
      .then(([summary, days, earlier]) => {
        if (request !== latest.current) return;
        const previous = earlier === "failed" ? null : earlier;
        setFigures({ key, period, summary, days: days ?? [summary], previous, previousFailed: earlier === "failed" });
      })
      .catch((e) => {
        if (request === latest.current) setError(e);
      })
      .finally(() => {
        if (request === latest.current) setLoading(false);
      });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, key, !!status, !!rangeError]);

  const draftError = period.preset === "custom" ? (customRangeError(draft.from, draft.to, today) ?? rangeError) : null;
  const applyDraft = (from: string, to: string) => {
    setDraft({ from, to });
    if (!customRangeError(from, to, today)) select("custom", from, to);
  };
  const shown = figures;
  const stale = loading || (shown && shown.key !== key);
  const settled = !!shown || !!error || period.beforeRecords || !!rangeError;
  useEffect(() => {
    if (settled) onReady?.();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [settled]);

  return (
    <section className="panel energy-figures" aria-labelledby="energy-figures-title" aria-busy={loading || undefined}>
      <h2 id="energy-figures-title" className="sr-only">
        Measured energy
      </h2>
      <div className="energy-toolbar">
        <Segmented
          label="Period"
          options={PERIOD_PRESETS}
          value={period.preset}
          onChange={(p) => (p === "custom" ? select("custom", period.fromDay, period.toDay) : select(p))}
        />
        <div className="energy-asof" aria-live="polite">
          {!rangeError && (
            <span className="energy-dates">
              {stale && <LoaderCircle className="spin" size={14} aria-hidden="true" />}
              <span className="energy-dates-long">
                {periodText(period.from.toISOString(), period.end.toISOString(), timeZone)}
                {period.includesToday && <> · up to {clock(period.to, { timeZone })}</>}
              </span>
              <span className="energy-dates-short" aria-hidden="true">
                {shortPeriodText(period.from, period.end, timeZone)}
                {period.includesToday && <> · to {clock(period.to, { timeZone })}</>}
              </span>
            </span>
          )}
          {shown &&
            !period.beforeRecords &&
            !rangeError &&
            (saved && onOpenReport ? (
              <Button variant="ghost" size="sm" onClick={() => onOpenReport(saved.id)}>
                Open saved report
              </Button>
            ) : (
              onSaveReport &&
              period.days <= REPORT_MAX_DAYS && (
                <Button variant="ghost" size="sm" disabled={saving} onClick={() => onSaveReport(period)}>
                  Save as report
                </Button>
              )
            ))}
        </div>
      </div>
      {period.preset === "custom" && (
        <div className="energy-custom">
          <label className="field">
            From
            <input
              type="date"
              aria-label="Energy from date"
              value={draft.from}
              min={firstDay}
              max={today}
              onChange={(e) => applyDraft(e.target.value, draft.to)}
            />
          </label>
          <label className="field">
            To
            <input
              type="date"
              aria-label="Energy to date"
              value={draft.to}
              min={firstDay}
              max={today}
              onChange={(e) => applyDraft(draft.from, e.target.value)}
            />
          </label>
          {draftError && (
            <p role="alert" className="energy-custom-error">
              {draftError}
            </p>
          )}
        </div>
      )}
      {shown && shown.key === key && !rangeError && !period.beforeRecords && (
        <PeriodNote figures={shown} period={period} timeZone={timeZone} first={first} />
      )}
      {rangeError ? null : !status && failure.error ? (
        <ErrorNotice error={failure.error} title="Couldn't load your meter readings" onRetry={failure.retry} />
      ) : !status ? (
        <LoadingBlock lines={4} label="Loading your meter readings" />
      ) : period.beforeRecords ? (
        <EmptyState
          title="No records for these dates"
          action={
            <Button variant="secondary" size="sm" onClick={() => select("7d")}>
              Show the last 7 days
            </Button>
          }
        >
          {first ? `Records start ${dayLabel(first, { timeZone })}.` : "Joule has no meter readings yet."}
        </EmptyState>
      ) : error && !shown ? (
        <ErrorNotice error={error} title="Couldn't load the figures" onRetry={() => select(period.preset)} />
      ) : !shown ? (
        <LoadingBlock lines={4} label="Loading your meter readings" />
      ) : (
        <FigureBody figures={shown} timeZone={timeZone} status={status} stale={!!stale} />
      )}
    </section>
  );
}

/** Text whose time ranges ("11:00–13:30") never break across lines. */
export function KeepTimes({ text }: { text: string }) {
  const parts = text.split(/(\d{1,2}:\d{2}\s?–\s?\d{1,2}:\d{2})/);
  // One wrapping span, so inside a flex container (a chip) the text still flows as one line of words.
  return (
    <span>
      {parts.map((p, i) =>
        i % 2 ? (
          <span key={i} className="nowrap">
            {p}
          </span>
        ) : (
          p
        ),
      )}
    </span>
  );
}

function FigureBody({
  figures,
  timeZone,
  status,
  stale,
}: {
  figures: Figures;
  timeZone: string;
  status: TelemetryStatus;
  stale: boolean;
}) {
  const { api } = useApp();
  const { summary, previous, period } = figures;
  const options = { timeZone };
  const net = netCost(summary, options);
  const home = homeUse(summary);
  const hasReadings = Object.values(summary.metrics).some((m) => m.energyKwh != null && m.coverageFraction > 0);
  const prev = previous?.summary ?? null;
  const label = previous?.label ?? "";
  const netChange = prev
    ? change("cost", net.value, netCost(prev).value, label, {
        money: true,
        currentCoverage: Math.min(summary.importCostCoverage ?? 1, summary.exportCostCoverage ?? 1),
        previousCoverage: Math.min(prev.importCostCoverage ?? 1, prev.exportCostCoverage ?? 1),
      })
    : null;
  const prevHome = prev ? homeUse(prev) : null;
  const homeChange = prevHome
    ? change("load", home.value, prevHome.value, label, {
        currentCoverage: home.metric?.coverageFraction,
        previousCoverage: prevHome.metric?.coverageFraction,
      })
    : null;
  const singleDay = period.days === 1;
  const matches = singleDay ? reconcile(summary, options) : { matched: [], differ: [] };
  const homeNote = homeStatus(summary, { ...options, multiDay: !singleDay, singleDay });
  const notes = periodNotes(summary, options);

  if (!hasReadings)
    return (
      <EmptyState title="No meter readings for this period">
        {status.configured || status.demo
          ? "Nothing was recorded on these dates."
          : "Once your Home Assistant meters are set up, Joule reads them every 5 minutes."}
      </EmptyState>
    );

  return (
    <div className={stale ? "energy-body is-stale" : "energy-body"}>
      <div className="energy-summary">
        <div className="energy-hero">
          <HeroFigure
            label={net.label}
            value={net.text}
            accent={net.earnings ? "battery" : "neutral"}
            className={net.earnings ? "is-earnings" : undefined}
            sub={
              (net.paid || net.earned) && (
                <span className="energy-paid">
                  {net.paid && <span>Paid {net.paid}</span>}
                  {net.paid && net.earned && (
                    <span className="energy-paid-sep" aria-hidden="true">
                      {" · "}
                    </span>
                  )}
                  {net.earned && <span>Earned {net.earned}</span>}
                </span>
              )
            }
            note={net.note}
            delta={netChange && <ChangeText change={netChange} label={label} />}
          />
          <HeroFigure
            label="Home use"
            value={home.value == null ? "—" : metricValue(home.metric).replace(/ kWh$/, "")}
            unit={home.value == null ? undefined : "kWh"}
            accent="load"
            sub={home.note}
            note={homeNote}
            delta={homeChange && <ChangeText change={homeChange} label={label} />}
          />
        </div>
        <div className="energy-chips" role="list" aria-label="Meters">
          {CHIP_METRICS.map((key) => {
            const m = summary.metrics[key];
            if (key === "ev" && !m) return null;
            const s = metricStatus(key, m, { ...options, multiDay: period.days > 1 });
            const p = prev?.metrics[key];
            const c = prev
              ? change(key, m?.energyKwh, p?.energyKwh, label, {
                  currentCoverage: m?.coverageFraction,
                  previousCoverage: p?.coverageFraction,
                })
              : null;
            const differ = matches.differ.find((d) => d.metric === key);
            return (
              <div
                role="listitem"
                key={key}
                className="energy-chip"
                aria-label={metricLabel(key)}
                title={
                  m?.energyKwh != null ? `${metricLabel(key)}: ${kwh(m.energyKwh, { precision: "table" })}` : undefined
                }
              >
                <span className="energy-chip-label">
                  <i className="energy-swatch" style={{ background: metricColor[key] }} aria-hidden="true" />
                  {metricLabel(key)}
                </span>
                <strong className="energy-chip-value">{metricValue(m)}</strong>
                <span className="energy-chip-foot">
                  {s ? (
                    <Chip tone={s.tone} title={s.title}>
                      <KeepTimes text={s.label} />
                    </Chip>
                  ) : c ? (
                    <ChangeText change={c} label={label} compact />
                  ) : null}
                  {differ && (
                    <span className="energy-chip-meter" title={`The meter says ${differ.meter}; ${differ.reason}.`}>
                      Meter says {differ.meter}
                      {s ? "" : ` · ${differ.reason}`}
                    </span>
                  )}
                </span>
              </div>
            );
          })}
        </div>
      </div>
      <DailyChart
        days={figures.days}
        timeZone={timeZone}
        previous={previous?.days.length === figures.days.length ? previous.days : undefined}
        previousLabel={label || undefined}
        api={api}
      />
      {notes.notes.length > 0 && (
        <ul className="energy-notes" aria-label="About this period">
          {notes.notes.map((n) => (
            <li key={n}>
              <Info size={15} aria-hidden="true" />
              <span>
                <KeepTimes text={n} />
              </span>
            </li>
          ))}
          {notes.more > 0 && <li className="muted">and {notes.more} more like these</li>}
        </ul>
      )}
      <HowFiguresWork demo={status.demo} car={!!summary.loadIncludesEv} />
    </div>
  );
}

/**
 * The line under the period bar: why the period is shorter than asked ("Only 4 days recorded so far (since Fri 2 Oct)"),
 * what the changes compare with, and for a single day whether Home Assistant's own meter totals agree.
 */
function PeriodNote({
  figures,
  period,
  timeZone,
  first,
}: {
  figures: Figures;
  period: EnergyPeriod;
  timeZone: string;
  first?: string | null;
}) {
  const options = { timeZone };
  const matches = period.days === 1 ? reconcile(figures.summary, options) : { matched: [], differ: [] };
  const clipped = clippedNote(period, timeZone);
  const startsLate = periodNotes(figures.summary, options).notes.some((n) => n.startsWith("Records start"));
  const compare = figures.previousFailed
    ? "Couldn’t load the earlier period to compare with"
    : figures.previous
      ? `Compared with ${figures.previous.label}`
      : clipped || startsLate || !first
        ? "Nothing earlier to compare with"
        : `Nothing earlier to compare with: records start ${dayLabel(first, options)}`;
  return (
    <p className="energy-period-note">
      {clipped && <span>{clipped}</span>}
      <span>{compare}</span>
      {matches.matched.length > 0 && matches.differ.length === 0 && (
        <span className="energy-match">
          <Check size={14} aria-hidden="true" /> Matches Home Assistant’s meters
        </span>
      )}
    </p>
  );
}

/** A headline figure: label, the number at 32px, the line that explains it, then the change against the earlier period. */
function HeroFigure({
  label,
  value,
  unit,
  sub,
  note,
  delta,
  accent,
  className,
}: {
  label: string;
  value: string;
  unit?: string;
  sub?: ReactNode;
  note?: string | null;
  delta?: ReactNode;
  accent: "neutral" | "load" | "battery";
  className?: string;
}) {
  return (
    <article className={`stat stat-md accent-${accent} energy-hero-stat ${className ?? ""}`} aria-label={label}>
      <span className="stat-label">{label}</span>
      <div className="stat-value">
        <span>{value}</span>
        {unit && <small>{unit}</small>}
      </div>
      {sub && <p className="energy-hero-sub">{sub}</p>}
      {/* "Missing 11:00–13:30 · House meter says 35.9 kWh": one line each, so a narrow card never starts a line with "·". */}
      {note?.split(" · ").map((line) => (
        <p key={line} className="energy-hero-note">
          <KeepTimes text={line} />
        </p>
      ))}
      {delta && <p className="energy-hero-delta">{delta}</p>}
    </article>
  );
}

function ChangeText({ change: c, label, compact = false }: { change: Change; label: string; compact?: boolean }) {
  if (c.text === "Same")
    return (
      <span className="energy-change" title={c.title}>
        {compact ? "No change" : `Same as ${label}`}
      </span>
    );
  // "£5.99 better than this time yesterday", "6.0 kWh more (+12%)": the word carries the direction, coloured when it is
  // good or bad news for this figure.
  return (
    <span className="energy-change" title={c.title}>
      <span className="sr-only">{c.title}</span>
      <span aria-hidden="true">
        <span className="energy-change-amount">{c.amount}</span>{" "}
        <span className={`energy-change-word tone-${c.tone}`}>{c.word}</span>
        {!compact && ` than ${label}`}
        {c.pct && <span className="energy-change-pct">{c.pct}</span>}
      </span>
    </span>
  );
}

/** The plain explainer: four bullets, with the server settings behind "For installers". */
export function HowFiguresWork({ demo = false, car = false }: { demo?: boolean; car?: boolean }) {
  return (
    <Disclosure summary="How these figures work" className="energy-how">
      <ul className="energy-how-list">
        <li>
          {demo
            ? "In the demo the readings are made up. On your install, Joule reads your Home Assistant meters every 5 minutes."
            : "Joule reads your Home Assistant meters every 5 minutes and takes one reading from the next to get the energy in between."}
        </li>
        <li>
          If a meter is offline for up to 2 hours, the energy it recorded meanwhile is shared across that time and
          marked ≈. Longer outages are left out, never guessed, and named under the chart.
        </li>
        <li>Costs use your Octopus rate at the time of each reading.</li>
        <li>Standing charges aren’t included.</li>
        {car && (
          <li>Home use leaves out the car: your house meter includes its charging, so the car has its own figure.</li>
        )}
      </ul>
      <HomeAssistantSetupHelp />
    </Disclosure>
  );
}

/** Where the sensors are configured, for installers. Closed until asked for. */
export function HomeAssistantSetupHelp({ summary = "For installers" }: { summary?: string }) {
  return (
    <Disclosure summary={summary} className="energy-installers">
      <p>
        Joule reads the sensors named in its server settings: <code>HomeAssistant__BaseUrl</code> and{" "}
        <code>HomeAssistant__AccessToken</code>, plus one <code>HomeAssistant__Entities__…</code> line per meter (Load,
        Pv, GridImport, GridExport, BatteryCharge, BatteryDischarge, Ev, Soc, ImportTariff, ExportTariff).
      </p>
      <p>
        Energy meters must report kWh, Wh or MWh, the battery level a percentage and prices pence per kWh. Without a
        Home Assistant token, Joule reads the same sensors through Predbat.
      </p>
    </Disclosure>
  );
}

// ------------------------------------------------------------------ sensors

/** Breaks a long entity id only at "." and "_" (never mid-word). */
function breakable(id: string): ReactNode {
  return id.split(/(?<=[._])/).map((part, i) => (
    <Fragment key={i}>
      {part}
      <wbr />
    </Fragment>
  ));
}

function SensorItem({ row }: { row: SensorRow }) {
  return (
    <li>
      <details className={`sensor-row tone-${row.tone}`}>
        <summary>
          <i className="sensor-dot" aria-hidden="true" />
          <span className="sensor-main">
            <span className="sensor-name">{row.label}</span>
            <span className="sensor-meta">
              <span className="sensor-state">{row.stateLabel}</span>
              {row.when && <span> · {row.when}</span>}
            </span>
          </span>
          <span className="sensor-value">{row.value}</span>
          <ChevronDown className="sensor-chevron" size={16} aria-hidden="true" />
        </summary>
        <dl className="sensor-detail">
          {row.note && (
            <div className="sensor-note">
              <dt className="sr-only">Note</dt>
              <dd>{row.note}</dd>
            </div>
          )}
          <div>
            <dt>Sensor</dt>
            <dd>{row.entity ? <code>{breakable(row.entity)}</code> : "Not set up"}</dd>
          </div>
          {row.source && (
            <div>
              <dt>Read from</dt>
              <dd>{row.source}</dd>
            </div>
          )}
          {row.raw && (
            <div>
              <dt>Home Assistant says</dt>
              <dd>
                <code>{row.raw}</code>
              </dd>
            </div>
          )}
          {row.profile && (
            <div>
              <dt>Kind</dt>
              <dd>{row.profile}</dd>
            </div>
          )}
        </dl>
      </details>
    </li>
  );
}

/**
 * The sensors as a health strip: one line when all is well ("All 11 sensors OK · checked 2 min ago"), the sensors that need a
 * look listed underneath, and every sensor behind "Show all". Each row opens to show its entity id, source and raw state.
 */
export function SensorHealth({
  status,
  defaultOpen = false,
  collapsible = true,
}: {
  status: TelemetryStatus | null;
  defaultOpen?: boolean;
  /** False on Setup › Sensors: every sensor is listed, with no Show all / Hide. */
  collapsible?: boolean;
}) {
  const { mutate, measured, busy } = useApp();
  const [shown, setOpen] = useState(defaultOpen);
  const open = shown || !collapsible;
  const failure = useTelemetryFailure();
  const failed = !status && !!failure.error;
  const [reading, setReading] = useState(false);
  const rows = useMemo(() => sensorRows(status, { timeZone: status?.timeZone }), [status]);
  const problems = rows.filter((r) => !r.ok);
  const headline = sensorHeadline(rows, status?.lastCollection, { timeZone: status?.timeZone });
  const tone = failed
    ? "warn"
    : !rows.length
      ? "neutral"
      : problems.some((r) => r.tone === "danger")
        ? "danger"
        : problems.length
          ? "warn"
          : "success";
  return (
    <section className="panel sensor-health" aria-labelledby="sensor-health-title">
      <div className="sensor-health-head">
        <i className={`sensor-dot tone-${tone}`} aria-hidden="true" />
        <div className="sensor-health-title">
          <h2 id="sensor-health-title">Sensors</h2>
          <p>{status ? headline : failed ? "Couldn't read the sensors" : "Checking sensors…"}</p>
        </div>
        <div className="sensor-health-actions">
          {failed && (
            <Button variant="secondary" size="sm" onClick={failure.retry}>
              Try again
            </Button>
          )}
          <Button
            variant="ghost"
            size="sm"
            disabled={reading || busy}
            onClick={async () => {
              setReading(true);
              if (await mutate("/telemetry/collect", {}, "Sensors read.")) measured.retry();
              setReading(false);
            }}
          >
            <RefreshCw size={14} aria-hidden="true" className={reading ? "spin" : undefined} />
            Read now
          </Button>
          {collapsible && rows.length > 0 && (
            <Button variant="secondary" size="sm" aria-expanded={open} onClick={() => setOpen(!open)}>
              {open ? "Hide" : `Show all ${rows.length}`}
            </Button>
          )}
        </div>
      </div>
      {status?.demo && (
        <p className="sensor-health-demo">Demo readings. On your install these are your Home Assistant sensors.</p>
      )}
      {(open ? rows : problems).length > 0 && (
        <ul className="sensor-list" aria-label={open ? "All sensors" : "Sensors that need a look"}>
          {(open ? rows : problems).map((r) => (
            <SensorItem key={r.key} row={r} />
          ))}
        </ul>
      )}
    </section>
  );
}

/** Setup › Sensors: the same health strip, opened, with the setup help. */
export function SensorsSection({ status }: { status: TelemetryStatus | null }) {
  return <SensorHealth status={status} collapsible={false} />;
}

/** Kept for older imports: the figures for a period. */
export const DataPage = EnergyFigures;
