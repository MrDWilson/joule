import { describe, expect, it } from "vitest";
import { instantOfWall, previousPeriod, shiftLocalDays, todaySoFar, zonedDay } from "./comparison";

const London = "Europe/London";
const iso = (ms: number) => new Date(ms).toISOString();

describe("shiftLocalDays", () => {
  it("is exactly 24 hours on an ordinary day", () => {
    expect(iso(shiftLocalDays(Date.parse("2026-10-05T09:30:00Z"), -1, London))).toBe("2026-10-04T09:30:00.000Z");
  });
  it("keeps the local clock time across the clocks going back (Sun 25 Oct 2026)", () => {
    // 10:30 GMT on the 25th is 10:30 BST on the 24th: 25 hours earlier, not 24.
    expect(iso(shiftLocalDays(Date.parse("2026-10-25T10:30:00Z"), -1, London))).toBe("2026-10-24T09:30:00.000Z");
    // And the day after the change: 10:30 GMT on the 26th is 10:30 GMT on the 25th.
    expect(iso(shiftLocalDays(Date.parse("2026-10-26T10:30:00Z"), -1, London))).toBe("2026-10-25T10:30:00.000Z");
    // A week back from Mon 26 Oct lands in BST.
    expect(iso(shiftLocalDays(Date.parse("2026-10-26T10:30:00Z"), -7, London))).toBe("2026-10-19T09:30:00.000Z");
  });
  it("maps the repeated hour to its first occurrence", () => {
    // 01:30 on Mon 26 Oct (GMT) back a day is 01:30 on the 25th, which happens twice: the BST one comes first.
    expect(iso(shiftLocalDays(Date.parse("2026-10-26T01:30:00Z"), -1, London))).toBe("2026-10-25T00:30:00.000Z");
  });
  it("moves forward across a change too", () => {
    expect(iso(shiftLocalDays(Date.parse("2026-10-24T09:30:00Z"), 1, London))).toBe("2026-10-25T10:30:00.000Z");
  });
  it("resolves a skipped time (clocks going forward) past the jump", () => {
    // 01:30 does not exist on Sun 29 Mar 2026 in London.
    expect(iso(instantOfWall(Date.UTC(2026, 2, 29, 1, 30), London))).toBe("2026-03-29T01:30:00.000Z");
  });
  it("leaves zero shifts and invalid instants alone", () => {
    expect(shiftLocalDays(123, 0, London)).toBe(123);
    expect(shiftLocalDays(NaN, -1, London)).toBeNaN();
  });
});

describe("previousPeriod", () => {
  const window = (now: string) => {
    const { params, earlier } = todaySoFar(new Date(now), London);
    return {
      from: params.get("from"),
      to: params.get("to"),
      prevFrom: earlier!.params.get("from"),
      prevTo: earlier!.params.get("to"),
      label: earlier!.label,
    };
  };
  it("compares today so far with yesterday up to the same clock time", () => {
    expect(window("2026-10-05T09:30:00Z")).toEqual({
      from: "2026-10-04T23:00:00.000Z",
      to: "2026-10-05T09:30:00.000Z",
      prevFrom: "2026-10-03T23:00:00.000Z",
      prevTo: "2026-10-04T09:30:00.000Z",
      label: "this time yesterday",
    });
  });
  it("is right on the day the clocks go back (25 Oct 2026)", () => {
    // 10:30 GMT: 11.5 hours since midnight BST. Yesterday's window must end at 10:30 BST (09:30Z), not 11:30 BST.
    const w = window("2026-10-25T10:30:00Z");
    expect(w.from).toBe("2026-10-24T23:00:00.000Z");
    expect(w.prevFrom).toBe("2026-10-23T23:00:00.000Z");
    expect(w.prevTo).toBe("2026-10-24T09:30:00.000Z");
  });
  it("is right on the day after the clocks go back (26 Oct 2026)", () => {
    // Today starts at 00:00 GMT; yesterday (the 25-hour day) started at 00:00 BST and its window ends at 10:30 GMT.
    const w = window("2026-10-26T10:30:00Z");
    expect(w.from).toBe("2026-10-26T00:00:00.000Z");
    expect(w.prevFrom).toBe("2026-10-24T23:00:00.000Z");
    expect(w.prevTo).toBe("2026-10-25T10:30:00.000Z");
  });
  it("uses the whole previous days for complete periods", () => {
    const start = new Date("2026-10-01T23:00:00Z"),
      end = new Date("2026-10-04T23:00:00Z");
    const p = previousPeriod("2026-10-02", "2026-10-04", "2026-10-05", start, end, end, London)!;
    expect(p.params.get("from")).toBe("2026-09-28T23:00:00.000Z");
    expect(p.params.get("to")).toBe(start.toISOString());
    expect(p.label).toBe("the previous 3 days");
  });
  it("offers nothing for periods longer than a week", () => {
    const start = new Date("2026-09-01T23:00:00Z");
    expect(previousPeriod("2026-09-02", "2026-09-20", "2026-10-05", start, start, start, London)).toBeNull();
  });
  it("names local calendar days", () => {
    expect(zonedDay(new Date("2026-10-04T23:30:00Z"), London)).toBe("2026-10-05");
  });
});
