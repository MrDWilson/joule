/**
 * Deterministic, live-shaped chart data for the #/kit showcase and design review: a day of measured history and a day
 * and a half of plan around a fixed "now", with the awkward cases real installs produce: solar asleep overnight (no
 * forecast), a short and a long meter outage, the plan starting part-way through a half-hour, car charging, an overnight
 * charge with a free session and an IOG dispatch, an evening export and prices Predbat estimated.
 */
import type { EnergySummary, ObservedMeterTrends } from "../../completion-types";
import type { TimelineSlot } from "./timeline";

export const KIT_ZONE = "Europe/London";
/** Mon 5 Oct 2026, 14:25 BST. */
export const KIT_NOW = Date.parse("2026-10-05T13:25:00Z");
const HALF = 1800000;

const sun = (t: number) => {
  const h = (((t / 3600000 + 1) % 24) + 24) % 24; // BST hour
  return h < 7 || h > 18.5 ? 0 : Math.max(0, Math.sin(((h - 7) / 11.5) * Math.PI)) * 2.4;
};
const home = (t: number) => {
  const h = (((t / 3600000 + 1) % 24) + 24) % 24;
  return 0.28 + (h > 17 && h < 21 ? 0.55 : 0) + (h > 7 && h < 9 ? 0.25 : 0) + 0.05 * Math.sin(t / 2.3e6);
};

export function kitHistory(): TimelineSlot[] {
  const start = KIT_NOW - 24 * HALF * 2 - (KIT_NOW % HALF);
  const out: TimelineSlot[] = [];
  let soc = 62;
  for (let t = start; t < KIT_NOW - (KIT_NOW % HALF) + HALF; t += HALF) {
    const i = (t - start) / HALF;
    const h = (((t / 3600000 + 1) % 24) + 24) % 24;
    const ended = t + HALF <= KIT_NOW;
    const outage = h >= 11 && h < 13; // long gap: 4 slots
    const blip = h === 9.5; // short gap: 1 slot
    const car = h >= 1 && h < 3.5 ? 3.4 : 0;
    const charging = h >= 0 && h < 5;
    soc = Math.min(100, Math.max(4, soc + (charging ? 9 : -2.2) + (sun(t) > 1 ? 3 : 0)));
    out.push({
      time: new Date(t).toISOString(),
      durationMinutes: 30,
      loadForecast: home(t),
      pvForecast: sun(t) * 1.05,
      loadActual: ended && !outage && !blip ? home(t) * (0.92 + ((i * 7) % 5) / 25) + car : null,
      homeActual: ended && !outage && !blip ? home(t) * (0.92 + ((i * 7) % 5) / 25) : null,
      evActual: ended && !outage && !blip ? car : null,
      pvActual:
        ended && !outage && !blip
          ? sun(t) > 0
            ? sun(t) * (0.85 + ((i * 3) % 4) / 20)
            : sun(t) === 0 && h < 7
              ? null
              : 0
          : null,
      loadActualMethod: h === 14 ? "estimated" : "measured",
      pvActualMethod: "measured",
      socForecast: soc,
      socActual: ended && !outage ? soc : null,
      importRate: charging ? 6.67 : 25.4,
      exportRate: 15,
      action: charging ? "charge" : "demand",
      cost: 0,
    });
  }
  return out;
}

export function kitPlan(): TimelineSlot[] {
  // Predbat replanned at 14:25: the first slot is the 5 minutes to 14:30.
  const out: TimelineSlot[] = [];
  const first = KIT_NOW;
  let soc = 74;
  let t = first;
  const end = KIT_NOW + 34 * 3600000;
  while (t < end) {
    const minutes = t === first ? 5 : 30;
    const h = (((t / 3600000 + 1) % 24) + 24) % 24;
    const charge = h >= 23.5 || h < 5.5;
    const exportSlot = h >= 18 && h < 19;
    const free = h >= 1 && h < 2;
    const iog = h >= 14.5 && h < 15.5 && t > KIT_NOW + 6 * 3600000;
    const car = h >= 2 && h < 4 ? 3.2 : 0;
    soc = Math.min(100, Math.max(4, soc + (charge ? 12 : exportSlot ? -18 : -2.4) * (minutes / 30)));
    out.push({
      time: new Date(t).toISOString(),
      durationMinutes: minutes,
      loadForecast: home(t) * (minutes / 30),
      pvForecast: sun(t) * (minutes / 30),
      loadActual: null,
      pvActual: null,
      socForecast: soc,
      socActual: null,
      importRate: free ? 0 : charge || iog ? 6.67 : h >= 16 && h < 19 ? 33.4 : 25.4,
      exportRate: 15,
      importRateType: t > KIT_NOW + 20 * 3600000 ? "copy" : null,
      rateEstimated: t > KIT_NOW + 20 * 3600000,
      carKwh: car,
      action: charge ? "charge" : exportSlot ? "export" : h >= 19 && h < 19.5 ? "hold-export" : "demand",
      actionKey: charge ? "charge" : exportSlot ? "export" : h >= 19 && h < 19.5 ? "hold-export" : "demand",
      targetPercent: charge ? 100 : exportSlot ? 38 : null,
      reasonText: charge
        ? "Charging to 100% while import is 6.67p, so the morning peak runs from the battery."
        : exportSlot
          ? "Exporting while the export price beats tomorrow's charge cost."
          : null,
      cost: (minutes / 30) * (charge ? 0.25 : 0.06),
    });
    t += minutes * 60000;
  }
  return out;
}

