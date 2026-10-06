import { describe, expect, it } from "vitest";
import { halfHourBins, peakBin, seriesBins, type MeterInterval } from "./bins";

const T0 = Date.parse("2026-10-04T23:00:00Z"); // 00:00 BST
const MIN = 60000;
const iv = (fromMin: number, toMin: number, averageKw: number | null, status = "observed"): MeterInterval => ({
  start: new Date(T0 + fromMin * MIN).toISOString(),
  end: new Date(T0 + toMin * MIN).toISOString(),
  averageKw,
  status,
});

describe("halfHourBins", () => {
  it("averages a skipped-update zig-zag into the real half-hour power", () => {
    // The live pattern: 0, 15.1, 7.6, 7.6, 0, 15.3 kW per 5-minute poll is about 7.6 kW over the half-hour.
    const zigzag = [0, 15.1, 7.6, 7.6, 0, 15.3].map((kw, i) => iv(i * 5, (i + 1) * 5, kw));
    const [bin] = halfHourBins(zigzag, T0, T0 + 30 * MIN, T0 + 60 * MIN);
    expect(bin.kw).toBeCloseTo(7.6, 1);
    expect(peakBin([bin])!.kw).toBeLessThan(8);
  });
  it("leaves a half-hour with under half measured empty, and marks it missing once over", () => {
    const bins = halfHourBins([iv(0, 10, 2)], T0, T0 + 60 * MIN, T0 + 90 * MIN);
    expect(bins.map((b) => b.state)).toEqual(["missing", "missing"]);
  });
  it("counts idle sensors as zero and spread intervals as approximate", () => {
    const bins = halfHourBins([iv(0, 30, null, "idle"), iv(30, 60, 1, "spread")], T0, T0 + 60 * MIN, T0 + 90 * MIN);
    expect(bins.map((b) => [b.kw, b.approx])).toEqual([
      [0, false],
      [1, true],
    ]);
    // A ≈ half-hour's timing is estimated, so it never sets the quoted peak.
    expect(peakBin(bins)!.kw).toBe(0);
  });
  it("ignores reset and invalid intervals", () => {
    const bins = halfHourBins([iv(0, 30, 9, "reset"), iv(0, 30, 9, "invalid")], T0, T0 + 30 * MIN, T0 + 60 * MIN);
    expect(bins[0].state).toBe("missing");
  });
  it("shows the half-hour in progress from its elapsed part and nothing after now", () => {
    const bins = halfHourBins([iv(0, 10, 3)], T0, T0 + 90 * MIN, T0 + 15 * MIN);
    expect(bins.map((b) => b.state)).toEqual(["value", "none", "none"]);
    expect(bins[0].kw).toBe(3);
  });
});

describe("seriesBins", () => {
  // Live 5 Oct: the load meter read 7.4 kW at 00:00 while the car charged; home without the car was 0.29 kW.
  const home = {
    start: new Date(T0).toISOString(),
    stepMinutes: 30,
    kw: [0.29, 0.568, null, 0, null],
    status: ["measured", "estimated", "missing", "idle", "pending"],
  };
  it("reads the server's home series: values, ≈ estimates, gaps and the pending half-hour", () => {
    const bins = seriesBins(home, T0, T0 + 150 * MIN, T0 + 140 * MIN)!;
    expect(bins.map((b) => b.state)).toEqual(["value", "value", "missing", "value", "none"]);
    expect(bins.map((b) => b.kw)).toEqual([0.29, 0.568, null, 0, null]);
    expect(bins[1].approx).toBe(true);
    expect(peakBin(bins)!.kw).toBe(0.29);
  });
  it("declines a series that is not on a half-hour grid", () => {
    expect(seriesBins({ ...home, stepMinutes: 15 }, T0, T0 + 60 * MIN, T0 + 60 * MIN)).toBeNull();
  });
});
