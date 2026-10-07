import { describe, expect, it } from "vitest";
import type { StandingChargeSettings } from "../../completion-types";
import { rateChanges, standingChargeSource } from "./StandingChargeSetting";

const base: StandingChargeSettings = {
  includeInNet: true,
  manualPencePerDay: null,
  entity: null,
  entityOrigin: null,
  sensorPencePerDay: null,
  sensorAt: null,
  sensorStatus: null,
  todayPencePerDay: null,
  todaySource: null,
  recent: [],
};
const row = (day: string, pencePerDay: number, source = "sensor") => ({
  day,
  pencePerDay,
  source,
  entityId: null,
  recordedAt: `${day}T12:00:00Z`,
});

describe("standingChargeSource", () => {
  it("says where today's figure comes from, in pence and pounds", () => {
    expect(
      standingChargeSource({
        ...base,
        entity: "sensor.octopus_energy_electricity_x_y_current_standing_charge",
        entityOrigin: "octopus",
        todayPencePerDay: 53.68,
        todaySource: "sensor",
      }),
    ).toBe(
      "53.68p a day (£0.54), read from Home Assistant (the Octopus Energy sensor on the same meter as your import rate).",
    );
    expect(standingChargeSource({ ...base, todayPencePerDay: 61, todaySource: "manual", manualPencePerDay: 61 })).toBe(
      "61p a day (£0.61), the figure you entered.",
    );
    expect(standingChargeSource(base)).toMatch(/^Joule doesn’t know it yet\./);
  });
});

describe("rateChanges", () => {
  it("lists each change once, newest first", () => {
    const recent = [
      row("2026-10-07", 55),
      row("2026-10-06", 55),
      row("2026-10-05", 50, "manual"),
      row("2026-10-04", 50),
    ];
    expect(rateChanges(recent)).toEqual([
      { from: "2026-10-06", pence: 55, source: "sensor" },
      { from: "2026-10-04", pence: 50, source: "sensor" },
    ]);
  });
});
