import { describe, expect, it } from "vitest";
import { eveningBefore, plannedLevelAt } from "./useLastNight";
import type { Plan } from "../../types";

const TZ = "Europe/London";

describe("eveningBefore", () => {
  it("is 18:00 the same evening for a window starting at 23:30", () => {
    expect(new Date(eveningBefore(Date.parse("2026-10-04T22:30:00Z"), TZ)).toISOString()).toBe(
      "2026-10-04T17:00:00.000Z",
    );
  });
  it("is 18:00 the evening before for a window starting in the small hours", () => {
    expect(new Date(eveningBefore(Date.parse("2026-10-05T00:30:00Z"), TZ)).toISOString()).toBe(
      "2026-10-04T17:00:00.000Z",
    );
  });
  it("follows the clocks going back (18:00 GMT after the change)", () => {
    expect(new Date(eveningBefore(Date.parse("2026-10-26T00:30:00Z"), TZ)).toISOString()).toBe(
      "2026-10-25T18:00:00.000Z",
    );
  });
});

describe("plannedLevelAt", () => {
  const slot = (time: string, socForecast: number, socForecastEnd?: number) =>
    ({ time, durationMinutes: 30, socForecast, socForecastEnd }) as unknown as Plan["slots"][number];
  const plan = {
    id: "p",
    at: "",
    source: "Predbat",
    slots: [slot("2026-10-05T04:00:00Z", 70, 85), slot("2026-10-05T04:30:00Z", 85)],
  } as Plan;
  it("reads the end level of the slot ending then, or the start of the next", () => {
    expect(plannedLevelAt(plan, Date.parse("2026-10-05T04:30:00Z"))).toBe(85);
    expect(plannedLevelAt(plan, Date.parse("2026-10-05T04:00:00Z"))).toBe(70);
    expect(plannedLevelAt(plan, Date.parse("2026-10-05T09:00:00Z"))).toBeNull();
  });
});
