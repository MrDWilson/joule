/**
 * Energy › Saved reports. Reports are made from the meters (no AI): a daily one for yesterday at 08:00 by default, a weekly one
 * if wanted, and any period saved from the figures above. A report stores only its period; opening it asks the server to
 * recompute its figures and written summary, so later accounting fixes reach old reports too. Opening it also marks it read
 * (with its notification): there is one read state.
 */
import { useEffect, useRef, useState } from "react";
import { ChevronDown, FileText } from "lucide-react";
import { useApp } from "../context/AppContext";
import { Button, Disclosure, EmptyState, ErrorNotice, LoadingBlock, Switch } from "./ui";
import { PlainText } from "./PlainText";
import { buildHash, navigate } from "../lib/router";
import { dayTime } from "../lib/time";
import {
  homeStatus,
  homeUse,
  isEmptyReport,
  metricColor,
  metricValue,
  netCost,
  reportMeta,
  reportTitle,
} from "../lib/energy";
import { periodQuery, zonedDayOf } from "../lib/period";
import type { EnergyReport, ReportPreferences, ReportView } from "../completion-types";
import { plural } from "../lib/copy";

const SHOW = 5;

/** "A daily report for yesterday arrives at 08:00. A weekly one arrives on Mondays." */
export function scheduleLine(p: ReportPreferences) {
  const hour = `${String(p.hourLocal).padStart(2, "0")}:00`;
  if (p.dailyEnabled && p.weeklyEnabled)
    return `A daily report for yesterday arrives at ${hour}, and a weekly one on Mondays.`;
  if (p.dailyEnabled) return `A daily report for yesterday arrives at ${hour}.`;
  if (p.weeklyEnabled) return `A weekly report for last week arrives on Mondays at ${hour}.`;
  return "Automatic reports are off.";
}

function ScheduleForm({ prefs }: { prefs: ReportPreferences }) {
  const { mutate, busy } = useApp();
  const save = (next: Partial<ReportPreferences>) =>
    void mutate("/reports/preferences", { ...prefs, ...next }, "Report schedule saved.");
  return (
    <div className="report-schedule">
      <div className="report-schedule-row">
        <span>Daily report</span>
        <Switch
          label="Daily reports"
          checked={prefs.dailyEnabled}
          disabled={busy}
          onCheckedChange={(v) => save({ dailyEnabled: v })}
        />
      </div>
      <div className="report-schedule-row">
        <span>Weekly report (Mondays)</span>
        <Switch
          label="Weekly reports"
          checked={prefs.weeklyEnabled}
          disabled={busy}
          onCheckedChange={(v) => save({ weeklyEnabled: v })}
        />
      </div>
      <label className="report-schedule-row">
        <span>Arrives at</span>
        <select
          aria-label="Report delivery hour"
          value={prefs.hourLocal}
          disabled={busy}
          onChange={(e) => save({ hourLocal: Number(e.target.value) })}
        >
          {Array.from({ length: 24 }, (_, h) => (
            <option key={h} value={h}>
              {String(h).padStart(2, "0")}:00
            </option>
          ))}
        </select>
      </label>
    </div>
  );
}

