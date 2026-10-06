import { describe, expect, it } from "vitest";
import { ago, clock, dayLabel, dayTime, isAmbiguousTime, localDate, range, stamp, when } from "./time";

const London = { timeZone: "Europe/London", now: "2026-10-05T09:20:00Z" };

describe("clock", () => {
  it("shows the household's local time, not UTC", () => {
    expect(clock("2026-10-05T09:20:00Z", London)).toBe("10:20");
    expect(clock("2026-10-05T09:20:00Z", { timeZone: "Pacific/Auckland" })).toBe("22:20");
  });
  it("names the zone in the hour that happens twice when the clocks go back", () => {
    // 25 Oct 2026: 01:30 BST (00:30Z) and 01:30 GMT (01:30Z).
    expect(isAmbiguousTime("2026-10-25T00:30:00Z", "Europe/London")).toBe(true);
    expect(clock("2026-10-25T00:30:00Z", London)).toBe("01:30 BST");
    expect(clock("2026-10-25T01:30:00Z", London)).toBe("01:30 GMT");
    expect(clock("2026-10-25T03:30:00Z", London)).toBe("03:30");
  });
});

describe("dayTime", () => {
  it("is relative within a day either side", () => {
    expect(dayTime("2026-10-05T09:20:00Z", London)).toBe("Today 10:20");
    expect(dayTime("2026-10-04T22:56:00Z", London)).toBe("Yesterday 23:56");
    expect(dayTime("2026-10-06T05:00:00Z", London)).toBe("Tomorrow 06:00");
  });
  it("is absolute beyond that, with the year only for other years", () => {
    expect(dayTime("2026-10-03T20:22:00Z", London)).toBe("Sat 3 Oct, 21:22");
    expect(dayTime("2025-10-04T20:22:00Z", London)).toBe("Sat 4 Oct 2025, 21:22");
  });
  it("writes September with three letters", () => {
    expect(dayTime("2026-09-25T11:00:00Z", London)).toBe("Fri 25 Sep, 12:00");
  });
  it("uses local calendar days across midnight", () => {
    // 23:30Z on 4 Oct is 00:30 BST on 5 Oct: today, not yesterday.
    expect(dayTime("2026-10-04T23:30:00Z", London)).toBe("Today 00:30");
  });
});

describe("range", () => {
  it("writes the date once with an en dash", () => {
    expect(range("2026-10-05T09:20:00Z", "2026-10-05T17:30:00Z", London)).toBe("Today 10:20–18:30");
    expect(range("2026-10-02T09:20:00Z", "2026-10-02T17:30:00Z", London)).toBe("Fri 2 Oct, 10:20–18:30");
  });
  it("spells out both ends across days", () => {
    expect(range("2026-10-05T22:30:00Z", "2026-10-06T00:30:00Z", London)).toBe("Today 23:30 – Tomorrow 01:30");
  });
  it("reads an end at midnight as 24:00", () => {
    expect(range("2026-10-05T21:00:00Z", "2026-10-05T23:00:00Z", London)).toBe("Today 22:00–24:00");
  });
});

describe("dayLabel and helpers", () => {
  it("labels days", () => {
    expect(dayLabel("2026-10-05T09:20:00Z", London)).toBe("Mon 5 Oct");
    expect(dayLabel("2026-10-05T09:20:00Z", { ...London, relative: true })).toBe("Today");
    expect(localDate("2026-10-04T23:30:00Z", London)).toBe("2026-10-05");
  });
  it("says how long ago", () => {
    expect(ago("2026-10-05T09:19:40Z", London)).toBe("just now");
    expect(ago("2026-10-05T09:16:00Z", London)).toBe("4 min ago");
    expect(ago("2026-10-05T06:10:00Z", London)).toBe("3 h ago");
    expect(ago("2026-10-03T20:22:00Z", London)).toBe("Sat 3 Oct, 21:22");
  });
  it("keeps tooltips absolute", () => {
    expect(when("2026-10-04T22:00:00Z", "Europe/London")).toBe("Sun 4 Oct, 23:00");
    expect(stamp("not a date")).toBe("");
  });
});
