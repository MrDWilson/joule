import { useEffect, useId, useState } from "react";
import type { Api } from "../completion-types";
import { Button, Segmented } from "./ui";
import { PlainText } from "./PlainText";
import { gbp, kwh } from "../lib/format";
import { dayTime, householdTimeZone } from "../lib/time";
import { ChartFigure, ChartTable, TipRow } from "./charts/kit";
import { SlotChart } from "./charts/SlotChart";
import { series } from "./charts/theme";
import { slotLabel } from "./charts/timeTicks";

interface Preview {
  available: boolean;
  reason: string;
  methodology: string;
  planAt: string | null;
  configurationRevision: number;
  costDeltaLowerGbp: number | null;
  costDeltaUpperGbp: number | null;
  /** The server's plain description of the change, shown when no cost range can be given. */
  description?: string | null;
  /** The home's time zone (IANA id) the server used for the description. */
  timeZone?: string | null;
  assumptions: string[];
  slots: {
    time: string;
    durationMinutes?: number;
    baselineLoadKwh: number;
    proposedLoadKwh: number;
    baselinePvKwh: number;
    proposedPvKwh: number;
  }[];
}

type Metric = "Load" | "Pv";
const SAME = 1e-6;

/** The cost change as a headline: "−£0.16 to −£0.26", or one figure when the range collapses. */
export function costRange(lower: number | null, upper: number | null) {
  if (lower == null || upper == null) return null;
  const [a, b] = Math.abs(lower) <= Math.abs(upper) ? [lower, upper] : [upper, lower];
  return Math.abs(a - b) < 0.005
    ? gbp(a, { signed: true })
    : `${gbp(a, { signed: true })} to ${gbp(b, { signed: true })}`;
}

/** The difference the change makes, slot by slot, for one metric: proposed − current forecast. */
export function impactDeltas(slots: Preview["slots"], metric: Metric) {
  return slots.map((s) => s[`proposed${metric}Kwh`] - s[`baseline${metric}Kwh`]);
}

/**
 * What a proposed change might do to the plan: the cost range as the headline, then the difference it makes to the
 * forecast slot by slot as an area around zero (the two forecasts drawn side by side were near-identical lines). The solar
 * view is offered only when the change moves the solar forecast at all.
 */
