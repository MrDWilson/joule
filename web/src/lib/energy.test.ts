import { describe, expect, it } from "vitest";
import type { EnergyMetric, EnergySummary, TelemetryStatus } from "../completion-types";
import {
  change,
  homeStatus,
  homeUse,
  isEmptyReport,
  metricStatus,
  metricValue,
  netCost,
  periodNotes,
  reconcile,
  reportMeta,
  reportTitle,
  sensorHeadline,
  sensorRows,
} from "./energy";

const London = { timeZone: "Europe/London", now: Date.parse("2026-10-05T09:55:00Z") };
const metric = (energyKwh: number | null, extra: Partial<EnergyMetric> = {}): EnergyMetric => ({
  energyKwh,
  observedSeconds: 3600,
  coverageFraction: 1,
  missingIntervals: 0,
  state: "complete",
  gaps: [],
  ...extra,
});
/** The live 5 Oct shape at 10:57: import £1.80, export £0.70, the stricter matched figure £0.06. */
function liveMorning(extra: Partial<EnergySummary> = {}): EnergySummary {
  return {
    from: "2026-10-04T23:00:00Z",
    to: "2026-10-05T09:57:00Z",
    metrics: {
      load: metric(16.908, { counterDayTotalKwh: 16.908, reconciled: true }),
      pv: metric(8.344, { state: "idle_zero", counterDayTotalKwh: 8.344, reconciled: true }),
      grid_import: metric(26.962, { counterDayTotalKwh: 26.962, reconciled: true }),
      grid_export: metric(6.3336, { state: "idle_zero", counterDayTotalKwh: 6.3336, reconciled: true }),
      battery_charge: metric(23.599),
      battery_discharge: metric(11.535),
      ev: metric(4.585, { state: "estimated", estimatedKwh: 0.108 }),
    },
    importCostGbp: 1.8007,
    exportCreditGbp: 0.697,
    observedNetCostGbp: 0.0639,
    costCoverageFraction: 0.744,
    costObservedSeconds: 3600,
    sources: ["HomeAssistant"],
    limitations: [],
    netCostGbp: 1.1037,
    importCostCoverage: 1,
    exportCostCoverage: 1,
    home: metric(12.324),
    loadIncludesEv: true,
    ...extra,
  };
}

describe("netCost", () => {
  it("is paid minus earned, never the matched-period figure", () => {
    const n = netCost(liveMorning(), London);
    expect(n).toMatchObject({
      label: "Net cost",
      text: "£1.10",
      paid: "£1.80",
      earned: "£0.70",
      approximate: false,
      note: null,
    });
  });
  it("says ≈ and names the meter when a side is under 95% priced", () => {
    const s = liveMorning({ exportCostCoverage: 0.8 });
    s.metrics.grid_export.gaps = [{ from: "2026-10-04T23:00:00Z", to: "2026-10-05T01:47:00Z", reason: "offline" }];
    const n = netCost(s, London);
    expect(n.text).toBe("≈ £1.10");
    expect(n.note).toBe("Export meter offline 00:00–02:47");
  });
  it("labels a negative net as earnings", () => {
    expect(netCost(liveMorning({ netCostGbp: -2.5 }), London)).toMatchObject({
      label: "Net earnings",
      text: "£2.50",
      earnings: true,
    });
  });
  it("falls back to paid minus earned for older servers, and a dash when nothing is priced", () => {
    const old = liveMorning();
    delete old.netCostGbp;
    expect(netCost(old, London).text).toBe("£1.10");
    expect(netCost(null).text).toBe("—");
  });
});

describe("homeUse", () => {
  it("leaves the car out when the house meter includes it, and says so", () => {
    expect(homeUse(liveMorning())).toMatchObject({ text: "12.3 kWh", note: "16.9 kWh including the car" });
    expect(homeUse(liveMorning({ loadIncludesEv: false }))).toMatchObject({ text: "16.9 kWh", note: null });
  });
});

