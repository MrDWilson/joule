import { useEffect, useId, useState } from "react";
import type { Api } from "../completion-types";
import { ErrorNotice } from "./ui";
import { Section } from "./ui/Section";
import { kwh } from "../lib/format";
import { stamp } from "../lib/time";
import { ChartFigure, ChartTable, TipRow } from "./charts/kit";
import { SlotChart } from "./charts/SlotChart";
import { ink, series } from "./charts/theme";
import { slotLabel } from "./charts/timeTicks";

interface Comparison {
  available: boolean;
  reason: string;
  methodology: string;
  entityId: string | null;
  source: string | null;
  capturedAt: string | null;
  status?: string;
  nativeLoadMl?: boolean;
  matchedSlots: number;
  predbatMaeKwhPerHalfHour: number | null;
  alternativeMaeKwhPerHalfHour: number | null;
  slots: {
    time: string;
    durationMinutes: number;
    predbatKwh: number;
    alternativeKwh: number;
    actualKwh: number | null;
  }[];
}

/**
 * Encoding: what the meter measured is solid neutral ink; both forecasts are dashed (dashed always means forecast), Predbat's
 * in the home-use blue and the second in the grid magenta. Violet was the first idea, but next to blue it fails the palette
 * check (normal-vision ΔE 9.8), and these two lines overlap all the time.
 */
const colors = { actual: ink, predbat: series.home.color, second: series.grid.color };
const mae = (v: number | null) => (v == null ? "Unavailable" : kwh(v, { precision: "table" }));

