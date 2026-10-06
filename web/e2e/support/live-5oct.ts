import type { Page } from "@playwright/test";

/**
 * A live-shaped, anonymised snapshot of /api/telemetry/status and /api/telemetry/summary at 5 Oct 2026, 10:57 BST
 * (entity ids are synthetic; no MPANs, serials or names).
 *
 * The figures are a case that once looked wrong on the dashboard: import £1.80, export £0.70, so net £1.10, while the stricter
 * matched-period figure was £0.06; grid export 6.33 kWh on the meter. Every daily meter reconciles with Home Assistant's
 * own day total. The car charger is idle between sessions (Home Assistant says "unknown"); solar was asleep overnight.
 */
export const NOW = "2026-10-05T09:57:00.000Z";
export const MIDNIGHT = "2026-10-04T23:00:00.000Z";
export const ZONE = "Europe/London";

type Metric = Record<string, unknown>;
const metric = (energyKwh: number | null, extra: Metric = {}): Metric => ({
  energyKwh,
  observedSeconds: 39420,
  coverageFraction: 1,
  missingIntervals: 0,
  measuredSeconds: 39420,
  idleSeconds: 0,
  estimatedSeconds: 0,
  estimatedKwh: null,
  missingKwh: 0,
  gaps: [],
  counterDayTotalKwh: null,
  reconciled: null,
  profile: "daily_counter",
  state: "complete",
  coverageFrom: MIDNIGHT,
  coverageTo: NOW,
  ...extra,
});
const counter = (kwh: number, extra: Metric = {}) =>
  metric(kwh, { counterDayTotalKwh: kwh, reconciled: true, ...extra });

export function todaySummary(from = MIDNIGHT, to = NOW) {
  return {
    from,
    to,
    metrics: {
      load: counter(12.276),
      pv: counter(1.41, { profile: "solar_daily", state: "idle_zero", idleSeconds: 27489 }),
      grid_import: counter(26.962),
      grid_export: counter(6.33355078125, { state: "idle_zero", idleSeconds: 9765 }),
      battery_charge: counter(21.776),
      battery_discharge: counter(6.9),
      ev: metric(4.585, { profile: "session_counter", state: "estimated", estimatedKwh: 0.108, idleSeconds: 33000 }),
    },
    importCostGbp: 1.8007,
    exportCreditGbp: 0.697,
    observedNetCostGbp: 0.0639,
    costCoverageFraction: 0.744,
    costObservedSeconds: 29330,
    sources: ["HomeAssistant"],
    limitations: [
      "netCostGbp is import cost minus export credit, each with its own coverage. observedNetCostGbp counts only periods both meters cover.",
      "Home Assistant 'unavailable' means a device is offline. 'unknown' is stored as idle.",
    ],
    netCostGbp: 1.1037,
    importCostCoverage: 1,
    exportCostCoverage: 1,
    importCostEstimated: false,
    exportCostEstimated: false,
    unpricedGridKwh: 0,
    gridEnergyUnknown: false,
    estimatedCostGbp: 0,
    costGaps: [],
    home: metric(7.691, { profile: "derived" }),
    loadIncludesEv: true,
  };
}

/** The same stretch of 4 Oct, for "vs this time yesterday". */
export function yesterdaySummary(from: string, to: string) {
  const s = todaySummary(from, to);
  const scale = (m: Metric, f: number) => ({
    ...m,
    energyKwh: Number(m.energyKwh) * f,
    counterDayTotalKwh: null,
    reconciled: null,
  });
  return {
    ...s,
    metrics: Object.fromEntries(Object.entries(s.metrics).map(([k, m]) => [k, scale(m, k === "pv" ? 1.2 : 0.9)])),
    importCostGbp: 2.3,
    exportCreditGbp: 0.4,
    netCostGbp: 1.9,
    home: scale(s.home, 0.9),
  };
}

