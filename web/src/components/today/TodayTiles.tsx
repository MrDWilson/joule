import { Battery, House, Sun, UtilityPole, Wallet } from "lucide-react";
import { Chip, Stat } from "../ui";
import { Hint } from "../Hint";
import { GridTrend, ObservedTrend, SocTrend } from "../ObservedTrend";
import type { EnergySummary, ObservedMeterTrends } from "../../completion-types";
import { gbp, kwh, pence } from "../../lib/format";
import { clock } from "../../lib/time";
import type { TimelineSlot } from "../charts/timeline";
import {
  coverageNote,
  directionText,
  gridView,
  homeUse,
  yesterdayByNow,
  type BatteryNow,
  type CostView,
  type MetricDetail,
} from "./model";

/** Paid against earned as one thin bar: the net cost tile's picture. */
function PaidEarnedBar({ paid, earned }: { paid: number | null; earned: number | null }) {
  const p = Math.max(0, paid ?? 0),
    e = Math.max(0, earned ?? 0);
  const total = p + e;
  if (total <= 0) return <div className="paid-earned empty" aria-hidden="true" />;
  return (
    <div className="paid-earned" aria-hidden="true">
      <span className="paid" style={{ width: `${(p / total) * 100}%` }} />
      <span className="earned" style={{ width: `${(e / total) * 100}%` }} />
    </div>
  );
}

const kwhValue = (n: number | null | undefined) => kwh(n, { unit: false });
/** "≈" when more than 5% of a meter's figure rests on timing-estimated energy. */
const approxOf = (m: MetricDetail | null | undefined) =>
  !!m && m.energyKwh != null && (m.estimatedKwh ?? 0) > Math.max(0.05, 0.05 * m.energyKwh);

/**
 * Today's five tiles, one anatomy each: label and icon, the figure, a small picture, one footnote and, only when it is
 * comparable, "Yesterday by now". Coverage is mentioned only below 98%, as a chip naming the meter and the hours.
 */