describe("metricStatus", () => {
  it("says nothing for complete figures, including solar asleep overnight and a tiny estimated tail", () => {
    const s = liveMorning();
    for (const key of ["load", "pv", "grid_export", "ev"]) expect(metricStatus(key, s.metrics[key], London)).toBeNull();
    expect(metricValue(s.metrics.grid_export)).toBe("6.3 kWh");
  });
  it("names the missing hours for a partial figure", () => {
    const m = metric(5.71, {
      state: "partial",
      gaps: [{ from: "2026-10-04T23:00:00Z", to: "2026-10-05T01:47:00Z", reason: "offline" }],
    });
    expect(metricStatus("grid_export", m, London)).toMatchObject({ label: "Missing 00:00–02:47", tone: "warn" });
    // A long outage is left out, so the figure is a floor, not an estimate: no "≈", the chip names the hours.
    expect(metricValue(m)).toBe("5.7 kWh");
  });
  it("marks a mostly estimated figure and a missing one", () => {
    expect(metricStatus("battery_charge", metric(2, { state: "estimated", estimatedKwh: 0.5 }), London)?.label).toBe(
      "≈ estimated",
    );
    expect(metricStatus("ev", metric(null, { state: "missing", coverageFraction: 0 }), London)?.label).toBe(
      "Not measured",
    );
    expect(metricStatus("ev", metric(null, { state: "no_records" }), London)?.label).toBe("No records");
  });
  it("never shows '% measured' at 98% or above", () => {
    const old = { energyKwh: 3, observedSeconds: 1, coverageFraction: 0.985, missingIntervals: 0 };
    expect(metricStatus("load", old, London)).toBeNull();
    expect(metricStatus("load", { ...old, coverageFraction: 0.81 }, London)?.label).toBe("81% measured");
  });
});

describe("homeStatus", () => {
  const outage = { from: "2026-10-05T10:00:00Z", to: "2026-10-05T12:30:00Z", reason: "offline" };
  it("says nothing for a complete day that matches the house meter", () => {
    expect(homeStatus(liveMorning(), { ...London, singleDay: true })).toBeNull();
  });
  it("names the missing hours from the house meter, and the meter's own total when it differs", () => {
    const s = liveMorning();
    s.metrics.load = metric(14.2, { state: "partial", gaps: [outage], counterDayTotalKwh: 16.9, reconciled: false });
    s.home = metric(9.6, { state: "partial" });
    expect(homeStatus(s, { ...London, singleDay: true })).toBe("Missing 11:00–13:30 · House meter says 16.9 kWh");
    expect(homeStatus(s, { ...London, multiDay: true })).toBe("Missing Mon 5 Oct, 11:00–13:30");
  });
  it("gives a short outage spread across as the reason for ≈", () => {
    const s = liveMorning({ home: metric(12.3, { state: "estimated", estimatedKwh: 2 }) });
    expect(homeStatus(s, London)).toBe("Part of this is estimated from a short outage");
  });
});

describe("reconcile", () => {
  it("lists meters that match and explains one that doesn't", () => {
    const s = liveMorning();
    expect(reconcile(s, London).matched).toEqual(["load", "pv", "grid_import", "grid_export"]);
    s.metrics.load = metric(16.38, {
      counterDayTotalKwh: 36.39,
      reconciled: false,
      coverageFrom: "2026-10-05T13:59:00Z",
    });
    expect(reconcile(s, London).differ).toEqual([
      { metric: "load", meter: "36.4 kWh", measured: "16.4 kWh", reason: "readings began 14:59" },
    ]);
  });
});

