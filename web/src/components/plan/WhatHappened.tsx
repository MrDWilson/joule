import { Fragment, useEffect, useState } from "react";
import { AlertTriangle, ChevronDown, ChevronRight } from "lucide-react";
import type { Api, PlanEvidence } from "../../completion-types";
import { Hint } from "../Hint";
import { kwh, percent } from "../../lib/format";
import { clock } from "../../lib/time";
import { useMediaQuery, breakpoints } from "../../lib/useMediaQuery";
import { slotAction } from "../../lib/planActions";
import { ActionBadge } from "./ActionBadge";
import { dayWord, shortAction } from "./windows";
import {
  flagSentence,
  historyRows,
  historyWindows,
  type HistoryRow,
  type HistoryWindow,
  type Measured,
} from "./history";
import type { PlanSlotLike } from "./windows";
import { useOverflow } from "./useOverflow";

const HOUR = 3600000;

/**
 * GET only: the meter evidence (grid in and out, battery, car) for each half-hour of the last day, from the plan made
 * about 24 hours ago (its slots cover the whole day since). Refetched every half-hour; a failure leaves the grid
 * columns empty.
 */
export function useDayEvidence(api: Api, now: number) {
  const [evidence, setEvidence] = useState<PlanEvidence | null>(null);
  const halfHour = Math.floor(now / (HOUR / 2));
  useEffect(() => {
    let active = true;
    const at = halfHour * (HOUR / 2);
    const params = new URLSearchParams({
      from: new Date(at - 25 * HOUR).toISOString(),
      to: new Date(at - 23.5 * HOUR).toISOString(),
      limit: "1",
    });
    api<{ items: { id: string }[] }>(`/plans?${params}`)
      .then((page) =>
        page.items[0] ? api<PlanEvidence>(`/telemetry/plans/${encodeURIComponent(page.items[0].id)}/evidence`) : null,
      )
      .then((e) => active && setEvidence(e))
      .catch(() => active && setEvidence(null));
    return () => {
      active = false;
    };
  }, [api, halfHour]);
  return evidence;
}

const energy = (m: Measured, forecast?: number | null, night?: boolean) => {
  if (m.value == null) {
    if (night && (forecast ?? 0) < 0.01) return <span className="muted">0 (asleep)</span>;
    return <span className="muted">—</span>;
  }
  return (
    <>
      {m.approx ? "≈" : ""}
      {kwh(m.value, { precision: "table", unit: false })}
    </>
  );
};
const plain = (v: number | null) =>
  v == null ? <span className="muted">—</span> : kwh(v, { precision: "table", unit: false });
const socPair = (a: number | null, b: number | null) =>
  a != null && b != null ? `${percent(a)} → ${percent(b)}` : a != null ? percent(a) : "—";
/** The measured end level against the planned one, in points, when both are known. */
const miss = (soc: HistoryRow["soc"]) =>
  soc.end != null && soc.plannedEnd != null ? Math.round(soc.end - soc.plannedEnd) : null;
/**
 * How far the measured end level missed the plan, as "−6 pts" (read out as "Ended 6 points below plan"). Under 5 points
 * is noise and shows nothing; under 10 is worth a glance (neutral); 10 or more is a real miss (amber below plan, green
 * above).
 */
export function DeltaPill({ off }: { off: number | null }) {
  if (off == null || Math.abs(off) < 5) return null;
  const size = Math.abs(off);
  const tone = size < 10 ? " neutral" : off < 0 ? " below" : "";
  return (
    <span
      className={`delta-pill${tone}`}
      role="img"
      aria-label={`Ended ${size} ${size === 1 ? "point" : "points"} ${off < 0 ? "below" : "above"} plan`}
      title={`Ended ${size} points ${off < 0 ? "below" : "above"} plan`}
    >
      {off > 0 ? "+" : "−"}
      {size} pts
    </span>
  );
}

/** The energy column headers carry their unit once, so the cells can be bare numbers. */
const Unit = () => <span className="th-unit">kWh</span>;

const isNight = (ms: number, timeZone?: string) => {
  const h = Number(clock(ms, { timeZone }).slice(0, 2));
  return h >= 20 || h < 6;
};

/**
 * "What happened (last 24 h)": each window of the plan of record (the plan made just before each half-hour) against
 * what the meters measured: battery planned and measured, home use, solar, grid in and out and the car, with any
 * contradiction called out. Windows expand to their half-hours. On a phone each window is a card.
 */
