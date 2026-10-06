import { beforeAll, describe, expect, it } from "vitest";
import { plainText, textParts } from "./sanitize";
import { setHouseholdTimeZone } from "./time";

beforeAll(() => setHouseholdTimeZone("Europe/London"));

describe("plainText", () => {
  it("replaces Predbat plan codes with their labels", () => {
    expect(plainText("Predbat sat in FrzExp overnight")).toBe(
      "Predbat sat in Export solar, don't charge battery overnight",
    );
    expect(plainText("then HoldChrg until 05:30")).toBe("then Hold at charge target until 05:30");
    expect(plainText("FrzChg and FrzChrg")).toBe("Hold battery level and Hold battery level");
    expect(plainText("Chrg 70% from 23:30")).toBe("Charge from the grid (to 70%) from 23:30");
  });
  it("leaves ordinary words alone", () => {
    expect(plainText("Expected export was low; the Exponent stayed")).toBe(
      "Expected export was low; the Exponent stayed",
    );
    expect(plainText("sensor.predbat_charge_limit")).toBe("sensor.predbat_charge_limit");
  });
  it("renames the unknown-state prefix", () => {
    expect(plainText("Predbat state Mystery")).toBe("Other Predbat state (Mystery)");
  });
  it("shows UTC timestamps in household time", () => {
    const out = plainText("Closed at 2026-10-04 12:50:27Z because it ran at 2026-10-04T23:30:00Z.");
    expect(out).not.toMatch(/Z\b|UTC|T23/);
    expect(out).toMatch(/13:50/);
    expect(out).toMatch(/00:30/);
    expect(plainText("brief sent 2026-10-05 04:16 UTC")).toMatch(/05:16/);
  });
  it("fixes the brand and the jargon", () => {
    expect(plainText("ChatGpt read the telemetry")).toBe("ChatGPT read the data");
    expect(plainText("Telemetry shows a gap")).toBe("Data shows a gap");
    // The substitute keeps the grammar of the sentence it lands in.
    expect(plainText("solar telemetry remains unavailable")).toBe("solar data remains unavailable");
  });
});

describe("textParts", () => {
  it("turns backticked identifiers and entity ids into code parts and drops other backticks", () => {
    expect(textParts("Set `load_scaling` and check sensor.solar_today, not `the evening`.")).toEqual([
      { kind: "text", value: "Set " },
      { kind: "code", value: "load_scaling" },
      { kind: "text", value: " and check " },
      { kind: "code", value: "sensor.solar_today" },
      { kind: "text", value: ", not the evening." },
    ]);
  });
  it("labels a backticked plan code instead of showing it as code", () => {
    expect(textParts("in `FrzExp`")).toEqual([{ kind: "text", value: "in Export solar, don't charge battery" }]);
  });
});