export function TodayTiles({
  daily,
  yesterday,
  cost,
  yesterdayCost,
  trends,
  trendError,
  battery,
  batteryOutlook,
  chart,
  reserve,
  socReading,
  plan,
  timeZone,
  solarAsleep,
}: {
  daily: EnergySummary | null;
  yesterday: EnergySummary | null;
  cost: CostView | null;
  yesterdayCost: number | null;
  trends: ObservedMeterTrends | null;
  trendError: string;
  battery: BatteryNow;
  batteryOutlook: string;
  chart: TimelineSlot[];
  reserve: number | null;
  socReading: { value: number | null; time: string } | null;
  plan: TimelineSlot[];
  timeZone: string;
  /** Night-time with no solar so far: say so calmly instead of showing a gap. */
  solarAsleep: boolean;
}) {
  const solar = daily?.metrics.pv as MetricDetail | undefined;
  const grid = gridView(daily, timeZone);
  const gridApprox = approxOf(daily?.metrics.grid_import as MetricDetail | undefined);
  const home = homeUse(daily);
  const prevHome = homeUse(yesterday);
  const solarNote = coverageNote("pv", solar, timeZone);
  const homeNote = home?.metric ? coverageNote(home.excludesCar ? "home" : "load", home.metric, timeZone) : null;
  const approx = approxOf;
  const costDelta =
    cost && yesterdayCost != null
      ? yesterdayByNow(
          { value: Math.abs(yesterdayCost), coverage: 1 },
          (n) => `${yesterdayCost < 0 ? "earned" : "paid"} ${gbp(n)}`,
        )
      : null;
  return (
    <div className="today-tiles">
      <Stat
        ariaLabel={cost?.label ?? "Net cost today"}
        className={cost?.earning ? "stat-earning" : undefined}
        label={
          <>
            {cost?.earning ? "Net earnings" : "Net cost"}
            <Hint label="How is net cost worked out?">
              What you paid for electricity from the grid today, minus what you were paid for exporting
              {cost?.standing
                ? cost.standing.included
                  ? `, plus the standing charge (${cost.standing.rate}) for the part of today so far.`
                  : `. The standing charge (${cost.standing.rate}) is left out, as chosen in Setup › Sensors.`
                : ". Joule doesn’t know your standing charge yet: add it in Setup › Sensors."}
            </Hint>
          </>
        }
        value={cost?.value ?? "—"}
        icon={<Wallet />}
        accent={cost?.earning ? "export" : "neutral"}
        sparkline={cost ? <PaidEarnedBar paid={cost.paid} earned={cost.earned} /> : undefined}
        delta={
          cost ? (
            <>
              {cost.breakdown}
              {cost.standing?.included && (
                <span className="stat-delta-line">
                  Standing charge {cost.standing.text} · {cost.standing.rate}
                </span>
              )}
            </>
          ) : undefined
        }
        footnote={costDelta}
        status={cost?.note ? <Chip tone="warn">{cost.note}</Chip> : undefined}
      />
      <Stat
        ariaLabel="Grid"
        className="stat-grid"
        label={
          <>
            Grid import
            <Hint label="What does the Grid tile show?">
              Electricity bought from the grid since midnight, what it cost and the average price per kWh, from your
              import meter. Under it, what you sold back. The bars are each half-hour: bought above the line, sold
              below.
            </Hint>
          </>
        }
        value={
          <>
            {gridApprox ? "≈ " : ""}
            {kwhValue(grid?.importKwh)}
          </>
        }
        unit="kWh"
        icon={<UtilityPole />}
        accent="grid"
        sparkline={<GridTrend slots={chart} timeZone={timeZone} />}
        delta={
          grid ? (
            <>
              {grid.importGbp != null
                ? `Cost ${gbp(grid.importGbp)}${grid.averagePence != null ? ` · ${pence(Math.round(grid.averagePence * 10) / 10)}` : ""}`
                : "Cost not priced yet"}
              <span className="stat-delta-line">{grid.exported}</span>
            </>
          ) : undefined
        }
        footnote={yesterdayByNow(
          {
            value: yesterday?.metrics.grid_import?.energyKwh,
            coverage: yesterday?.metrics.grid_import?.coverageFraction,
          },
          (n) => `${kwh(n)}${yesterday?.importCostGbp != null ? ` for ${gbp(yesterday.importCostGbp)}` : ""}`,
        )}
        status={grid?.note ? <Chip tone="warn">{grid.note}</Chip> : undefined}
      />
      <Stat
        ariaLabel="Solar"
        label="Solar"
        value={
          <>
            {approx(solar) ? "≈ " : ""}
            {kwhValue(solar?.energyKwh)}
          </>
        }
        unit="kWh"
        icon={<Sun />}
        accent="solar"
        sparkline={<ObservedTrend data={trends} metric="pv" error={trendError} timeZone={timeZone} plan={plan} />}
        delta={solarAsleep ? "Asleep overnight, so nothing yet" : undefined}
        footnote={yesterdayByNow(
          { value: yesterday?.metrics.pv?.energyKwh, coverage: yesterday?.metrics.pv?.coverageFraction },
          (n) => kwh(n),
        )}
        status={solarNote ? <Chip tone="warn">{solarNote}</Chip> : undefined}
      />
      <Stat
        ariaLabel="Home use"
        label="Home use"
        value={
          <>
            {approx(home?.metric) ? "≈ " : ""}
            {kwhValue(home?.metric?.energyKwh)}
          </>
        }
        unit="kWh"
        icon={<House />}
        accent="load"
        sparkline={<ObservedTrend data={trends} metric="load" error={trendError} timeZone={timeZone} plan={plan} />}
        delta={home?.car != null ? `Plus the car: ${kwh(home.car)}` : undefined}
        footnote={yesterdayByNow(
          { value: prevHome?.metric?.energyKwh, coverage: prevHome?.metric?.coverageFraction },
          (n) => kwh(n),
        )}
        status={homeNote ? <Chip tone="warn">{homeNote}</Chip> : undefined}
      />
      <Stat
        ariaLabel="Battery"
        label="Battery"
        className={battery.stale ? "stat-stale" : undefined}
        value={battery.value != null ? Math.round(battery.value).toString() : "—"}
        unit={battery.value != null ? "%" : undefined}
        icon={<Battery />}
        accent="battery"
        sparkline={<SocTrend slots={chart} current={socReading} reserve={reserve} timeZone={timeZone} />}
        delta={
          battery.stale
            ? `As of ${battery.time ? clock(battery.time, { timeZone }) : "earlier"} · sensor not reporting`
            : [directionText(battery), batteryOutlook].filter(Boolean).join(" · ") || undefined
        }
      />
    </div>
  );
}