export function WhatHappened({
  history,
  evidence,
  now,
  reserve,
  timeZone,
}: {
  history: PlanSlotLike[];
  evidence: PlanEvidence | null;
  now: number;
  reserve?: number | null;
  timeZone?: string;
}) {
  // Phones and tablets get cards: at 768px the table would hide the meter columns behind a sideways scroll.
  const cards = useMediaQuery(breakpoints.tablet);
  const wrap = useOverflow<HTMLDivElement>();
  const [open, setOpen] = useState<Set<string>>(() => new Set());
  const rows = historyRows(
    history.filter((s) => Date.parse(s.time) >= now - 24 * HOUR),
    now,
    evidence,
  );
  const windows = historyWindows(rows, now, reserve).reverse();
  const grid = rows.some((r) => r.gridIn != null || r.gridOut != null);
  const car = rows.some((r) => (r.car ?? 0) > 0.05);
  const toggle = (id: string) =>
    setOpen((s) => {
      const next = new Set(s);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  if (!windows.length) return <p className="muted">Nothing to compare yet: this fills in as each half-hour passes.</p>;
  const days: { day: string; windows: HistoryWindow[] }[] = [];
  for (const w of windows) {
    const day = dayWord(w.window.start, now, timeZone, { nights: false });
    const last = days.at(-1);
    if (last && last.day === day) last.windows.push(w);
    else days.push({ day, windows: [w] });
  }
  const when = (a: number, b: number) => `${clock(a, { timeZone })}–${clock(b, { timeZone })}`;
  /** A half-hour's flags in words ("During the 02:30 export, 1.2 kWh came from the grid"). */
  const rowFlags = (r: HistoryRow) =>
    r.flags.filter((f) => !f.startsWith("Ended")).map((f) => flagSentence(r, f, timeZone));
  /** A window's flags, each naming the half-hour it came from. */
  const flagsOf = (w: HistoryWindow) =>
    // A miss against the planned level shows as the number beside the battery; the words are for contradictions.
    [...new Set(w.rows.flatMap(rowFlags))];

  if (cards)
    return (
      <div className="next-cards">
        {days.map((d) => (
          <section key={d.day} aria-label={d.day}>
            <h3 className="day-head">{d.day}</h3>
            <ul>
              {d.windows.map((w) => {
                const off = miss(w.soc);
                const expanded = open.has(w.window.id);
                return (
                  <li key={w.window.id} className={`next-card${flagsOf(w).length ? " is-flagged" : ""}`}>
                    <div className="next-card-line">
                      <span className="next-card-time">{when(w.window.start, w.window.end)}</span>
                      <ActionBadge action={w.window.action} text={w.window.short} tone={w.window.tone} />
                    </div>
                    <div className="next-card-line">
                      <span>
                        Battery <strong>{socPair(w.soc.start, w.soc.end)}</strong>
                      </span>
                      <span className="muted">plan {socPair(w.soc.plannedStart, w.soc.plannedEnd)}</span>
                      <DeltaPill off={off} />
                    </div>
                    <div className="next-card-line muted">
                      {w.home.actual.value != null && <span>Home {energy(w.home.actual)} kWh</span>}
                      {w.solar.actual.value != null && w.solar.actual.value > 0.05 && (
                        <span>Solar {energy(w.solar.actual)} kWh</span>
                      )}
                      {grid && w.gridIn != null && <span>Grid in {plain(w.gridIn)} kWh</span>}
                      {grid && w.gridOut != null && w.gridOut > 0.05 && <span>out {plain(w.gridOut)} kWh</span>}
                      {car && (w.car ?? 0) > 0.05 && <span>Car {plain(w.car)} kWh</span>}
                    </div>
                    {flagsOf(w).map((f) => (
                      <p key={f} className="flag-line">
                        <AlertTriangle size={13} aria-hidden="true" /> {f}
                      </p>
                    ))}
                    {w.rows.length > 1 && (
                      <button
                        type="button"
                        className="expand-button"
                        aria-expanded={expanded}
                        onClick={() => toggle(w.window.id)}
                      >
                        {expanded ? (
                          <ChevronDown size={14} aria-hidden="true" />
                        ) : (
                          <ChevronRight size={14} aria-hidden="true" />
                        )}
                        {expanded ? "Hide" : "Show"} {w.rows.length} half-hours
                      </button>
                    )}
                    {expanded && (
                      <ul className="next-card-slots happened-slots">
                        {w.rows.map((r) => (
                          <li key={r.start} className={rowFlags(r).length ? "is-flagged" : undefined}>
                            <span className="next-card-time">{when(r.start, r.end)}</span>
                            <span>
                              Ended <strong>{r.soc.end != null ? percent(r.soc.end) : "—"}</strong>
                              {r.soc.plannedEnd != null && (
                                <span className="muted"> · plan {percent(r.soc.plannedEnd)}</span>
                              )}
                            </span>
                            <DeltaPill off={miss(r.soc)} />
                          </li>
                        ))}
                      </ul>
                    )}
                  </li>
                );
              })}
            </ul>
          </section>
        ))}
      </div>
    );

  const columns = 6 + (grid ? 2 : 0) + (car ? 1 : 0);
  return (
    <div ref={wrap} className="plan-table-wrap" tabIndex={0} role="region" aria-label="What happened, window by window">
      <table className="plan-table history-table">
        <caption className="sr-only">
          The last 24 hours: what Predbat planned just before each half-hour against what the meters measured.
        </caption>
        <thead>
          <tr>
            <th scope="col">When</th>
            <th scope="col">Planned</th>
            <th scope="col">Battery planned</th>
            <th scope="col">
              Battery measured
              <Hint label="How is the battery compared?">
                Where the battery actually started and ended, against the plan Predbat made just before each half-hour.
                A figure beside it, such as “−6 pts”, is how many percentage points the end level missed the plan by.
              </Hint>
            </th>
            <th scope="col" className="num">
              Home use <Unit />
              <Hint label="What does ≈ mean?">
                kWh measured by your meters. ≈ means the meter’s readings straddled the half-hour, so its energy was
                shared out by time: an estimate, not a direct reading.
              </Hint>
            </th>
            <th scope="col" className="num">
              Solar <Unit />
            </th>
            {grid && (
              <>
                <th scope="col" className="num">
                  Grid in <Unit />
                </th>
                <th scope="col" className="num">
                  Grid out <Unit />
                </th>
              </>
            )}
            {car && (
              <th scope="col" className="num">
                Car <Unit />
              </th>
            )}
          </tr>
        </thead>
        {days.map((d) => (
          <tbody key={d.day}>
            <tr className="day-row">
              <th colSpan={columns} scope="rowgroup">
                {d.day}
              </th>
            </tr>
            {d.windows.map((w) => {
              const expanded = open.has(w.window.id);
              const off = miss(w.soc);
              const night = isNight(w.window.start, timeZone);
              return (
                <Fragment key={w.window.id}>
                  <tr className={`plan-window-row${flagsOf(w).length ? " is-flagged" : ""}`}>
                    <th scope="row">
                      {w.rows.length > 1 ? (
                        <button
                          type="button"
                          className="expand-button"
                          aria-expanded={expanded}
                          aria-label={`${expanded ? "Hide" : "Show"} the half-hours of ${when(w.window.start, w.window.end)}`}
                          onClick={() => toggle(w.window.id)}
                        >
                          {expanded ? (
                            <ChevronDown size={14} aria-hidden="true" />
                          ) : (
                            <ChevronRight size={14} aria-hidden="true" />
                          )}
                        </button>
                      ) : (
                        <span className="expand-spacer" />
                      )}
                      <span className="when">{when(w.window.start, w.window.end)}</span>
                    </th>
                    <td>
                      <ActionBadge action={w.window.action} text={w.window.short} tone={w.window.tone} />
                      {flagsOf(w).map((f) => (
                        <span key={f} className="flag-line">
                          <AlertTriangle size={13} aria-hidden="true" /> {f}
                        </span>
                      ))}
                    </td>
                    <td className="tabular muted">{socPair(w.soc.plannedStart, w.soc.plannedEnd)}</td>
                    <td className="tabular">
                      {socPair(w.soc.start, w.soc.end)}
                      <DeltaPill off={off} />
                    </td>
                    <td className="num tabular">{energy(w.home.actual)}</td>
                    <td className="num tabular">{energy(w.solar.actual, w.solar.forecast, night)}</td>
                    {grid && (
                      <>
                        <td className="num tabular">{plain(w.gridIn)}</td>
                        <td className="num tabular">{plain(w.gridOut)}</td>
                      </>
                    )}
                    {car && <td className="num tabular">{plain(w.car)}</td>}
                  </tr>
                  {expanded &&
                    w.rows.map((r) => {
                      const action = slotAction({ ...r.slot, action: r.slot.action ?? "" });
                      const flags = rowFlags(r);
                      return (
                        <tr key={r.start} className={`slot-row${flags.length ? " is-flagged" : ""}`}>
                          <th scope="row">
                            <span className="expand-spacer" />
                            <span className="when">{when(r.start, r.end)}</span>
                          </th>
                          <td>
                            <ActionBadge action={action} text={shortAction(action)} hint={false} />
                            {flags.map((f) => (
                              <span key={f} className="flag-line">
                                <AlertTriangle size={13} aria-hidden="true" /> {f}
                              </span>
                            ))}
                          </td>
                          <td className="tabular muted">{socPair(r.soc.plannedStart, r.soc.plannedEnd)}</td>
                          <td className="tabular">
                            {socPair(r.soc.start, r.soc.end)}
                            <DeltaPill off={miss(r.soc)} />
                          </td>
                          <td className="num tabular">{energy(r.home.actual)}</td>
                          <td className="num tabular">
                            {energy(r.solar.actual, r.solar.forecast, isNight(r.start, timeZone))}
                          </td>
                          {grid && (
                            <>
                              <td className="num tabular">{plain(r.gridIn)}</td>
                              <td className="num tabular">{plain(r.gridOut)}</td>
                            </>
                          )}
                          {car && <td className="num tabular">{plain(r.car)}</td>}
                        </tr>
                      );
                    })}
                </Fragment>
              );
            })}
          </tbody>
        ))}
      </table>
    </div>
  );
}
