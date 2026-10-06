import { useEffect, useState } from "react";
import { Disclosure, ErrorNotice } from "../ui";
import { Section } from "../ui/Section";
import { coverage, energy } from "../charts/chartUtils";
import { stamp } from "../../lib/time";
import { number } from "../../lib/format";
import type { Slot } from "../../types";
import type { Api, PlanEvidence } from "../../completion-types";

type EvidenceSlot = PlanEvidence["slots"][number];
const finite = (v: number | null | undefined): v is number => typeof v === "number" && Number.isFinite(v);
const plural = (n: number, one: string, many = `${one}s`) => `${n} ${n === 1 ? one : many}`;

/** A half-hour's measured energy for one meter: the whole-slot estimate, else the reading when it covers the slot. */
function measuredKwh(slot: EvidenceSlot, key: "load" | "pv") {
  const estimate = key === "load" ? slot.estimatedLoadKwh : slot.estimatedPvKwh;
  if (finite(estimate)) return estimate;
  const m = slot.actual.metrics[key];
  return m && finite(m.energyKwh) && m.coverageFraction >= 0.9 ? m.energyKwh : null;
}

/**
 * The plan's forecast against the meters over the finished half-hours that have a reading for both: "Home use came in
 * 8% above forecast; solar 3% below." Solar is left out when too little was forecast to compare (under 0.2 kWh). Empty
 * when nothing can be compared yet.
 */
export function forecastResult(
  elapsed: EvidenceSlot[],
  forecasts: Pick<Slot, "time" | "loadForecast" | "pvForecast">[],
) {
  const byTime = new Map(forecasts.map((s) => [Date.parse(s.time), s]));
  const compare = (key: "load" | "pv") => {
    let actual = 0,
      forecast = 0,
      n = 0;
    for (const slot of elapsed) {
      const f = byTime.get(Date.parse(slot.time));
      const planned = key === "load" ? f?.loadForecast : f?.pvForecast;
      const measured = measuredKwh(slot, key);
      if (!finite(planned) || measured == null) continue;
      actual += measured;
      forecast += planned;
      n++;
    }
    if (!n || forecast < (key === "pv" ? 0.2 : 0.05)) return null;
    return Math.round(((actual - forecast) / forecast) * 100);
  };
  // "8% above forecast"; from double on, "2.2 times the forecast" reads better than "115% above".
  const words = (pct: number) =>
    Math.abs(pct) < 2
      ? "on forecast"
      : pct >= 100
        ? `at ${number((pct + 100) / 100, 1)} times the forecast`
        : `${Math.abs(pct)}% ${pct > 0 ? "above" : "below"} forecast`;
  const home = compare("load"),
    solar = compare("pv");
  if (home == null && solar == null) return "";
  if (home == null) return `Solar came in ${words(solar!)}.`;
  const lead = `Home use came in ${words(home)}`;
  if (solar == null) return `${lead}.`;
  // The second clause leans on the first: "solar 3% below" (but "solar at 2.2 times the forecast" in full).
  const second = words(solar);
  return `${lead}; solar ${/ (above|below) forecast$/.test(second) ? second.replace(/ forecast$/, "") : second}.`;
}