describe("change", () => {
  it("reads as a size and a direction, with good news coloured by the figure", () => {
    expect(change("pv", 10, 8, "yesterday")).toMatchObject({ up: true, text: "2.0 kWh more (+25%)", tone: "good" });
    expect(change("cost", 1.1, 1.5, "yesterday", { money: true })).toMatchObject({
      up: false,
      text: "£0.40 better (−27%)",
      word: "better",
      tone: "good",
    });
    expect(change("load", 10, 10.02, "yesterday")?.text).toBe("Same");
  });
  it("says money in words, better or worse, never a bare arrow that reads backwards", () => {
    // Net cost £3.20 against £9.19 this time yesterday: £5.99 better, not "▼ £5.99".
    const better = change("cost", 3.2, 9.19, "this time yesterday", { money: true })!;
    expect(better).toMatchObject({ amount: "£5.99", word: "better", tone: "good", pct: " (−65%)" });
    expect(better.title).toBe("£5.99 better than this time yesterday (£9.19)");
    expect(change("cost", 4.1, 2.55, "the day before", { money: true })).toMatchObject({
      text: "£1.55 worse (+61%)",
      tone: "bad",
    });
    expect(change("load", 16, 22, "the day before")).toMatchObject({ text: "6.0 kWh less (−27%)", word: "less" });
  });
  it("leaves out a percentage of a tiny earlier figure, a sign flip or a change over 200%", () => {
    expect(change("cost", 2.4, 0.4, "yesterday", { money: true })?.pct).toBe("");
    expect(change("cost", 1.2, -0.5, "yesterday", { money: true })?.pct).toBe("");
    expect(change("pv", 0.6, 0.2, "yesterday")?.pct).toBe("");
    expect(change("pv", 12, 3, "yesterday")).toMatchObject({ text: "9.0 kWh more", pct: "" });
    expect(change("pv", 9, 3, "yesterday")?.pct).toBe(" (+200%)");
  });
  it("is left out when either side is missing or under 95% measured", () => {
    expect(change("pv", 10, null, "yesterday")).toBeNull();
    expect(change("pv", 10, 8, "yesterday", { previousCoverage: 0.6 })).toBeNull();
  });
});

describe("periodNotes", () => {
  it("names gaps on several meters once, in local time, with recovered energy", () => {
    const s = liveMorning();
    const gap = { from: "2026-10-04T23:00:00Z", to: "2026-10-05T01:47:00Z", reason: "offline" };
    s.metrics.grid_export.gaps = [{ ...gap, knownKwh: 0.62 }];
    s.metrics.grid_import.gaps = [gap];
    s.metrics.battery_charge.gaps = [
      { from: "2026-10-05T09:56:59Z", to: "2026-10-05T09:57:00Z", reason: "no_samples" },
    ];
    expect(periodNotes(s, London).notes).toEqual(["Import and Export meters offline 00:00–02:47."]);
    s.metrics.grid_import.gaps = [];
    expect(periodNotes(s, London).notes).toEqual([
      "Export meter offline 00:00–02:47 · 0.62 kWh in that time isn't counted.",
    ]);
  });
  it("makes the same stretch on several days one note", () => {
    const s = liveMorning({ from: "2026-10-02T23:00:00Z", to: "2026-10-05T09:57:00Z" });
    s.metrics.pv.gaps = ["2026-10-03", "2026-10-04"].map((d) => ({
      from: `${d}T10:00:00Z`,
      to: `${d}T12:30:00Z`,
      reason: "offline",
    }));
    expect(periodNotes(s, London).notes).toEqual(["Solar meter offline 11:00–13:30 on 2 days."]);
  });
  it("says once when records start part-way through", () => {
    const s = liveMorning();
    s.metrics.load.coverageFrom = "2026-10-05T08:00:00Z";
    expect(periodNotes(s, London).notes[0]).toBe("Records start Today 09:00; earlier energy isn't counted.");
  });
});

