import { describe, expect, it } from "vitest";
import { compact, count, coverageText, gbp, kw, kwh, MINUS, number, pence, percent, usd } from "./format";

describe("kwh", () => {
  it("uses 1 dp in tiles and 2 dp in tables", () => {
    expect(kwh(12.345)).toBe("12.3 kWh");
    expect(kwh(12.345, { precision: "table" })).toBe("12.35 kWh");
    expect(kwh(12.25, { precision: "tile" })).toBe("12.3 kWh");
  });
  it("groups thousands", () => {
    expect(kwh(94317.69, { precision: "table" })).toBe("94,317.69 kWh");
  });
  it("uses a real minus sign and never shows −0.0", () => {
    expect(kwh(-1.25)).toBe(`${MINUS}1.3 kWh`);
    expect(kwh(-0.01)).toBe("0.0 kWh");
  });
  it("can leave the unit off and sign positive values", () => {
    expect(kwh(3, { unit: false })).toBe("3.0");
    expect(kwh(3, { signed: true })).toBe("+3.0 kWh");
  });
  it("says why a figure is missing", () => {
    expect(kwh(null)).toBe("—");
    expect(kwh(undefined, { missing: "sensor-offline" })).toBe("Sensor offline");
    expect(kwh(Number.NaN, { missing: "not-measured" })).toBe("Not measured");
  });
});

describe("kw", () => {
  it("rounds to 1 dp but keeps small draws visible", () => {
    expect(kw(3.84)).toBe("3.8 kW");
    expect(kw(0.054)).toBe("0.05 kW");
    expect(kw(0)).toBe("0.0 kW");
  });
});

describe("gbp", () => {
  it("formats pounds with a real minus sign", () => {
    expect(gbp(1.1)).toBe("£1.10");
    expect(gbp(-0.25)).toBe(`${MINUS}£0.25`);
    expect(gbp(1234.5)).toBe("£1,234.50");
  });
  it("signs positive values on request and leaves zero unsigned", () => {
    expect(gbp(0.25, { signed: true })).toBe("+£0.25");
    expect(gbp(-0.001, { signed: true })).toBe("£0.00");
  });
  it("names unpriced values", () => {
    expect(gbp(null, { missing: "not-priced" })).toBe("Not priced");
  });
});

describe("pence", () => {
  it("shows at most 2 dp and trims trailing zeros", () => {
    expect(pence(24.123456)).toBe("24.12p/kWh");
    expect(pence(15.0049)).toBe("15p/kWh");
    expect(pence(31.7296)).toBe("31.73p/kWh");
    expect(pence(6.7)).toBe("6.7p/kWh");
  });
  it("handles negative prices and the short unit", () => {
    expect(pence(-2.5)).toBe(`${MINUS}2.5p/kWh`);
    expect(pence(26.23, { unit: "p" })).toBe("26.23p");
  });
});

describe("percent", () => {
  it("formats values and fractions", () => {
    expect(percent(74.05)).toBe("74%");
    expect(percent(0.893, { fraction: true })).toBe("89%");
    expect(percent(0.9999, { fraction: true, round: "floor" })).toBe("99%");
    expect(percent(12.345, { dp: 1 })).toBe("12.3%");
  });
});

describe("usd", () => {
  it("shows small amounts to 4 dp and larger ones to 2", () => {
    expect(usd(0.01234)).toBe("$0.0123");
    expect(usd(0.5)).toBe("$0.50");
    expect(usd(3.5)).toBe("$3.50");
    expect(usd(0)).toBe("$0.00");
  });
});

describe("counts", () => {
  it("groups and shortens", () => {
    expect(count(1234567)).toBe("1,234,567");
    expect(compact(1_940_000)).toBe("1.9M");
    expect(compact(12_400)).toBe("12.4k");
    expect(compact(950)).toBe("950");
    expect(compact(2_000_000)).toBe("2M");
    expect(number(94317.687, 1)).toBe("94,317.7");
  });
});

describe("coverageText", () => {
  it("never rounds a gap up to fully measured", () => {
    expect(coverageText(1)).toBe("Fully measured");
    expect(coverageText(0.9999)).toBe("99% measured");
    expect(coverageText(0.001)).toBe("1% measured");
    expect(coverageText(0)).toBe("No readings");
    expect(coverageText(undefined)).toBe("No readings");
  });
});

describe("snapshot of every formatter", () => {
  it("is stable", () => {
    const values = [null, 0, 0.004, 1.005, -2.5, 12.345, 94317.69];
    expect(
      values.map((v) => ({
        v,
        kwh: kwh(v),
        kwhTable: kwh(v, { precision: "table" }),
        kw: kw(v),
        gbp: gbp(v),
        pence: pence(v),
        percent: percent(v),
        usd: usd(v),
      })),
    ).toMatchSnapshot();
  });
});
