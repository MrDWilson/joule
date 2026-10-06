import { describe, expect, it } from "vitest";
import type { EnergySummary } from "../../completion-types";
import { dayRow, dayTotals, describeDays, historySlot, previousAverages } from "./DailyCharts";

const London = "Europe/London";
const metric = (energyKwh: number | null, coverageFraction = 1) => ({
  energyKwh,
  observedSeconds: 0,
  coverageFraction,
  missingIntervals: 0,
});
const day = (
  from: string,
  metrics: Record<string, ReturnType<typeof metric>>,
  cost: number | null = 1,
): EnergySummary => ({
  from,
  to: from,
  metrics,
  importCostGbp: cost,
  exportCreditGbp: 0,
  observedNetCostGbp: cost,
  costCoverageFraction: 1,
  costObservedSeconds: 0,
  sources: [],
  limitations: [],
});

describe("daily bars", () => {
  it("reads home use without the car when the load meter includes it, and dates days like 'Sun 4 Oct'", () => {
    const r = dayRow(day("2026-10-03T23:00:00Z", { load: metric(42, 1), ev: metric(30, 0.9) }), London);
    expect(r.values.home).toEqual({ kwh: 12, coverage: 0.9 });
    expect(r.label).toBe("Sun 4 Oct");
    expect(r.short).toBe("Sun 4");
  });
  it("keeps the whole load as home use when the meter excludes the car", () => {
    const r = dayRow(day("2026-10-03T23:00:00Z", { load: metric(12), ev: metric(30) }), London, false);
    expect(r.values.home.kwh).toBe(12);
  });
  it("trusts the day's own word that the load meter excludes the car, whatever the caller assumes", () => {
    // An install whose load meter never saw the car: taking the car off again would read home use far too low.
    const r = dayRow(
      { ...day("2026-10-03T23:00:00Z", { load: metric(12), ev: metric(30) }), loadIncludesEv: false },
      London,
    );
    expect(r.values.home).toEqual({ kwh: 12, coverage: 1 });
  });
  it("uses the server's home figure when it sends one", () => {
    const r = dayRow(
      {
        ...day("2026-10-03T23:00:00Z", { load: metric(61.1), ev: metric(37.5) }),
        home: metric(23.7, 0.99),
        loadIncludesEv: true,
      },
      London,
    );
    expect(r.values.home).toEqual({ kwh: 23.7, coverage: 0.99 });
  });
  it("prices a day as paid minus earned, with the weaker side's coverage, not the matched-period figure", () => {
    // Live 2 Oct: both meters 99.97 % priced, yet the matched-period coverage was 37 % and the chart said 'n/a'.
    const r = dayRow(
      {
        ...day("2026-10-01T23:00:00Z", { load: metric(16) }),
        importCostGbp: 2.118,
        exportCreditGbp: 0.0003,
        observedNetCostGbp: 2.111,
        costCoverageFraction: 0.37,
        netCostGbp: 2.1177,
        importCostCoverage: 0.9997,
        exportCostCoverage: 0.95,
      },
      London,
    );
    expect(r.cost).toBe(2.1177);
    expect(r.costCoverage).toBe(0.95);
  });
  it("falls back to the matched-period figure only for a server without netCostGbp", () => {
    const r = dayRow({ ...day("2026-10-01T23:00:00Z", { load: metric(16) }, 3), costCoverageFraction: 0.6 }, London);
    expect(r.cost).toBe(3);
    expect(r.costCoverage).toBe(0.6);
  });
  it("compares only when the previous period actually has data", () => {
    // An earlier period full of empty days used to put 'previous period' entries in the legend with nothing drawn.
    expect(previousAverages([day("2026-09-26T23:00:00Z", { load: metric(null, 0) })], London)).toBeNull();
    expect(previousAverages(undefined, London)).toBeNull();
    const avg = previousAverages(
      [
        day("2026-09-26T23:00:00Z", { load: metric(10), ev: metric(0), pv: metric(4) }),
        day("2026-09-27T23:00:00Z", { load: metric(20), ev: metric(5), pv: metric(6) }),
        // Under half measured: left out of the average.
        day("2026-09-28T23:00:00Z", { load: metric(2, 0.3), ev: metric(0), pv: metric(1) }),
      ],
      London,
    )!;
    // Home use alone (10 and 15 kWh), so the line meets the home segment's edge rather than floating among the bars.
    expect(avg.use).toBe(12.5);
    expect(avg.solar).toBeCloseTo(11 / 3);
  });
  it("describes the period and names the partly measured days", () => {
    const rows = [
      dayRow(day("2026-10-01T23:00:00Z", { load: metric(10), pv: metric(5), grid_import: metric(6) }), London),
      dayRow(day("2026-10-02T23:00:00Z", { load: metric(8, 0.37), pv: metric(5) }), London),
    ];
    expect(describeDays(rows, false)).toBe(
      "2 days: home used 10.0 kWh, solar made 10.0 kWh, 6.0 kWh imported. Sat 3 Oct is partly measured (hatched).",
    );
  });
  it("captions the single-day profile with the day's own totals, home use without the car as the headline reads it", () => {
    const d = day("2026-10-03T23:00:00Z", { load: metric(28.3), pv: metric(4.1), ev: metric(4.6) });
    expect(dayTotals({ ...d, home: metric(23.7), loadIncludesEv: true })).toEqual({ home: 23.7, solar: 4.1, ev: 4.6 });
    expect(dayTotals({ ...d, home: metric(23.7), loadIncludesEv: false })).toEqual({ home: 28.3, solar: 4.1, ev: 4.6 });
  });
  it("turns measured history into timeline slots for the single-day profile", () => {
    expect(
      historySlot({
        time: "2026-10-05T00:00:00Z",
        durationMinutes: 30,
        load: null,
        loadEstimate: 0.4,
        pv: 0,
        pvStatus: "idle",
        home: 0.4,
        homeStatus: "estimated",
      }),
    ).toMatchObject({
      loadActual: 0.4,
      pvActual: 0,
      homeActual: 0.4,
      loadActualMethod: "estimated",
      pvActualMethod: "idle",
    });
  });
  it("counts a car session whose timing is estimated, so the profile agrees with the car's day total", () => {
    // Live 4 Oct: the car meter's day total was 37.5 kWh; leaving out the estimated slots made the profile say 24.1.
    expect(
      historySlot({
        time: "2026-10-04T00:00:00Z",
        durationMinutes: 30,
        ev: null,
        evEstimate: 3.7,
        evStatus: "estimated",
      } as Parameters<typeof historySlot>[0]),
    ).toMatchObject({ evActual: 3.7 });
  });
});
