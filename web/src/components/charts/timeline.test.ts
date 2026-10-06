import { describe, expect, it } from "vitest";
import {
  actionBlocks,
  attachEarlier,
  buildRows,
  describeTimeline,
  freshSoc,
  historyByStart,
  mergeTimeline,
  type TimelineSlot,
} from "./timeline";
import { analyseGaps, classify, gapLabel, stateNote, type SlotState } from "./gaps";
import { shiftLocalDays } from "../../lib/comparison";

const T0 = Date.parse("2026-10-05T09:00:00Z");
const MIN = 60000;
const slot = (minutesFromT0: number, extra: Partial<TimelineSlot> = {}): TimelineSlot => ({
  time: new Date(T0 + minutesFromT0 * MIN).toISOString(),
  durationMinutes: 30,
  loadForecast: 0.4,
  pvForecast: 0.2,
  loadActual: null,
  pvActual: null,
  socForecast: 50,
  importRate: 20,
  exportRate: 15,
  action: "demand",
  cost: 0.05,
  ...extra,
});

describe("mergeTimeline", () => {
  it("folds the plan's partial first slot into the half-hour in progress", () => {
    const history = [slot(0, { loadActual: 0.3 }), slot(30, { loadForecast: 0.6 })];
    // Plan made at 09:55: a 5-minute slot, then whole half-hours.
    const plan = [
      slot(55, { durationMinutes: 5, loadForecast: 0.05, action: "charge", importRate: 7 }),
      slot(60),
      slot(90),
    ];
    const merged = mergeTimeline(history, plan);
    expect(merged.map((s) => s.time.slice(11, 16))).toEqual(["09:00", "09:30", "10:00", "10:30"]);
    const current = merged[1];
    expect(current.durationMinutes).toBe(30);
    // 25 of 30 minutes of the old forecast plus the plan's 5 minutes.
    expect(current.loadForecast).toBeCloseTo(0.6 * (25 / 30) + 0.05);
    expect(current.action).toBe("charge");
    expect(current.importRate).toBe(7);
  });
  it("keeps history up to an aligned plan start and drops history the plan replaces", () => {
    const merged = mergeTimeline([slot(0), slot(30), slot(60)], [slot(60, { loadForecast: 1 }), slot(90)]);
    expect(merged.map((s) => s.time.slice(11, 16))).toEqual(["09:00", "09:30", "10:00", "10:30"]);
    expect(merged[2].loadForecast).toBe(1);
  });
  it("handles a plan or history alone", () => {
    expect(mergeTimeline([], [slot(0)])).toHaveLength(1);
    expect(mergeTimeline([slot(0)], [])).toHaveLength(1);
  });
});