describe("sensorRows", () => {
  const status = {
    demo: false,
    configured: true,
    homeAssistantDirect: true,
    lastSource: "HomeAssistant",
    timeZone: "Europe/London",
    lastCollection: "2026-10-05T09:53:00Z",
    error: null,
    maxGapMinutes: 15,
    missingMappings: [],
    entityMappings: {
      ev: "sensor.hypervolt_session_energy_total_increasing",
      soc: "sensor.my_home_percentage_charged",
    },
    latestReadings: {
      intelligent_slots: {
        value: null,
        unit: "",
        time: "2026-10-05T09:53:00Z",
        status: "observed",
        entityId: "binary_sensor.x",
        source: "HomeAssistant",
        rawState: "off",
      },
      ev: {
        value: null,
        unit: "",
        time: "2026-10-05T09:53:00Z",
        status: "idle",
        entityId: "sensor.hypervolt_session_energy_total_increasing",
        source: "HomeAssistant",
        rawState: "unknown",
        rawUnit: "Wh",
        sourceUpdatedAt: "2026-10-05T05:30:02Z",
        expected: true,
        reason: "Not charging (the charger reports unknown between sessions).",
        profile: "session_counter",
      },
      grid_export: {
        value: 6.3336,
        unit: "kWh",
        time: "2026-10-05T09:53:00Z",
        status: "observed",
        entityId: "sensor.e",
        source: "HomeAssistant",
        sourceUpdatedAt: "2026-10-05T09:41:00Z",
      },
      soc: {
        value: 83.92,
        unit: "%",
        time: "2026-10-05T09:53:00Z",
        status: "observed",
        entityId: "sensor.my_home_percentage_charged",
        source: "HomeAssistant",
      },
      pv: {
        value: null,
        unit: "",
        time: "2026-10-05T09:53:00Z",
        status: "unavailable",
        entityId: "sensor.pv",
        source: "HomeAssistant",
        lastObservedValue: 1.2,
        lastObservedAt: "2026-10-05T07:20:00Z",
      },
    },
  } as unknown as TelemetryStatus;
  it("keeps a fixed order and says each state plainly", () => {
    const rows = sensorRows(status, London);
    expect(rows.map((r) => r.key)).toEqual(["pv", "grid_export", "ev", "soc", "intelligent_slots"]);
    const by = Object.fromEntries(rows.map((r) => [r.key, r]));
    expect(by.ev).toMatchObject({
      stateLabel: "Idle",
      ok: true,
      value: "Not charging",
      note: "Not charging (the charger reports unknown between sessions).",
    });
    expect(by.grid_export).toMatchObject({
      stateLabel: "Live",
      value: "6.33 kWh",
      when: "changed 10:41",
    });
    expect(by.soc.value).toBe("84%");
    expect(by.intelligent_slots.value).toBe("None now");
    expect(by.pv).toMatchObject({
      stateLabel: "Offline",
      ok: false,
      value: "1.20 kWh",
      note: "Offline since Today 08:20 · last reading 1.20 kWh.",
    });
  });
  it("names the house meter apart from Home use and marks a daily counter's reading as today's", () => {
    const load = {
      value: 20.4,
      unit: "kWh",
      time: "2026-10-05T09:53:00Z",
      status: "observed",
      entityId: "sensor.house_load_today",
      source: "HomeAssistant",
      profile: "daily_counter",
    };
    const meters = {
      ...status,
      latestReadings: {
        load,
        grid_import: { ...load, value: 31004.2, entityId: "sensor.meter", profile: "lifetime_counter" },
        import_tariff: { ...load, value: 24.5, unit: "p/kWh", entityId: "sensor.rate", profile: "price" },
        soc: { ...status.latestReadings.soc, profile: "state" },
      },
    } as unknown as TelemetryStatus;
    const by = Object.fromEntries(sensorRows(meters, London).map((r) => [r.key, r]));
    expect(by.load).toMatchObject({ label: "House meter", value: "20.40 kWh today", stateLabel: "Daily total" });
    expect(by.grid_import).toMatchObject({ stateLabel: "Meter total" });
    expect(by.grid_import.value).not.toMatch(/today/);
    expect(by.import_tariff.stateLabel).toBe("Current rate");
    expect(by.soc.stateLabel).toBe("Now");
    const withCar = sensorRows({ ...meters, loadIncludesEv: true }, London).find((r) => r.key === "load")!;
    expect(withCar.label).toBe("Home use incl. car");
  });
  it("says when a sensor was checked only when it differs from the rest", () => {
    const late = {
      ...status,
      latestReadings: {
        ...status.latestReadings,
        grid_export: { ...status.latestReadings.grid_export, time: "2026-10-05T09:33:00Z" },
      },
    } as TelemetryStatus;
    const row = sensorRows(late, London).find((r) => r.key === "grid_export")!;
    expect(row.when).toBe("changed 10:41 · checked 22 min ago");
  });
  it("leaves off a 'changed' time that three or more sensors share", () => {
    const at = "2026-10-05T09:41:00Z";
    const same = {
      ...status,
      latestReadings: Object.fromEntries(
        Object.entries(status.latestReadings).map(([k, r]) => [k, { ...r, sourceUpdatedAt: at }]),
      ),
    } as TelemetryStatus;
    same.latestReadings.soc = { ...same.latestReadings.soc, sourceUpdatedAt: "2026-10-05T09:12:00Z" };
    const rows = sensorRows(same, London);
    expect(rows.find((r) => r.key === "grid_export")!.when).toBe("");
    expect(rows.find((r) => r.key === "soc")!.when).toBe("changed 10:12");
  });
  it("sums up health in one line", () => {
    const rows = sensorRows(status, London);
    expect(sensorHeadline(rows, status.lastCollection, London)).toBe("1 of 5 sensors need a look · checked 2 min ago");
    expect(
      sensorHeadline(
        rows.filter((r) => r.ok),
        status.lastCollection,
        London,
      ),
    ).toBe("All 4 sensors OK · checked 2 min ago");
  });
});

