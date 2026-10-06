import { describe, expect, it } from "vitest";
import {
  clippedNote,
  customRangeError,
  periodFromQuery,
  periodQuery,
  presetDays,
  previousOf,
  resolvePeriod,
} from "./period";

const tz = "Europe/London";
const now = new Date("2026-10-05T09:55:00Z");
const first = "2026-10-02T13:59:17Z";

describe("presets", () => {
  it("cover whole local days ending today", () => {
    expect(presetDays("today", "2026-10-05")).toEqual({ fromDay: "2026-10-05", toDay: "2026-10-05" });
    expect(presetDays("yesterday", "2026-10-05")).toEqual({ fromDay: "2026-10-04", toDay: "2026-10-04" });
    expect(presetDays("7d", "2026-10-05")).toEqual({ fromDay: "2026-09-29", toDay: "2026-10-05" });
    expect(presetDays("30d", "2026-10-05")).toEqual({ fromDay: "2026-09-06", toDay: "2026-10-05" });
  });
  it("round-trip through the address bar, with 7 days as the default", () => {
    expect(periodFromQuery(new URLSearchParams(""))).toEqual({ preset: "7d" });
    expect(periodFromQuery(new URLSearchParams("period=today"))).toEqual({ preset: "today" });
    expect(periodFromQuery(new URLSearchParams("from=2026-10-01&to=2026-10-04"))).toEqual({
      preset: "custom",
      fromDay: "2026-10-01",
      toDay: "2026-10-04",
    });
    expect(periodQuery("7d").toString()).toBe("");
    expect(periodQuery("yesterday").toString()).toBe("period=yesterday");
    expect(periodQuery("custom", "2026-10-01", "2026-10-04").toString()).toBe("from=2026-10-01&to=2026-10-04");
  });
});

describe("customRangeError", () => {
  it("gives one specific message", () => {
    expect(customRangeError("2026-10-06", "2026-10-08", "2026-10-05")).toBe("That's in the future: today is 5 Oct.");
    expect(customRangeError("2026-10-04", "2026-10-02", "2026-10-05")).toBe("The start date is after the end date.");
    expect(customRangeError("2026-10-01", "2026-10-04", "2026-10-05")).toBeNull();
  });
});

describe("resolvePeriod", () => {
  it("starts at the day records began and runs up to now", () => {
    const p = resolvePeriod("7d", "2026-09-29", "2026-10-05", tz, now, first);
    expect(p.from.toISOString()).toBe("2026-10-01T23:00:00.000Z");
    expect(p.to.toISOString()).toBe(now.toISOString());
    expect(p).toMatchObject({ clampedFrom: "2026-10-02", days: 4, includesToday: true, beforeRecords: false });
  });
  it("ends at the last reading, not the clock, when it runs up to now", () => {
    const p = resolvePeriod("today", "2026-10-05", "2026-10-05", tz, now, first, "2026-10-05T09:51:30Z");
    expect(p.to.toISOString()).toBe("2026-10-05T09:51:30.000Z");
    expect(p.includesToday).toBe(true);
    // A whole past day is never cut short by the last reading.
    const y = resolvePeriod("yesterday", "2026-10-04", "2026-10-04", tz, now, first, "2026-10-05T09:51:30Z");
    expect(y.to.toISOString()).toBe("2026-10-04T23:00:00.000Z");
  });
  it("explains a period cut short by the day records began", () => {
    const p = resolvePeriod("30d", "2026-09-06", "2026-10-05", tz, now, first);
    expect(clippedNote(p, tz)).toBe("Only 4 days recorded so far (since Fri 2 Oct)");
    expect(clippedNote(resolvePeriod("today", "2026-10-05", "2026-10-05", tz, now, first), tz)).toBeNull();
  });
  it("knows when every day asked for is before records began", () => {
    expect(resolvePeriod("custom", "2026-09-01", "2026-09-24", tz, now, first).beforeRecords).toBe(true);
  });
});

describe("previousOf", () => {
  it("compares today so far with the same stretch of yesterday", () => {
    const p = resolvePeriod("today", "2026-10-05", "2026-10-05", tz, now, first);
    const prev = previousOf(p, tz, first)!;
    expect(prev.from.toISOString()).toBe("2026-10-03T23:00:00.000Z");
    expect(prev.to.toISOString()).toBe("2026-10-04T09:55:00.000Z");
    expect(prev.label).toBe("this time yesterday");
  });
  it("compares a whole day with the day before", () => {
    const p = resolvePeriod("yesterday", "2026-10-04", "2026-10-04", tz, now, first);
    expect(previousOf(p, tz, first)).toMatchObject({ label: "the day before" });
  });
  it("has nothing to compare with before records began", () => {
    const p = resolvePeriod("yesterday", "2026-10-03", "2026-10-03", tz, now, first);
    expect(previousOf(p, tz, first)).toBeNull();
    expect(previousOf(resolvePeriod("7d", "2026-09-29", "2026-10-05", tz, now, first), tz, first)).toBeNull();
  });
});