export function ImpactPreviewPanel({ proposalId, api, timeZone }: { proposalId: string; api: Api; timeZone?: string }) {
  const [preview, setPreview] = useState<Preview | null>(null);
  const [error, setError] = useState("");
  const [attempt, setAttempt] = useState(0);
  const [choice, setMetric] = useState<Metric>("Load");
  const id = useId().replace(/:/g, "");
  useEffect(() => {
    let active = true;
    setPreview(null);
    setError("");
    api<Preview>(`/proposals/${encodeURIComponent(proposalId)}/preview`)
      .then((value: Preview) => {
        if (active) setPreview(value);
      })
      .catch((e: Error) => {
        if (active) setError(e.message);
      });
    return () => {
      active = false;
    };
  }, [api, proposalId, attempt]);
  // The server names the home's time zone, so the times here match its description.
  const zone = timeZone ?? preview?.timeZone ?? householdTimeZone();
  const slots = preview?.slots ?? [];
  const pvChanges = impactDeltas(slots, "Pv").some((d) => Math.abs(d) > SAME);
  const metric: Metric = choice === "Pv" && !pvChanges ? "Load" : choice;
  const deltas = impactDeltas(slots, metric);
  const starts = slots.map((s) => Date.parse(s.time));
  const ends = slots.map((s, i) => starts[i] + (s.durationMinutes ?? 30) * 60000);
  const changed = deltas.some((d) => Math.abs(d) > SAME);
  const name = metric === "Pv" ? "solar" : "home use";
  const total = deltas.reduce((t, d) => t + d, 0);
  const biggest = deltas.reduce((best, d, i) => (Math.abs(d) > Math.abs(deltas[best] ?? 0) ? i : best), 0);
  const hours = slots.length ? Math.round((ends.at(-1)! - starts[0]) / 3600000) : 0;
  const color = metric === "Pv" ? series.solar.color : series.home.color;
  const range = preview ? costRange(preview.costDeltaLowerGbp, preview.costDeltaUpperGbp) : null;
  const summary = changed
    ? `With this change Predbat's ${name} forecast moves by ${kwh(total, { precision: "table", signed: true })} over the next ${hours} h; the largest change is ${kwh(deltas[biggest], { precision: "table", signed: true })} at ${slotLabel(starts[biggest], ends[biggest], zone)}.`
    : `This change doesn't move Predbat's ${name} forecast over the next ${hours} h.`;
  return (
    <section className="impact-preview" aria-label="Expected impact preview">
      <h4>What the plan might look like</h4>
      {error ? (
        <div role="alert" className="callout">
          {error}{" "}
          <Button variant="outline" onClick={() => setAttempt(attempt + 1)}>
            Retry preview
          </Button>
        </div>
      ) : !preview ? (
        <p className="muted" role="status">
          Calculating the remaining plan…
        </p>
      ) : !preview.available ? (
        <p className="callout">{preview.reason}</p>
      ) : (
        <>
          {range ? (
            <p className="impact-headline">
              <strong>{range}</strong>
              <span>possible cost change over the next {hours} h (a minus figure is a saving)</span>
            </p>
          ) : (
            preview.description && (
              <p className="callout">
                <PlainText text={preview.description} />
              </p>
            )
          )}
          <p className="muted">
            Based on the plan from {dayTime(preview.planAt!, { timeZone: zone })} (settings version{" "}
            {preview.configurationRevision}). A rough illustration, not a promise: the battery and your real bill may
            behave differently.
          </p>
          {pvChanges && (
            <Segmented<Metric>
              label="Forecast shown"
              size="sm"
              value={metric}
              onChange={setMetric}
              options={[
                { value: "Load", label: "Home use" },
                { value: "Pv", label: "Solar" },
              ]}
            />
          )}
          <ChartFigure
            id={id}
            title={`Change to the ${name} forecast · kWh per slot`}
            summary={summary}
            className="impact-chart-figure"
            data={{ metric, changed: changed ? "yes" : "no" }}
            table={
              <ChartTable label="Forecast with and without the change">
                <thead>
                  <tr>
                    <th>Time</th>
                    <th>Current forecast, kWh</th>
                    <th>With this change, kWh</th>
                    <th>Difference, kWh</th>
                  </tr>
                </thead>
                <tbody>
                  {slots.map((s, i) => (
                    <tr key={s.time}>
                      <td>{slotLabel(starts[i], ends[i], zone)}</td>
                      <td>{kwh(s[`baseline${metric}Kwh`], { precision: "table", unit: false })}</td>
                      <td>{kwh(s[`proposed${metric}Kwh`], { precision: "table", unit: false })}</td>
                      <td>{kwh(deltas[i], { precision: "table", unit: false, signed: true })}</td>
                    </tr>
                  ))}
                </tbody>
              </ChartTable>
            }
          >
            {changed ? (
              <SlotChart
                starts={starts}
                ends={ends}
                timeZone={zone}
                height={190}
                label="Read the change slot by slot"
                unit="kWh"
                lines={[{ key: "delta", color, values: deltas, area: true }]}
                readout={(i) =>
                  `${slotLabel(starts[i], ends[i], zone)}: ${kwh(deltas[i], { precision: "table", signed: true })}`
                }
                tooltip={(i) => (
                  <div className="tl-tip">
                    <p className="chart-tip-label">{slotLabel(starts[i], ends[i], zone)}</p>
                    <TipRow
                      color={color}
                      kind="dashed"
                      name="Current forecast"
                      value={kwh(slots[i][`baseline${metric}Kwh`], { precision: "table" })}
                    />
                    <TipRow
                      color={color}
                      kind="dashed"
                      name="With this change"
                      value={kwh(slots[i][`proposed${metric}Kwh`], { precision: "table" })}
                    />
                    <TipRow
                      color={color}
                      kind="block"
                      name="Difference"
                      value={kwh(deltas[i], { precision: "table", signed: true })}
                    />
                  </div>
                )}
              />
            ) : (
              <p className="chart-empty">No change to the {name} forecast.</p>
            )}
          </ChartFigure>
          <details>
            <summary>How this was worked out</summary>
            <p className="body-copy">{preview.methodology}</p>
            <ul className="evidence">
              {preview.assumptions.map((a) => (
                <li key={a}>{a}</li>
              ))}
            </ul>
          </details>
        </>
      )}
    </section>
  );
}