export const kitDispatches = [
  { start: new Date(KIT_NOW + 25 * 3600000).toISOString(), end: new Date(KIT_NOW + 26 * 3600000).toISOString() },
];

/** Raw 5-minute meter intervals for today, including Home Assistant's skipped-update zig-zag. */
export function kitTrends(metric: "load" | "pv"): ObservedMeterTrends {
  const start = Date.parse("2026-10-04T23:00:00Z");
  const intervals: ObservedMeterTrends["intervals"] = [];
  for (let t = start; t + 300000 <= KIT_NOW; t += 300000) {
    const h = (t - start) / 3600000;
    const skipped = Math.floor((t - start) / 300000) % 4;
    const base = metric === "pv" ? sun(t) * 2 : home(t) * 2 + (h >= 1 && h < 3.5 ? 6.8 : 0);
    const kw = metric === "load" && h < 4 ? (skipped === 1 ? 0 : skipped === 2 ? base * 2 : base) : base;
    const outage = h >= 11 && h < 13;
    intervals.push({
      metric,
      start: new Date(t).toISOString(),
      end: new Date(t + 300000).toISOString(),
      averageKw: outage ? null : kw,
      source: "Kit",
      entityId: null,
      status: outage ? "unavailable" : metric === "pv" && sun(t) === 0 ? "idle" : "observed",
    });
  }
  return {
    from: new Date(start).toISOString(),
    to: new Date(KIT_NOW).toISOString(),
    intervals,
    truncated: false,
    limit: 5000,
    method: "Kit",
  };
}

const m = (energyKwh: number | null, coverageFraction = 1) => ({
  energyKwh,
  observedSeconds: 0,
  coverageFraction,
  missingIntervals: 0,
});
/** Seven days of daily summaries: one partly measured day, one barely measured, and today in progress. */
export function kitDays(offset = 0): EnergySummary[] {
  return Array.from({ length: 7 }, (_, i) => {
    const from = new Date(Date.parse("2026-09-28T23:00:00Z") + (i - offset) * 86400000).toISOString();
    const cov = offset ? 1 : i === 3 ? 0.37 : i === 5 ? 0.82 : i === 6 ? 0.6 : 1;
    const solar = 8 + 6 * Math.sin(i + offset),
      use = 14 + 3 * Math.cos(i * 1.7 + offset),
      car = i % 3 === 0 ? 9 : 0;
    return {
      from,
      to: from,
      metrics: {
        load: m(use + car, cov),
        ev: m(car, 1),
        pv: m(solar, cov),
        grid_import: m(Math.max(0, use + car - solar + 4), 1),
        grid_export: m(Math.max(0, solar - 6), 1),
        battery_charge: m(6, 1),
        battery_discharge: m(5.4, 1),
      },
      home: m(use, cov),
      loadIncludesEv: true,
      importCostGbp: 2.1 + i * 0.1,
      exportCreditGbp: 0.4 + (i === 2 ? 2.6 : 0),
      // Live-shaped: the headline is paid minus earned with each side's coverage (day 3 under half priced); the stricter
      // matched-period figure differs and covers less (day 4: 37 %), and the chart must not use it.
      netCostGbp: 1.7 + i * 0.1 - (i === 2 ? 2.6 : 0),
      importCostCoverage: i === 3 ? 0.4 : 1,
      exportCostCoverage: 1,
      observedNetCostGbp: 1.6 + i * 0.1 - (i === 2 ? 2.6 : 0),
      costCoverageFraction: i === 3 ? 0.4 : i === 4 ? 0.37 : 1,
      costObservedSeconds: 0,
      sources: [],
      limitations: [],
    };
  });
}
