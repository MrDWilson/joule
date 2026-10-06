import { beforeAll, describe, expect, it } from "vitest";
import { richParts, richToText, type RichPart } from "./richText";
import { nameBook } from "./insights";
import { setHouseholdTimeZone } from "./time";
import type { Setting } from "../types";

beforeAll(() => setHouseholdTimeZone("Europe/London"));

const kinds = (parts: RichPart[]) => parts.map((p) => p.kind);
const book = nameBook(
  [{ key: "load_scaling", name: "House load scaling", entityId: "input_number.predbat_load_scaling" } as Setting],
  { pv: "sensor.my_home_solar_generated" },
  (m) => (m === "pv" ? "Solar" : m),
);

describe("richParts", () => {
  it("renders Predbat codes as plain terms that keep the code in their tooltip", () => {
    const parts = richParts("During the next FrzExp slot the battery held.");
    const term = parts.find((p) => p.kind === "term");
    expect(term).toMatchObject({ value: "Export solar, don't charge battery", code: "FrzExp" });
    expect(term && term.kind === "term" && term.title).toMatch(/^Predbat: FrzExp\. The battery won't charge/);
    expect(richToText(parts)).toBe("During the next Export solar, don't charge battery slot the battery held.");
  });
  it("catches the AI's misspellings and targets", () => {
    expect(richToText(richParts("FrzChg then Chrg 70% at 23:30"))).toBe(
      "Hold battery level then Charge from the grid (to 70%) at 23:30",
    );
  });
  it("names Demand slots, SoC and PV in plain words, capitalised at a sentence start", () => {
    const text = richToText(richParts("The 09:00 Demand slots ended at 66%. SoC followed PV of 0.86 kWh."));
    expect(text).toBe("The 09:00 Power your home slots ended at 66%. Battery level followed solar of 0.86 kWh.");
    expect(richToText(richParts("Demand is high tonight; Expected export"))).toBe(
      "Demand is high tonight; Expected export",
    );
    expect(richToText(richParts("The 18:00 slot still showed Demand at 31.73p/kWh, then went to Demand."))).toBe(
      "The 18:00 slot still showed Power your home at 31.73p/kWh, then went to Power your home.",
    );
    expect(richToText(richParts("Peak demand in Demand is rare; respond to Demand response events."))).toBe(
      "Peak demand in Demand is rare; respond to Demand response events.",
    );
  });
  it("turns backticks into code and **bold** into strong text", () => {
    const parts = richParts("**Approve** the pending `combine_charge_slots=on` change");
    expect(kinds(parts)).toEqual(["strong", "text", "code", "text"]);
    expect(parts[2]).toEqual({ kind: "code", value: "combine_charge_slots=on" });
    expect(richToText(parts)).not.toContain("`");
    expect(richToText(parts)).not.toContain("**");
  });
  it("shows known entity ids and setting keys by their friendly names", () => {
    const parts = richParts(
      "Restore `sensor.my_home_solar_generated`, then lower load_scaling; pv_today failed.",
      book,
    );
    expect(parts.filter((p) => p.kind === "name")).toEqual([
      { kind: "name", value: "Solar sensor", id: "sensor.my_home_solar_generated" },
      { kind: "name", value: "House load scaling", id: "load_scaling" },
    ]);
    expect(parts.find((p) => p.kind === "code")).toEqual({ kind: "code", value: "pv_today" });
  });
  it("leaves ordinary words and markup alone", () => {
    expect(richToText(richParts("Expected export was low; the Exponent stayed <img src=x>"))).toBe(
      "Expected export was low; the Exponent stayed <img src=x>",
    );
  });
  it("still converts UTC timestamps", () => {
    expect(richToText(richParts("Closed at 2026-10-04 12:50:27Z."))).not.toMatch(/Z\b/);
  });
});
