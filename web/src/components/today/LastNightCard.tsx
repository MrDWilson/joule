import { AlertTriangle, ArrowRight, Info, Moon } from "lucide-react";
import { Hint } from "../Hint";
import { gbp, kwh, number, percent, pence } from "../../lib/format";
import { clock } from "../../lib/time";
import type { EnergySummary } from "../../completion-types";
import { windowWhen } from "../plan/windows";
import type { LastNight, NightFlag } from "./model";

/** A level bar from start to end with the planned level marked: "did the battery get where it should?". */
function LevelBar({ start, end, planned }: { start: number | null; end: number | null; planned: number | null }) {
  if (start == null || end == null) return null;
  const lo = Math.min(start, end),
    hi = Math.max(start, end);
  return (
    <div className="level-bar" aria-hidden="true">
      <span
        className={`level-fill${end < start ? " falling" : ""}`}
        style={{ left: `${lo}%`, width: `${Math.max(1, hi - lo)}%` }}
      />
      {planned != null && <span className="level-target" style={{ left: `${Math.min(99.5, planned)}%` }} />}
    </div>
  );
}

/**
 * "Last night": the cheap window's grade. Where the battery started and ended against the evening plan, what the
 * energy cost, what the car took and what was sold back; any half-hour that contradicted the plan; then yesterday's total.
 */
export function LastNightCard({
  night,
  flags,
  yesterday,
  now,
  timeZone,
}: {
  night: LastNight | null;
  /** Half-hours in the window that contradicted the plan, with their cause when known (model.ts nightFlag). */
  flags: Pick<NightFlag, "text" | "at" | "tone">[];
  yesterday: EnergySummary | null;
  now: number;
  timeZone?: string;
}) {
  const yNet = yesterday
    ? (yesterday.netCostGbp ??
      (yesterday.importCostGbp != null || yesterday.exportCreditGbp != null
        ? (yesterday.importCostGbp ?? 0) - (yesterday.exportCreditGbp ?? 0)
        : null))
    : null;
  if (!night && yNet == null) return null;
  const reached = night?.peak && night.socEnd != null && night.peak.value - night.socEnd >= 3 ? night.peak : null;
  return (
    <section className="panel last-night" aria-labelledby="last-night-heading">
      <div className="panel-head">
        <div>
          <h2 id="last-night-heading">
            <Moon size={16} aria-hidden="true" /> {night ? "Last night" : "Yesterday"}
          </h2>
          {night && (
            <p>
              {windowWhen(night.window.start, night.window.end, now, timeZone).replace(/^Last night /, "")} · cheap
              import at {pence(night.window.rate, { unit: "p" })}
            </p>
          )}
        </div>
      </div>
      {night && (
        <>
          <div className="night-battery">
            <span className="night-label">Battery</span>
            <strong>
              {night.socStart != null && night.socEnd != null
                ? `${percent(night.socStart)} → ${percent(night.socEnd)}`
                : "Not measured"}
            </strong>
            {night.planned != null && (
              <span className={`night-plan${night.socEnd != null && night.socEnd < night.planned - 5 ? " short" : ""}`}>
                planned {percent(night.planned)}
                <Hint label="Which plan is this?">
                  {night.plannedFrom
                    ? `What Predbat's plan from ${clock(night.plannedFrom, { timeZone })} the evening before expected by ${clock(night.window.end, { timeZone })}.`
                    : "The highest charge target Predbat set during the window."}
                </Hint>
              </span>
            )}
          </div>
          <LevelBar start={night.socStart} end={night.socEnd} planned={night.planned} />
          {reached && (
            <p className="night-fact muted">
              Peaked at {percent(reached.value)} at {clock(reached.at, { timeZone })}
            </p>
          )}
          <dl className="night-facts">
            {night.importKwh != null && (
              <div>
                <dt>Bought</dt>
                <dd>
                  {kwh(night.importKwh)}
                  {night.averagePrice != null && ` at ${number(night.averagePrice, 1)}p avg`}
                  {night.importCost != null && <span className="muted"> · {gbp(night.importCost)}</span>}
                </dd>
              </div>
            )}
            {night.carKwh != null && night.carKwh >= 0.1 && (
              <div>
                <dt>Car</dt>
                <dd>{kwh(night.carKwh)}</dd>
              </div>
            )}
            {night.exportKwh != null && night.exportKwh >= 0.1 && (
              <div>
                <dt>Sold back</dt>
                <dd>
                  {kwh(night.exportKwh)}
                  {night.exportCredit != null && <span className="muted"> · {gbp(night.exportCredit)}</span>}
                </dd>
              </div>
            )}
          </dl>
          {flags.length > 0 && (
            <ul className="night-flags">
              {flags.slice(0, 3).map((f) => {
                const Icon = f.tone === "info" ? Info : AlertTriangle;
                const at = clock(f.at, { timeZone });
                return (
                  <li key={`${f.text}-${f.at}`} className={`night-flag tone-${f.tone}`}>
                    <Icon size={14} aria-hidden="true" />
                    <span>
                      {f.text}.{" "}
                      <a className="night-flag-link" href="#/plan">
                        See {at} on the plan
                        <ArrowRight size={13} aria-hidden="true" />
                      </a>
                    </span>
                  </li>
                );
              })}
            </ul>
          )}
        </>
      )}
      {yNet != null && yesterday && (
        <p className={`night-yesterday${night ? "" : " alone"}`}>
          {night && <span className="night-label">Yesterday</span>}
          <strong className={yNet < 0 ? "good" : undefined}>
            {yNet < 0 ? `${gbp(-yNet)} earned` : `${gbp(yNet)} net cost`}
          </strong>
          <span className="muted">
            paid {gbp(yesterday.importCostGbp)} · earned {gbp(yesterday.exportCreditGbp)}
          </span>
        </p>
      )}
    </section>
  );
}