/** Forecast against measured energy for each finished half-hour of one plan. */
export function PlanMeasuredEvidence({
  api,
  id,
  slots,
  refreshKey,
  timeZone,
}: {
  api: Api;
  id: string;
  slots: Slot[];
  refreshKey?: string;
  timeZone?: string;
}) {
  const [result, setResult] = useState<PlanEvidence | null>(null),
    [error, setError] = useState<unknown>(null),
    [retry, setRetry] = useState(0);
  useEffect(() => {
    let active = true;
    setResult(null);
    setError(null);
    api<PlanEvidence>(`/telemetry/plans/${encodeURIComponent(id)}/evidence`)
      .then((r) => {
        if (active) setResult(r);
      })
      .catch((e: unknown) => {
        if (active) setError(e ?? new Error("Request failed"));
      });
    return () => {
      active = false;
    };
  }, [api, id, retry, refreshKey]);
  const now = Date.now();
  const elapsed = result?.slots.filter((slot) => Date.parse(slot.time) + slot.durationMinutes * 60000 <= now) || [];
  const underway =
    result?.slots.filter(
      (slot) => Date.parse(slot.time) <= now && Date.parse(slot.time) + slot.durationMinutes * 60000 > now,
    ).length || 0;
  const future = (result?.slots.length || 0) - elapsed.length - underway;
  const measured = elapsed.filter(
    (slot) => slot.actual.metrics.load?.energyKwh != null || slot.actual.metrics.pv?.energyKwh != null,
  ).length;
  const verdict = forecastResult(elapsed, slots);
  const forecastOf = (time: string) => slots.find((s) => s.time === time);
  return (
    <Section
      title="How this plan’s forecast did"
      subtitle="Its forecast against your meters, for each half-hour that has finished."
    >
      {error ? (
        <ErrorNotice
          title="Couldn’t load this plan’s meter readings"
          error={error}
          onRetry={() => setRetry(retry + 1)}
        />
      ) : !result ? (
        <p role="status">Loading the meter readings…</p>
      ) : (
        <div className="forecast-did">
          {verdict && <p className="forecast-did-result">{verdict}</p>}
          <p className="availability-note">
            <strong>
              {elapsed.length
                ? `${plural(elapsed.length, "finished half-hour")} to compare`
                : "This plan is all still ahead"}
            </strong>
            {plural(future, "half-hour")} still to come{underway ? ` and ${underway} under way` : ""}.
          </p>
          {elapsed.length > 0 && (
            <Disclosure
              summary={`${plural(elapsed.length, "finished half-hour")} · ${measured} with readings`}
              className="forecast-did-table"
            >
              <div
                className="table-wrap table-scroll"
                tabIndex={0}
                role="region"
                aria-label="Finished half-hours (scrolls sideways)"
              >
                <table>
                  <thead>
                    <tr>
                      <th>Half-hour</th>
                      <th>Home use (measured)</th>
                      <th>Solar (measured)</th>
                      <th>Forecast: home / solar</th>
                      <th>Estimate for the whole half-hour: home / solar</th>
                    </tr>
                  </thead>
                  <tbody>
                    {elapsed.map((slot) => (
                      <tr key={slot.time}>
                        <td>
                          {stamp(slot.time, timeZone)}
                          <small className="cell-note">{slot.durationMinutes} minutes</small>
                        </td>
                        <td>
                          {energy(slot.actual.metrics.load?.energyKwh)}
                          <small className="cell-note">{coverage(slot.actual.metrics.load?.coverageFraction)}</small>
                        </td>
                        <td>
                          {energy(slot.actual.metrics.pv?.energyKwh)}
                          <small className="cell-note">{coverage(slot.actual.metrics.pv?.coverageFraction)}</small>
                        </td>
                        <td>
                          {energy(forecastOf(slot.time)?.loadForecast)} / {energy(forecastOf(slot.time)?.pvForecast)}
                        </td>
                        <td>
                          {energy(slot.estimatedLoadKwh)} / {energy(slot.estimatedPvKwh)}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              <p className="muted forecast-did-note">
                Meter readings rarely line up exactly with a half-hour’s start and end, so the energy between the
                readings either side is shared out by time. It’s an estimate, not a direct measurement.
                {Array.from(new Set(elapsed.map((slot) => slot.estimateMethod)))
                  .filter(Boolean)
                  .map((method) => ` ${method}`)}
              </p>
            </Disclosure>
          )}
          <Disclosure summary="Where do the measured figures come from?" className="forecast-did-rules">
            <p>
              From your own energy meters in Home Assistant. Predbat’s own load history can include adjustments and
              forecasts, so it isn’t used as the measured figure. Demo readings are made up.
            </p>
            <p>
              Check that your home-use meter measures the same things Predbat forecasts. A whole-house meter may include
              car charging or a hot-water heater that Predbat leaves out of its forecast; that difference alone doesn’t
              mean the forecast is wrong.
            </p>
          </Disclosure>
        </div>
      )}
    </Section>
  );
}