function ReportFigures({ view }: { view: ReportView }) {
  const s = view.energySummary;
  const net = netCost(s, { timeZone: view.timeZone });
  const home = homeUse(s);
  const multiDay = view.days.length > 1;
  const homeNote = homeStatus(s, { timeZone: view.timeZone, multiDay, singleDay: !multiDay });
  const paid = (net.paid || net.earned) && (
    <small className="report-paid">
      {net.paid && <span>Paid {net.paid}</span>}
      {net.paid && net.earned && (
        <span className="report-paid-sep" aria-hidden="true">
          {" · "}
        </span>
      )}
      {net.earned && <span>Earned {net.earned}</span>}
    </small>
  );
  // One line per note ("16.9 kWh including the car", "Missing 11:00–13:30"), never joined into one long run.
  const homeLines = [home.note, ...(homeNote?.split(" · ") ?? [])].filter(Boolean) as string[];
  const items = [
    { key: "net", label: net.label, value: net.text, sub: paid },
    {
      key: "load",
      label: "Home use",
      value: metricValue(home.metric),
      sub: homeLines.map((l) => <small key={l}>{l}</small>),
    },
    { key: "pv", label: "Solar", value: metricValue(s.metrics.pv), sub: null },
    { key: "grid_import", label: "Grid import", value: metricValue(s.metrics.grid_import), sub: null },
    { key: "grid_export", label: "Grid export", value: metricValue(s.metrics.grid_export), sub: null },
  ];
  return (
    <dl className="report-figures">
      {items.map((i) => (
        <div key={i.key} className={`report-figure-${i.key}`}>
          <dt>
            {i.key !== "net" && (
              <i className="energy-swatch" style={{ background: metricColor[i.key] }} aria-hidden="true" />
            )}
            {i.label}
          </dt>
          <dd>
            <strong>{i.value}</strong>
            {i.sub}
          </dd>
        </div>
      ))}
    </dl>
  );
}