describe("buildRows", () => {
  const now = T0 + 95 * MIN; // 10:35
  it("normalises energy to kWh per half-hour and keeps the raw kWh", () => {
    const { rows } = buildRows([slot(0, { durationMinutes: 5, loadForecast: 0.06 })], { now });
    expect(rows[0].home.forecast).toBeCloseTo(0.36);
    expect(rows[0].home.forecastRaw).toBe(0.06);
  });
  it("classifies past, current and future slots", () => {
    const { rows } = buildRows([slot(0, { loadActual: 0.3 }), slot(90), slot(120)], { now });
    expect(rows.map((r) => r.phase)).toEqual(["past", "current", "future"]);
    expect(rows[0].home).toMatchObject({ actual: 0.3, state: "value", approx: false });
    expect(rows[1].home.state).toBe("none");
  });
  it("counts solar as zero overnight where Predbat forecast none, but not in daylight", () => {
    const { rows } = buildRows([slot(0, { pvForecast: 0 }), slot(30, { pvForecast: 0.5 })], { now });
    expect(rows[0].solar).toMatchObject({ state: "zero", actual: 0 });
    expect(rows[1].solar).toMatchObject({ state: "missing", actual: null });
    expect(stateNote("zero", "solar")).toBe("Solar sensor asleep overnight; counted as 0");
  });
  it("marks shared-out readings as approximate", () => {
    const { rows } = buildRows([slot(0, { loadActual: 0.3, loadActualMethod: "estimated" })], { now });
    expect(rows[0].home.approx).toBe(true);
  });
  it("draws home without the car when a car is in the picture", () => {
    const { rows, car } = buildRows(
      [
        slot(0, { loadActual: 3.5, evActual: 3.2, homeActual: 0.3 }),
        slot(30, { loadActual: 3.4, evActual: 3.1, homeActual: 0.3 }),
      ],
      { now },
    );
    expect(car).toBe(true);
    expect(rows[0].home.actualRaw).toBe(0.3);
    expect(rows[1].home.actualRaw).toBe(0.3);
    expect(rows[0].ev.actualRaw).toBe(3.2);
  });
  it("never takes the car off a load meter that excludes it", () => {
    // The server leaves homeActual out exactly when the load meter does not include the car: the load is home use.
    const { rows, car } = buildRows([slot(0, { loadActual: 0.6, evActual: 3.2 })], { now });
    expect(car).toBe(true);
    expect(rows[0].home.actualRaw).toBe(0.6);
    expect(rows[0].ev.actualRaw).toBe(3.2);
  });
  it("keeps the whole load for a slot with no car reading at all, rather than leaving a hole", () => {
    const { rows } = buildRows([slot(0, { loadActual: 0.5 }), slot(30, { carKwh: 2 })], { now });
    expect(rows[0].home.actualRaw).toBe(0.5);
    expect(rows[0].ev.state).toBe("missing");
  });
  it("never reports a car gap when no car is in the picture", () => {
    const { rows, car } = buildRows([slot(0, { loadActual: 0.5 })], { now });
    expect(car).toBe(false);
    expect(rows[0].ev.state).toBe("none");
  });
  it("tags free and saving sessions, and Intelligent Octopus dispatches", () => {
    const { rows } = buildRows([slot(120, { importRate: 0 }), slot(150, { importRateType: "saving" }), slot(180)], {
      now,
      dispatches: [{ start: T0 + 180 * MIN, end: T0 + 200 * MIN }],
    });
    expect(rows.map((r) => r.tags)).toEqual([["free"], ["saving"], ["iog"]]);
  });
  it("explains estimated prices", () => {
    const { rows } = buildRows([slot(120, { importRateType: "copy", rateEstimated: true })], { now });
    expect(rows[0].price).toMatchObject({ estimated: true, note: "Not published yet: copied from the previous day" });
  });
  it("takes Predbat's labels, targets and the car-hold variant from the server fields", () => {
    const { rows } = buildRows(
      [slot(120, { action: "charge", actionKey: "charge", targetPercent: 100, reasonText: "Cheap" })],
      { now },
    );
    expect(rows[0].action).toMatchObject({
      key: "charge",
      label: "Charge from the grid",
      target: 100,
      reason: "Cheap",
    });
  });
  it("fills a missing forecast end level from the next slot", () => {
    const { rows } = buildRows([slot(120, { socForecast: 40 }), slot(150, { socForecast: 60 })], { now });
    expect(rows[0].soc.forecastEnd).toBe(60);
  });
});

describe("gaps", () => {
  const now = T0 + 600 * MIN;
  const states = (pattern: string): SlotState[] =>
    [...pattern].map((c) => (c === "v" ? "value" : c === "m" ? "missing" : c === "z" ? "zero" : "none"));
  const bounds = (k: number) => ({
    starts: Array.from({ length: k }, (_, i) => T0 + i * 30 * MIN),
    ends: Array.from({ length: k }, (_, i) => T0 + (i + 1) * 30 * MIN),
  });
  it("bridges up to two missing slots between drawn neighbours and washes longer runs", () => {
    const s = states("vmvvmmmmvz");
    const { starts, ends } = bounds(s.length);
    expect(analyseGaps(s, starts, ends)).toEqual({ bridges: [[0, 2]], bands: [[4, 7]] });
  });
  it("never bridges to the edge", () => {
    const s = states("mvvm");
    const { starts, ends } = bounds(s.length);
    expect(analyseGaps(s, starts, ends)).toEqual({
      bridges: [],
      bands: [
        [0, 0],
        [3, 3],
      ],
    });
  });
  it("waits a quarter of an hour before calling a reading missing", () => {
    expect(classify({ value: null, end: now - 5 * MIN, metric: "home" }, now)).toBe("awaiting");
    expect(classify({ value: null, end: now - 20 * MIN, metric: "home" }, now)).toBe("missing");
    expect(classify({ value: null, end: now - 20 * MIN, method: "idle", metric: "ev" }, now)).toBe("zero");
  });
  it("names the meter in the outage label", () => {
    expect(gapLabel("solar", "18:30–19:30")).toBe("Solar meter offline 18:30–19:30");
  });
});

describe("action blocks", () => {
  it("groups consecutive slots with the same action and target", () => {
    const { rows } = buildRows(
      [
        slot(0, { action: "charge", targetPercent: 100 }),
        slot(30, { action: "charge", targetPercent: 100 }),
        slot(60, { action: "charge", targetPercent: 80 }),
        slot(90, { action: "demand" }),
      ],
      { now: T0 },
    );
    expect(actionBlocks(rows).map((b) => [b.from, b.to, b.action.key])).toEqual([
      [0, 1, "charge"],
      [2, 2, "charge"],
      [3, 3, "demand"],
    ]);
  });
});

