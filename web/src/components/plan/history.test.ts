import { describe, expect, it } from "vitest";
import timeline from "../today/fixtures/live-timeline-2026-10-05.json";
import { contradictions, flagSentence, historyRows, historyWindows, type HistoryRow } from "./history";
import { windowWhen, type PlanSlotLike } from "./windows";
import type { PlanEvidence } from "../../completion-types";

const NOW = Date.parse("2026-10-05T15:54:00Z");
const recent = timeline.slots as PlanSlotLike[];
const metric = (energyKwh: number) => ({ energyKwh, observedSeconds: 1800, coverageFraction: 1, missingIntervals: 0 });

describe("historyRows", () => {
  it("keeps only finished half-hours, with one actual per quantity", () => {
    const rows = historyRows(recent, NOW);
    expect(rows).toHaveLength(47);
    const last = rows.at(-1)!;
    expect(last.home.actual.value).toBeCloseTo(0.2066, 3);
    expect(last.home.actual.approx).toBe(false);
    // A boundary-allocated figure reads as an estimate.
    expect(rows.at(-2)!.home.actual.approx).toBe(true);
  });
  it("flags the overnight charge half-hour where the battery fell", () => {
    const rows = historyRows(recent, NOW);
    const fell = rows.find((r) => r.slot.time.startsWith("2026-10-05T02:00"))!;
    expect(fell.flags).toContain("Battery fell during a charge");
  });
  it("names why the battery fell during a charge: the car, or a pause Predbat planned", () => {
    const at = Date.parse("2026-10-05T02:00:00Z");
    const row = (extra: Partial<PlanSlotLike>) =>
      historyRows(
        [
          {
            time: new Date(at).toISOString(),
            durationMinutes: 30,
            action: "charge",
            actionKey: "charge",
            socForecast: 40,
            socForecastEnd: 50,
            socActualStart: 40,
            socActual: 36,
            ...extra,
          } as PlanSlotLike,
        ],
        at + 3600000,
      )[0];
    expect(row({ evActual: 1.8 }).cause).toBe("car");
    expect(row({ evActual: 0.2, socForecastEnd: 40 }).cause).toBe("pause");
    expect(row({}).cause).toBeNull();
    // No flag, no cause.
    expect(row({ socActual: 48, evActual: 1.8 }).cause).toBeNull();
    const car = row({ evActual: 1.8 });
    expect(flagSentence(car, "Battery fell during a charge", "Europe/London")).toBe(
      "During the 03:00 charge, the battery fell 4 points while the car took 1.8 kWh",
    );
    expect(flagSentence(row({ socForecastEnd: 40 }), "Battery fell during a charge", "Europe/London")).toBe(
      "During the 03:00 charge, the battery fell 4 points; Predbat had planned a pause here",
    );
  });
  it("joins grid figures from the meter evidence", () => {
    const evidence: PlanEvidence = {
      planId: "p",
      slots: [
        {
          time: "2026-10-05T01:30:00+00:00",
          durationMinutes: 30,
          actual: {
            from: "",
            to: "",
            metrics: { grid_import: metric(2.1), grid_export: metric(1.4) },
          } as never,
          estimatedLoadKwh: null,
          estimatedPvKwh: null,
          estimateMethod: "",
        },
      ],
    };
    const row = historyRows(recent, NOW, evidence).find((r) => r.slot.time.startsWith("2026-10-05T01:30"))!;
    expect(row.gridIn).toBe(2.1);
    expect(row.gridOut).toBe(1.4);
    expect(row.flags).toContain("Imported from the grid during an export");
    expect(flagSentence(row, "Imported from the grid during an export", "Europe/London")).toBe(
      "During the 02:30 export, 2.1 kWh came from the grid",
    );
  });
});

describe("historyWindows", () => {
  it("groups the night into its windows and carries the flags up", () => {
    const windows = historyWindows(historyRows(recent, NOW), NOW, 4);
    expect(
      windows.map((w) => `${w.window.short} ${windowWhen(w.window.start, w.window.end, NOW, "Europe/London")}`),
    ).toEqual([
      "Power home Yesterday 17:00–18:00",
      "Export Last night 18:00–19:00",
      "Power home Last night 19:00–21:00",
      "Export Last night 21:00–21:30",
      "Power home Last night 21:30–23:30",
      "Charge Last night 23:30–05:30",
      "Power home Today 05:30–16:30",
    ]);
    const night = windows[5];
    // The charge-then-export start and the 02:30 export are trades inside the charging window.
    expect(night.window.trades?.count).toBe(2);
    expect(night.window.pauses).toHaveLength(1);
    expect(Math.round(night.soc.start!)).toBe(2);
    expect(Math.round(night.soc.end!)).toBe(90);
    expect(night.flags).toContain("Battery fell during a charge");
  });
});

describe("flagSentence", () => {
  it("keeps any other flag readable, with its time", () => {
    const r = {
      start: Date.parse("2026-10-05T02:00:00Z"),
      slot: { action: "demand" },
      soc: {},
    } as unknown as HistoryRow;
    expect(flagSentence(r, "Something odd", "Europe/London")).toBe("Something odd (03:00)");
  });
});

describe("contradictions", () => {
  it("calls out a big miss against the planned end level", () => {
    expect(contradictions("demand", { plannedStart: 80, plannedEnd: 70, start: 80, end: 55 }, null, 30)).toEqual([
      "Ended 15 points below plan",
    ]);
    expect(contradictions("demand", { plannedStart: 80, plannedEnd: 70, start: 80, end: 66 }, null, 30)).toEqual([]);
  });
});
