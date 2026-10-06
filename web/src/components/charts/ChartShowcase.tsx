/** Every chart in the kit, on deterministic live-shaped data, for #/kit (development builds) and design review. */
import { EnergyTimeline } from "./EnergyTimeline";
import { DailyChart } from "./DailyCharts";
import { SlotChart } from "./SlotChart";
import { ObservedTrend, SocTrend } from "../ObservedTrend";
import { KIT_NOW, KIT_ZONE, kitDays, kitDispatches, kitHistory, kitPlan, kitTrends } from "./fixtures";
import { mergeTimeline } from "./timeline";
import { series, ink, earlierInk } from "./theme";
import { kwh } from "../../lib/format";

/** The dataviz palette check, run against the dark panel surface #0d151d (scripts in the PR). */
const palette = [
  ["Home", series.home.color, "#3987e5"],
  ["Solar", series.solar.color, "#c98500"],
  ["Battery", series.battery.color, "#199e70"],
  ["Car", series.ev.color, "#9085e9"],
  ["Grid", series.grid.color, "#d55181"],
  ["Measured (comparison charts)", ink, "#dce5ed"],
  ["Earlier period", earlierInk, "#b4c3cf"],
] as const;

export function ChartShowcase() {
  const slots = mergeTimeline(kitHistory(), kitPlan());
  const plan = kitPlan();
  const starts = plan.slice(1, 25).map((s) => Date.parse(s.time));
  const ends = starts.map((s) => s + 1800000);
  const deltas = starts.map((_, i) => -0.08 * Math.max(0, Math.sin((i / 24) * Math.PI)));
  return (
    <div className="kit-charts">
      <section aria-labelledby="kit-charts-palette">
        <h3 id="kit-charts-palette">Series colours</h3>
        <div className="kit-grid">
          {palette.map(([name, color, hex]) => (
            <span key={name} className="kit-swatch">
              <i style={{ background: color }} />
              {name} <code>{hex}</code>
            </span>
          ))}
        </div>
        <p className="muted">
          Dashed always means forecast. Earlier periods are a thin solid line at 35% opacity; a bridged short gap is the
          series hue at 30%; “Now” is a solid hairline with a pill.
        </p>
      </section>
      <section aria-labelledby="kit-charts-today">
        <h3 id="kit-charts-today">Energy timeline · Today</h3>
        <EnergyTimeline
          slots={slots}
          timeZone={KIT_ZONE}
          now={KIT_NOW}
          preset="today"
          reserve={4}
          dispatches={kitDispatches}
          labelledBy="kit-charts-today"
          storageId="kit-today"
        />
      </section>
      <section aria-labelledby="kit-charts-plan">
        <h3 id="kit-charts-plan">Energy timeline · Plan</h3>
        <EnergyTimeline
          slots={slots}
          timeZone={KIT_ZONE}
          now={KIT_NOW}
          preset="plan"
          reserve={4}
          dispatches={kitDispatches}
          labelledBy="kit-charts-plan"
          storageId="kit-plan"
        />
      </section>
      <section aria-labelledby="kit-charts-battery">
        <h3 id="kit-charts-battery">Battery outlook</h3>
        <EnergyTimeline
          slots={slots}
          timeZone={KIT_ZONE}
          now={KIT_NOW}
          preset="battery"
          reserve={4}
          labelledBy="kit-charts-battery"
        />
      </section>
      <section aria-labelledby="kit-charts-tiles">
        <h3 id="kit-charts-tiles">Tile sparklines</h3>
        <div className="kit-grid kit-tiles">
          <div className="metric amber">
            <ObservedTrend data={kitTrends("pv")} metric="pv" error="" timeZone={KIT_ZONE} plan={plan} now={KIT_NOW} />
          </div>
          <div className="metric blue">
            <ObservedTrend
              data={kitTrends("load")}
              metric="load"
              error=""
              timeZone={KIT_ZONE}
              plan={plan}
              now={KIT_NOW}
            />
          </div>
          <div className="metric green">
            <SocTrend
              slots={slots}
              current={{ value: 74, time: new Date(KIT_NOW).toISOString() }}
              reserve={4}
              timeZone={KIT_ZONE}
              now={KIT_NOW}
            />
          </div>
        </div>
      </section>
      <section aria-labelledby="kit-charts-daily">
        <h3 id="kit-charts-daily">Daily energy and net cost</h3>
        <DailyChart days={kitDays()} previous={kitDays(7)} previousLabel="the previous 7 days" timeZone={KIT_ZONE} />
      </section>
      <section aria-labelledby="kit-charts-delta">
        <h3 id="kit-charts-delta">Impact preview delta</h3>
        <SlotChart
          starts={starts}
          ends={ends}
          timeZone={KIT_ZONE}
          label="Kit delta"
          unit="kWh"
          lines={[{ key: "delta", color: series.home.color, values: deltas, area: true }]}
          readout={(i) => `${deltas[i]}`}
          tooltip={(i) => <div className="tl-tip">{kwh(deltas[i], { precision: "table", signed: true })}</div>}
        />
      </section>
    </div>
  );
}