describe("earlier period", () => {
  it("matches each slot to the same local clock time a calendar day earlier, across the clock change", () => {
    // Sun 25 Oct 2026, 10:00 GMT: yesterday's 10:00 was BST (09:00Z).
    const start = Date.parse("2026-10-25T10:00:00Z");
    const { rows } = buildRows([{ ...slot(0), time: new Date(start).toISOString() }], { now: start + 3600000 });
    const map = historyByStart([{ time: "2026-10-24T09:00:00Z", durationMinutes: 30, load: 0.5 }], false);
    attachEarlier(rows, map, (ms) => shiftLocalDays(ms, -1, "Europe/London"), start + 3600000);
    expect(rows[0].earlier).toBe(0.5);
  });
  it("uses home without the car when a car is in the picture", () => {
    const map = historyByStart([{ time: "2026-10-24T09:00:00Z", durationMinutes: 30, load: 3, home: 0.4 }], true);
    expect(map.get(Date.parse("2026-10-24T09:00:00Z"))).toBe(0.4);
  });
  it("compares with the whole load when the load meter excludes the car", () => {
    const map = historyByStart([{ time: "2026-10-24T09:00:00Z", durationMinutes: 30, load: 0.6, ev: 3 }], true);
    expect(map.get(Date.parse("2026-10-24T09:00:00Z"))).toBe(0.6);
  });
});