describe("reportTitle", () => {
  it("matches the server's titles and keeps a part day honest on later days", () => {
    const part = { kind: "Daily", from: "2026-10-04T23:00:00Z", to: "2026-10-05T08:53:00Z" };
    // A snapshot of part of a day reads the same today and tomorrow: never "So far today".
    expect(reportTitle(part, London)).toBe("Report · Mon 5 Oct, to 09:53");
    expect(reportTitle(part, { ...London, now: Date.parse("2026-10-06T09:00:00Z") })).toBe(
      "Report · Mon 5 Oct, to 09:53",
    );
    expect(reportTitle({ kind: "Daily", from: "2026-10-03T23:00:00Z", to: "2026-10-04T23:00:00Z" }, London)).toBe(
      "Daily report · Sun 4 Oct",
    );
    expect(reportTitle({ kind: "Weekly", from: "2026-09-27T23:00:00Z", to: "2026-10-04T23:00:00Z" }, London)).toBe(
      "Weekly report · 28 Sep – 4 Oct",
    );
  });
  it("says how each report came to be: arrived on schedule, saved by you, or made later", () => {
    const sun = { kind: "Daily", from: "2026-10-03T23:00:00Z", to: "2026-10-04T23:00:00Z" };
    expect(reportMeta({ ...sun, createdAt: "2026-10-05T07:00:20Z" }, 8, London)).toBe("arrived Today 08:00");
    expect(reportMeta({ ...sun, createdAt: "2026-10-05T18:09:00Z" }, 8, London)).toBe("made Today 19:09");
    const part = { kind: "Daily", from: "2026-10-04T23:00:00Z", to: "2026-10-05T08:53:00Z" };
    expect(reportMeta({ ...part, createdAt: "2026-10-05T08:53:00Z" }, 8, London)).toBe("saved Today 09:53");
  });
  it("recognises reports with nothing in them", () => {
    expect(
      isEmptyReport({
        summary: "Demo figures from made-up readings. No meter readings for this period.",
        textVersion: 2,
      }),
    ).toBe(true);
    expect(isEmptyReport({ summary: "No authoritative energy intervals were available", textVersion: 0 })).toBe(true);
    expect(isEmptyReport({ summary: "You used 1.0 kWh.", textVersion: 2 })).toBe(false);
  });
});