const reading = (value: number | null, unit: string, extra: Metric = {}) => ({
  value,
  unit,
  time: "2026-10-05T09:53:30Z",
  status: "observed",
  source: "HomeAssistant",
  rawState: value == null ? "unknown" : String(value),
  rawUnit: unit,
  sourceUpdatedAt: "2026-10-05T09:41:22Z",
  lastObservedValue: value,
  lastObservedAt: "2026-10-05T09:53:30Z",
  expected: true,
  reason: null,
  ageSeconds: 9,
  stale: false,
  profile: "daily_counter",
  ...extra,
});

export function status() {
  const ids: Record<string, string> = {
    load: "sensor.owned_home_usage",
    pv: "sensor.owned_solar_generated",
    grid_import: "sensor.owned_grid_imported",
    grid_export: "sensor.owned_grid_exported",
    battery_charge: "sensor.owned_battery_charged",
    battery_discharge: "sensor.owned_battery_discharged",
    ev: "sensor.owned_charger_session_energy_total_increasing",
    soc: "sensor.owned_percentage_charged",
    import_tariff: "sensor.octopus_energy_electricity_00a0000000_1000000000000_current_rate",
    export_tariff: "sensor.octopus_energy_electricity_00a0000000_1000000000001_export_current_rate",
    intelligent_slots: "binary_sensor.octopus_energy_00000000_0000_4000_8000_000000000000_intelligent_dispatching",
  };
  const latest: Record<string, Metric> = {
    load: reading(12.276, "kWh"),
    pv: reading(1.41, "kWh", { profile: "solar_daily" }),
    grid_import: reading(26.962056640625, "kWh"),
    grid_export: reading(6.33355078125, "kWh"),
    battery_charge: reading(21.776, "kWh"),
    battery_discharge: reading(6.9, "kWh"),
    ev: reading(null, "", {
      status: "idle",
      rawState: "unknown",
      rawUnit: "Wh",
      sourceUpdatedAt: "2026-10-05T05:30:02Z",
      lastObservedValue: 0.005,
      lastObservedAt: "2026-10-05T01:57:45Z",
      reason: "Not charging (the charger reports unknown between sessions).",
      profile: "session_counter",
    }),
    soc: reading(74.05, "%", { profile: "state" }),
    import_tariff: reading(24.4, "p/kWh", { rawState: "0.244", rawUnit: "GBP/kWh", profile: "price" }),
    export_tariff: reading(15, "p/kWh", { rawState: "0.15", rawUnit: "GBP/kWh", profile: "price" }),
    intelligent_slots: reading(null, "", { rawState: "off", profile: "state", lastObservedValue: null }),
  };
  for (const [k, v] of Object.entries(latest)) v.entityId = ids[k];
  return {
    demo: false,
    configured: true,
    homeAssistantDirect: true,
    lastSource: "HomeAssistant",
    timeZone: ZONE,
    lastCollection: "2026-10-05T09:53:31Z",
    error: null,
    entityMappings: ids,
    missingMappings: [],
    maxGapMinutes: 15,
    latestReadings: latest,
    firstObservationAt: "2026-10-02T13:59:17Z",
    profiles: {},
    loadIncludesEv: true,
  };
}

/** Routes the measured-figure endpoints to the 5 Oct snapshot and fixes the clock at 10:57 BST. */
export async function useLive5Oct(page: Page) {
  await page.clock.setFixedTime(new Date(NOW));
  await page.route("**/api/telemetry/status", (route) => route.fulfill({ json: status() }));
  await page.route("**/api/telemetry/summary?*", (route) => {
    const url = new URL(route.request().url());
    const from = url.searchParams.get("from")!,
      to = url.searchParams.get("to")!;
    return route.fulfill({ json: from === MIDNIGHT ? todaySummary(from, to) : yesterdaySummary(from, to) });
  });
  await page.route("**/api/telemetry/history?*", (route) => route.fulfill({ json: { slots: [] } }));
}