describe("describeTimeline", () => {
  it("describes only the battery for the battery outlook", () => {
    const now = T0 + 60 * MIN;
    const { rows } = buildRows(
      [slot(0, { loadActual: 0.5 }), slot(60, { socForecast: 40 }), slot(90, { socForecast: 80 })],
      { now },
    );
    const wall = (ms: number) => new Date(ms).toISOString().slice(11, 16);
    expect(describeTimeline(rows, { now, wall, car: false, batteryOnly: true })).toBe(
      "Battery 40% now, lowest 40% at 10:00, highest 80% at 10:30.",
    );
  });
  it("says the measured level for 'now', and the plan only for lowest and highest", () => {
    // Demo data once read "Battery 97% now" from the plan beside a tile showing the 44% reading.
    const now = T0 + 60 * MIN;
    const { rows } = buildRows([slot(60, { socForecast: 97 }), slot(90, { socForecast: 80 })], { now });
    const wall = (ms: number) => new Date(ms).toISOString().slice(11, 16);
    expect(describeTimeline(rows, { now, wall, car: false, batteryOnly: true, socNow: 44 })).toBe(
      "Battery 44% now, lowest 80% at 10:30, highest 97% at 10:00.",
    );
  });
  it("lets the page describe the plan in its own windows", () => {
    const now = T0 + 60 * MIN;
    const { rows } = buildRows(
      [slot(60, { socForecast: 40, action: "charge" }), slot(90, { socForecast: 80, action: "charge" })],
      { now },
    );
    const wall = (ms: number) => new Date(ms).toISOString().slice(11, 16);
    const spans: [number, number][] = [];
    const text = describeTimeline(rows, {
      now,
      wall,
      car: false,
      describePlan: (from, to) => {
        spans.push([from, to]);
        return "Next 1 h: Predbat plans to charge to 80% today 10:00–11:00 at 6.67p.";
      },
    });
    expect(text).toContain("Next 1 h: Predbat plans to charge to 80% today 10:00–11:00 at 6.67p.");
    expect(text).not.toContain("charge from the grid");
    expect(spans).toEqual([[now, T0 + 120 * MIN]]);
  });
  it("trusts only a recent battery reading", () => {
    const now = T0;
    expect(freshSoc({ value: 44, time: new Date(T0 - 5 * MIN).toISOString() }, now)).toBe(44);
    expect(freshSoc({ value: 44, time: new Date(T0 - 90 * MIN).toISOString() }, now)).toBeNull();
    expect(freshSoc({ value: null, time: new Date(T0).toISOString() }, now)).toBeNull();
    expect(freshSoc(null, now)).toBeNull();
  });
  it("names the day of windows that are not today", () => {
    const now = T0;
    const { rows } = buildRows(
      [slot(0, { socForecast: 50 }), slot(15 * 60, { action: "charge", importRate: 7, socForecast: 20 })],
      { now },
    );
    const wall = (ms: number) => new Date(ms).toISOString().slice(11, 16);
    const day = (ms: number) => new Date(ms).toISOString().slice(0, 10);
    expect(describeTimeline(rows, { now, wall, car: false, day })).toContain(
      "charge from the grid tomorrow 00:00–00:30",
    );
  });
  it("writes later days as '05:30 Wed', never 'Wed 7 05:30'", () => {
    const now = T0;
    const { rows } = buildRows(
      [slot(0, { socForecast: 50 }), slot(40 * 60, { action: "charge", importRate: 7, socForecast: 20 })],
      { now },
    );
    const wall = (ms: number) => new Date(ms).toISOString().slice(11, 16);
    const weekdays = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
    const day = (ms: number) => `${weekdays[new Date(ms).getUTCDay()]} ${new Date(ms).getUTCDate()}`;
    const text = describeTimeline(rows, { now, wall, car: false, day });
    expect(text).toContain("charge from the grid 01:00–01:30 Wed");
    expect(text).not.toMatch(/Wed 7/);
  });
  it("says nothing of lows, highs or the next hours for a window that ends in the half-hour in progress", () => {
    // Past 24 h at 19:10: the last row is 19:00–19:30, in progress. It once read "Next 0 h: no charging or exporting
    // planned. Battery 35% now, lowest 60% at 19:00, highest 60% at 19:00."
    const now = T0 + 70 * MIN;
    const { rows } = buildRows(
      [
        slot(0, { loadActual: 0.5, pvActual: 0.2, socActual: 60 }),
        slot(30, { loadActual: 0.7, pvActual: 0.3, socActual: 58 }),
        slot(60, { socForecast: 60, socForecastEnd: 55 }),
      ],
      { now },
    );
    const wall = (ms: number) => new Date(ms).toISOString().slice(11, 16);
    const text = describeTimeline(rows, { now, wall, car: false, socNow: 35 });
    expect(text).toBe("Last 1 h: home used 1.2 kWh, solar made 0.5 kWh. Battery 35% now.");
    expect(text).not.toContain("Next 0 h");
    expect(text).not.toContain("lowest");
  });
  it("never names a past time for the lowest or highest level", () => {
    const now = T0 + 70 * MIN;
    const { rows } = buildRows(
      [
        slot(60, { socForecast: 90, socForecastEnd: 50 }),
        slot(90, { socForecast: 50 }),
        slot(120, { socForecast: 70 }),
        slot(150, { socForecast: 80 }),
      ],
      { now },
    );
    const wall = (ms: number) => new Date(ms).toISOString().slice(11, 16);
    const text = describeTimeline(rows, { now, wall, car: false, batteryOnly: true });
    // 90% was the in-progress slot's start, before now: not the highest "at 10:00".
    expect(text).toBe("Battery 90% now, lowest 50% at 10:30, highest 80% at 11:30.");
  });
  it("words an earlier plan's levels as what it expected", () => {
    const now = T0 + 60 * MIN;
    const { rows } = buildRows([slot(60, { socForecast: 40 }), slot(90, { socForecast: 80 })], { now });
    const wall = (ms: number) => new Date(ms).toISOString().slice(11, 16);
    expect(describeTimeline(rows, { now, wall, car: false, batteryOnly: true, asOfPlan: true })).toBe(
      "This plan expected 40% at 10:00, highest 80% at 10:30.",
    );
  });
  it("summarises what was measured, what is planned and where the battery goes", () => {
    const now = T0 + 60 * MIN;
    const { rows } = buildRows(
      [
        slot(0, { loadActual: 0.5, pvActual: 0.2 }),
        slot(30, { loadActual: 0.7, pvActual: 0.3 }),
        slot(60, { socForecast: 50 }),
        slot(90, { action: "charge", importRate: 6.67, socForecast: 20 }),
        slot(120, { action: "charge", importRate: 6.67, socForecast: 90 }),
      ],
      { now },
    );
    const wall = (ms: number) => new Date(ms).toISOString().slice(11, 16);
    expect(describeTimeline(rows, { now, wall, car: false })).toBe(
      "Last 1 h: home used 1.2 kWh, solar made 0.5 kWh. Next 2 h: Predbat plans to charge from the grid 10:30–11:30 at 6.67p. Battery 50% now, lowest 20% at 10:30, highest 90% at 11:00.",
    );
    // The day profile passes the period's own totals, so its caption matches the headline figure above it.
    expect(describeTimeline(rows, { now, wall, car: false, totals: { home: 23.74, solar: null } })).toMatch(
      /^Last 1 h: home used 23\.7 kWh\. /,
    );
  });
});