function ReportItem({
  report,
  open,
  onToggle,
  highlight,
}: {
  report: EnergyReport;
  open: boolean;
  onToggle: (open: boolean) => void;
  highlight: boolean;
}) {
  const { api, load, timeZone, data } = useApp();
  const [view, setView] = useState<ReportView | null>(null);
  const [error, setError] = useState<unknown>(null);
  const tz = report.timeZone || timeZone;
  const title = reportTitle(report, { timeZone: tz });
  const meta = reportMeta(report, data.state.reportPreferences.hourLocal, { timeZone: tz });
  const unread = !report.readAt;

  useEffect(() => {
    if (!open) return;
    let active = true;
    setError(null);
    api<ReportView>(`/reports/${encodeURIComponent(report.id)}`)
      .then((v) => active && setView(v))
      .catch((e) => active && setError(e));
    if (unread)
      // Opening a report is reading it: one read state for the report and its notification.
      api(`/reports/${encodeURIComponent(report.id)}/read`, {})
        .then(() => load())
        .catch(() => {});
    return () => {
      active = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, report.id]);

  const showDays = () => {
    const from = zonedDayOf(new Date(report.from), tz),
      to = zonedDayOf(new Date(Date.parse(report.to) - 1), tz);
    navigate(buildHash("energy", "", {}, periodQuery("custom", from, to)));
    document.getElementById("main")?.scrollTo?.({ top: 0, behavior: "smooth" });
    window.scrollTo({ top: 0, behavior: "smooth" });
  };

  return (
    <li id={`report-${report.id}`} className={highlight ? "report-item is-new" : "report-item"}>
      <details open={open} onToggle={(e) => onToggle((e.currentTarget as HTMLDetailsElement).open)}>
        <summary>
          <FileText size={16} aria-hidden="true" className="report-icon" />
          <span className="report-title">
            <strong>{title}</strong>
            <span className="report-meta">
              {report.isDemo ? "Demo · " : ""}
              {meta}
            </span>
          </span>
          {unread && (
            <span className="report-unread">
              <i aria-hidden="true" />
              New
            </span>
          )}
          <ChevronDown size={16} aria-hidden="true" className="report-chevron" />
        </summary>
        <div className="report-body">
          {error ? (
            <ErrorNotice error={error} title="Couldn't open this report" />
          ) : !view ? (
            <LoadingBlock lines={3} label="Opening the report" />
          ) : (
            <>
              <p className="report-summary">
                <PlainText text={view.summary} />
              </p>
              {view.hasReadings && <ReportFigures view={view} />}
              <div className="report-actions">
                <Button variant="secondary" size="sm" onClick={showDays}>
                  Open these days in the chart
                </Button>
              </div>
              {view.relatedChecks.length > 0 && (
                <Disclosure summary="Related AI checks" count={view.relatedChecks.length} className="report-checks">
                  <ul>
                    {view.relatedChecks.map((c) => (
                      <li key={c.id}>
                        <a href={buildHash("insights", "", { id: c.id })}>
                          <PlainText text={c.title} />
                        </a>
                        <span className="muted"> · {dayTime(c.at, { timeZone: tz })}</span>
                      </li>
                    ))}
                  </ul>
                </Disclosure>
              )}
            </>
          )}
        </div>
      </details>
    </li>
  );
}

/**
 * The saved reports list with its schedule. `highlight` is a report just saved or asked for: it opens and scrolls into
 * view, again each time `focusKey` changes.
 */
export function SavedReports({
  highlight,
  focusKey,
  ready = true,
}: {
  highlight?: string | null;
  focusKey?: number;
  ready?: boolean;
}) {
  const { data, route } = useApp();
  const s = data.state;
  const prefs = s.reportPreferences;
  const [scheduleOpen, setScheduleOpen] = useState(false);
  const [all, setAll] = useState(false);
  const linked = route.query.get("report");
  const [openIds, setOpenIds] = useState<Set<string>>(() => new Set([highlight, linked].filter(Boolean) as string[]));
  const section = useRef<HTMLElement>(null);
  const reports = [...s.reports]
    .filter((r) => !isEmptyReport(r))
    .sort((a, b) => Date.parse(b.to) - Date.parse(a.to) || Date.parse(b.createdAt) - Date.parse(a.createdAt));
  const focus = highlight || linked;

  useEffect(() => {
    if (!focus || !ready) return;
    setOpenIds((ids) => new Set(ids).add(focus));
    const index = reports.findIndex((r) => r.id === focus);
    if (index >= SHOW) setAll(true);
    requestAnimationFrame(() =>
      document.getElementById(`report-${focus}`)?.scrollIntoView({ block: "center", behavior: "smooth" }),
    );
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [focus, focusKey, ready]);

  const visible = all ? reports : reports.slice(0, SHOW);
  const unread = reports.filter((r) => !r.readAt).length;
  return (
    <section className="panel saved-reports" id="saved-reports" ref={section} aria-labelledby="saved-reports-title">
      <div className="saved-reports-head">
        <div>
          <h2 id="saved-reports-title">
            Saved reports
            {unread > 0 && <span className="report-count">{unread} new</span>}
          </h2>
          <p>{scheduleLine(prefs)} Straight from your meters.</p>
        </div>
        <Button
          variant="secondary"
          size="sm"
          aria-expanded={scheduleOpen}
          onClick={() => setScheduleOpen(!scheduleOpen)}
        >
          Schedule
        </Button>
      </div>
      {scheduleOpen && <ScheduleForm prefs={prefs} />}
      {reports.length ? (
        <ul className="report-list">
          {visible.map((r) => (
            <ReportItem
              key={r.id}
              report={r}
              highlight={r.id === highlight}
              open={openIds.has(r.id)}
              onToggle={(open) =>
                setOpenIds((ids) => {
                  const next = new Set(ids);
                  if (open) next.add(r.id);
                  else next.delete(r.id);
                  return next;
                })
              }
            />
          ))}
        </ul>
      ) : (
        <EmptyState quiet title="No reports yet">
          {prefs.dailyEnabled
            ? `The first daily report arrives at ${String(prefs.hourLocal).padStart(2, "0")}:00. You can also save the period above.`
            : "Save the period above as a report, or turn on the schedule."}
        </EmptyState>
      )}
      {reports.length > SHOW && (
        <Button variant="ghost" size="sm" onClick={() => setAll(!all)}>
          {all ? "Show fewer" : `Show all ${plural(reports.length, "report")}`}
        </Button>
      )}
    </section>
  );
}
