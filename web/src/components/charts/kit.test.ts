import { describe, expect, it } from "vitest";
import { divergingTicks, fittedTicks, linear, niceStep, niceTicks, percentile, perHalfHour, tickText } from "./scale";
import { midnights, slotLabel, timeTicks, wallOf } from "./timeTicks";
import { barPath, linePath, stepArea, stepPath } from "./paths";

const London = "Europe/London";
const at = (s: string) => Date.parse(s);

describe("nice ticks", () => {
  it("rounds the top to a clean step above the peak", () => {
    // The live 2.94 kWh peak used to sit at the ceiling of a 0, 0.75, 1.5, 2.25, 3 axis.
    expect(niceTicks(2.94)).toEqual({ max: 4, ticks: [0, 1, 2, 3, 4], step: 1 });
    expect(niceTicks(0.9).ticks).toEqual([0, 0.25, 0.5, 0.75, 1]);
    expect(niceTicks(0).max).toBeGreaterThan(0);
    expect(niceTicks(0, 4, 1).max).toBe(1);
  });
  it("chooses 1, 2, 2.5 or 5 steps", () => {
    expect(niceStep(10, 4)).toBe(2.5);
    expect(niceStep(7, 4)).toBe(2);
    expect(niceStep(30, 4)).toBe(10);
  });
  it("fits a small chart's scale to its data, so a small side takes one step", () => {
    const t = fittedTicks(-0.3, 5.2, 4);
    expect(t.ticks).toContain(0);
    expect(t.min).toBe(-t.step);
    expect(t.max).toBeGreaterThanOrEqual(5.2);
    expect(t.max).toBeLessThan(8);
  });
  it("brackets zero for values on both sides", () => {
    const t = divergingTicks(-12, 30);
    expect(t.min).toBeLessThan(-12);
    expect(t.max).toBeGreaterThan(30);
    expect(t.ticks).toContain(0);
  });
  it("prints ticks without trailing noise and with a real minus", () => {
    expect(tickText(-0.5, 0.5)).toBe("−0.5");
    expect(tickText(2, 1)).toBe("2");
    expect(tickText(0.25, 0.25)).toBe("0.25");
    expect(tickText(0.5, 0.25)).toBe("0.50");
    expect(tickText(0.5, 0.5)).toBe("0.5");
    expect(tickText(0.05, 0.05)).toBe("0.05");
    // The zero line reads "0", not "0.00".
    expect(tickText(0, 0.05)).toBe("0");
  });
  it("maps linearly both ways", () => {
    const x = linear([0, 10], [0, 100]);
    expect(x(5)).toBe(50);
    expect(x.invert(25)).toBe(2.5);
  });
});

describe("duration normalisation", () => {
  it("scales a partial slot to kWh per half-hour", () => {
    // Plan made at 10:25: a 5-minute 0.06 kWh slot is 0.36 kWh per half-hour, not a dip to 0.06.
    expect(perHalfHour(0.06, 5)).toBeCloseTo(0.36);
    expect(perHalfHour(0.4, 30)).toBe(0.4);
    expect(perHalfHour(1, 60)).toBe(0.5);
  });
  it("keeps a missing value missing", () => {
    expect(perHalfHour(null, 30)).toBeNull();
    expect(perHalfHour(1, 0)).toBeNull();
  });
  it("takes percentiles for the sparkline cap", () => {
    expect(percentile([1, 2, 3, 4, 100], 50)).toBe(3);
    expect(percentile([], 98)).toBeNull();
  });
});

describe("time ticks", () => {
  it("lands on clean local hours and dates the first tick and each midnight", () => {
    const ticks = timeTicks(at("2026-10-04T22:00:00Z"), at("2026-10-05T22:00:00Z"), London, 8);
    expect(ticks.map((t) => t.time)).toEqual(["00:00", "03:00", "06:00", "09:00", "12:00", "15:00", "18:00", "21:00"]);
    expect(ticks[0].day).toBe("Mon 5 Oct");
    expect(ticks.slice(1).every((t) => !t.day)).toBe(true);
  });
  it("dates a first tick that is itself midnight even when the next is not", () => {
    const ticks = timeTicks(at("2026-10-04T22:30:00Z"), at("2026-10-05T10:00:00Z"), London, 3);
    expect(ticks[0]).toMatchObject({ time: "00:00", day: "Mon 5 Oct", midnight: true });
  });
  it("leaves the first tick undated when the second already carries the date", () => {
    const ticks = timeTicks(at("2026-10-04T20:00:00Z"), at("2026-10-05T04:00:00Z"), London, 3);
    expect(ticks.map((t) => [t.time, t.day ?? ""])).toEqual([
      ["21:00", ""],
      ["00:00", "Mon 5 Oct"],
      ["03:00", ""],
    ]);
  });
  it("counts hours by the local clock across the clocks going back", () => {
    // Sun 25 Oct 2026 has 25 hours; 6-hourly ticks stay on 00, 06, 12, 18 local.
    const ticks = timeTicks(at("2026-10-24T23:00:00Z"), at("2026-10-26T00:00:00Z"), London, 5);
    expect(ticks.map((t) => t.time)).toEqual(["00:00", "06:00", "12:00", "18:00", "00:00"]);
    expect(new Date(ticks[1].t).toISOString()).toBe("2026-10-25T06:00:00.000Z");
  });
  it("finds local midnights and labels slots", () => {
    expect(
      midnights(at("2026-10-04T12:00:00Z"), at("2026-10-05T12:00:00Z"), London).map((t) => new Date(t).toISOString()),
    ).toEqual(["2026-10-04T23:00:00.000Z"]);
    expect(slotLabel(at("2026-10-05T22:30:00Z"), at("2026-10-05T23:00:00Z"), London)).toBe("Mon 5 Oct · 23:30–00:00");
    expect(wallOf(at("2026-09-05T12:00:00Z"), London).day).toBe("Sat 5 Sep");
  });
});

describe("paths", () => {
  it("draws touching slots as one stepped run and breaks at a missing slot", () => {
    expect(stepPath([{ x0: 0, x1: 10, y: 5 }, { x0: 10, x1: 20, y: 8 }, null, { x0: 30, x1: 40, y: 2 }])).toBe(
      "M0,5 H10 V8 H20 M30,2 H40",
    );
  });
  it("breaks a run where slots do not touch", () => {
    expect(
      stepPath([
        { x0: 0, x1: 10, y: 5 },
        { x0: 15, x1: 20, y: 5 },
      ]),
    ).toBe("M0,5 H10 M15,5 H20");
  });
  it("closes step areas to the baseline", () => {
    expect(stepArea([{ x0: 0, x1: 10, y: 5 }, null], 50)).toBe("M0,50 V5 H10 V50 Z");
  });
  it("draws lines broken at nulls", () => {
    expect(linePath([{ x: 0, y: 1 }, { x: 1, y: 2 }, null, { x: 3, y: 4 }])).toBe("M0,1 L1,2 M3,4");
  });
  it("rounds only the outer end of a bar", () => {
    expect(barPath(0, 10, 100, 50)).toMatch(/^M0,100 V54 Q0,50 4,50/);
    expect(barPath(0, 10, 100, 140)).toMatch(/^M0,100 V136 Q0,140 4,140/);
    expect(barPath(0, 10, 100, 100)).toBe("");
  });
});