export function AlternativeForecast({
  api,
  planId,
  refreshKey,
  timeZone,
}: {
  api: Api;
  planId: string | undefined;
  refreshKey?: string;
  timeZone?: string;
}) {
  const [data, setData] = useState<Comparison | null>(null),
    [error, setError] = useState<unknown>(null),
    [attempt, setAttempt] = useState(0);
  const id = useId().replace(/:/g, "");
  useEffect(() => {
    let active = true;
    setData(null);
    setError(null);
    if (planId)
      void api<Comparison>(`/telemetry/plans/${encodeURIComponent(planId)}/alternative`)
        .then((value) => {
          if (active) setData(value);
        })
        .catch((e: unknown) => {
          if (active) setError(e ?? new Error("Request failed"));
        });
    return () => {
      active = false;
    };
  }, [api, planId, attempt, refreshKey]);
  const slots = data?.slots ?? [];
  const starts = slots.map((s) => Date.parse(s.time));
  const ends = slots.map((s, i) => starts[i] + (s.durationMinutes || 30) * 60000);
  const secondName = data?.nativeLoadMl ? "LoadML" : "Second forecast";
  const showActual = !data?.nativeLoadMl && slots.some((s) => s.actualKwh != null);
  const summary = data?.nativeLoadMl
    ? `${slots.length} slots in both forecasts. No accuracy score yet: first check that LoadML, the plan and your meter cover the same things (for example, car charging).`
    : `${data?.matchedSlots ?? 0} measured slots in both. Average error per half-hour: Predbat ${mae(data?.predbatMaeKwhPerHalfHour ?? null)}, ${secondName.toLowerCase()} ${mae(data?.alternativeMaeKwhPerHalfHour ?? null)}.`;
  return (
    <Section
      title="Second load forecast"
      subtitle="Compares Predbat’s load forecast with another one: Predbat’s LoadML forecast when it publishes one, or a sensor you choose."
    >
      {error ? (
        <ErrorNotice title="Couldn’t load the second forecast" error={error} onRetry={() => setAttempt((x) => x + 1)} />
      ) : !planId ? (
        <p className="muted">Choose a plan first.</p>
      ) : !data ? (
        <p role="status">Loading the second forecast…</p>
      ) : (
        <>
          <p className="callout">{data.reason}</p>
          {data.available && (
            <>
              <p className="muted">
                {data.entityId} · {data.source} · captured{" "}
                {data.capturedAt ? stamp(data.capturedAt, timeZone) : "Unavailable"}
              </p>
              {!data.nativeLoadMl && (
                <div className="stat-chips">
                  <span className="stat-chip">
                    <span>
                      <i className="chart-key dashed" style={{ color: colors.predbat }} aria-hidden="true" />
                      Predbat, average error
                    </span>
                    <strong>{mae(data.predbatMaeKwhPerHalfHour)}</strong>
                  </span>
                  <span className="stat-chip">
                    <span>
                      <i className="chart-key dashed" style={{ color: colors.second }} aria-hidden="true" />
                      {secondName}, average error
                    </span>
                    <strong>{mae(data.alternativeMaeKwhPerHalfHour)}</strong>
                  </span>
                  <span className="stat-chip">
                    <span>Measured slots in both</span>
                    <strong>{data.matchedSlots}</strong>
                  </span>
                </div>
              )}
              <ChartFigure
                id={id}
                title="Load forecasts · kWh per slot"
                summary={summary}
                toolbar={
                  <ul className="chart-chips chart-chips-static" role="list" aria-label="Lines">
                    <li className="chart-chip">
                      <i className="chart-key dashed" style={{ color: colors.predbat }} aria-hidden="true" />
                      Predbat
                    </li>
                    <li className="chart-chip">
                      <i className="chart-key dashed" style={{ color: colors.second }} aria-hidden="true" />
                      {secondName}
                    </li>
                    {showActual && (
                      <li className="chart-chip">
                        <i className="chart-key" style={{ color: colors.actual }} aria-hidden="true" />
                        Measured
                      </li>
                    )}
                  </ul>
                }
                table={
                  <ChartTable label="Load forecasts slot by slot">
                    <thead>
                      <tr>
                        <th>Time</th>
                        <th>Predbat, kWh</th>
                        <th>{secondName}, kWh</th>
                        {showActual && <th>Measured, kWh</th>}
                      </tr>
                    </thead>
                    <tbody>
                      {slots.map((s, i) => (
                        <tr key={s.time}>
                          <td>{slotLabel(starts[i], ends[i], timeZone)}</td>
                          <td>{kwh(s.predbatKwh, { precision: "table", unit: false })}</td>
                          <td>{kwh(s.alternativeKwh, { precision: "table", unit: false })}</td>
                          {showActual && <td>{kwh(s.actualKwh, { precision: "table", unit: false })}</td>}
                        </tr>
                      ))}
                    </tbody>
                  </ChartTable>
                }
              >
                <SlotChart
                  starts={starts}
                  ends={ends}
                  timeZone={timeZone}
                  height={230}
                  label="Read the forecasts slot by slot"
                  unit="kWh"
                  lines={[
                    ...(showActual
                      ? [{ key: "actual", color: colors.actual, values: slots.map((s) => s.actualKwh) }]
                      : []),
                    { key: "predbat", color: colors.predbat, values: slots.map((s) => s.predbatKwh), dashed: true },
                    { key: "second", color: colors.second, values: slots.map((s) => s.alternativeKwh), dashed: true },
                  ]}
                  readout={(i) =>
                    `${slotLabel(starts[i], ends[i], timeZone)}: Predbat ${kwh(slots[i].predbatKwh, { precision: "table" })}, ${secondName} ${kwh(slots[i].alternativeKwh, { precision: "table" })}${showActual ? `, measured ${kwh(slots[i].actualKwh, { precision: "table" })}` : ""}`
                  }
                  tooltip={(i) => (
                    <div className="tl-tip">
                      <p className="chart-tip-label">{slotLabel(starts[i], ends[i], timeZone)}</p>
                      <TipRow
                        color={colors.predbat}
                        kind="dashed"
                        name="Predbat"
                        value={kwh(slots[i].predbatKwh, { precision: "table" })}
                      />
                      <TipRow
                        color={colors.second}
                        kind="dashed"
                        name={secondName}
                        value={kwh(slots[i].alternativeKwh, { precision: "table" })}
                      />
                      {showActual && (
                        <TipRow
                          color={colors.actual}
                          name="Measured"
                          value={
                            slots[i].actualKwh == null ? "no reading" : kwh(slots[i].actualKwh, { precision: "table" })
                          }
                          muted={slots[i].actualKwh == null}
                        />
                      )}
                    </div>
                  )}
                />
              </ChartFigure>
            </>
          )}
          <details>
            <summary>How it’s compared, and setup</summary>
            <p className="body-copy">{data.methodology}</p>
            <p className="muted">
              Predbat publishes its native LoadML sensor when load_ml_enable is enabled and the model has trained on the
              existing load_today history. Collect a new plan after it is ready. No additional sensor mapping is needed
              for that source. This app does not train or enable LoadML, or select it for battery planning.
            </p>
            <p className="muted">
              For another source, optionally map HomeAssistant__Entities__AlternativeForecast to a sensor with
              forecast_unit: kWh, forecast_kind: interval_energy and a forecast array containing time (with timezone),
              duration_minutes and load_kwh. Capture it before the Predbat plan. README describes the supported formats.
            </p>
          </details>
        </>
      )}
    </Section>
  );
}
