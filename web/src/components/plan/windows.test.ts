import { describe, expect, it } from "vitest";
import live from "./fixtures/live-plan-2026-10-05.json";
import {
  dayWord,
  planSentence,
  planWindows,
  priceRange,
  shortAction,
  shownCost,
  tradeNote,
  windowWhen,
  type PlanSlotLike,
} from "./windows";
import { batteryFlowText, moneyText, moneyWords, socNowText, windowNotes } from "./WindowList";
import { gbp } from "../../lib/format";
import { averagePrice, costFromNow, headlinePrice, priceBand, sellBand } from "./WhatsNext";
import { slotAction } from "../../lib/planActions";

const TZ = "Europe/London";
const MIN = 60000;
const T0 = Date.parse("2026-10-05T22:30:00Z"); // 23:30 BST
const slot = (minutes: number, extra: Partial<PlanSlotLike> = {}): PlanSlotLike => ({
  time: new Date(T0 + minutes * MIN).toISOString(),
  durationMinutes: 30,
  socForecast: 50,
  importRate: 20,
  exportRate: 12,
  action: "demand",
  cost: 0,
  ...extra,
});
const liveSlots = live.slots as PlanSlotLike[];
const liveNow = Date.parse("2026-10-05T15:54:00Z");

describe("planWindows", () => {
  it("keeps one export run as one window when the export price changes every half-hour", () => {
    // The 5 Oct audit plan: 23:30–01:00 export at 12.99p, 12.32p and 12.04p read as three rows before.
    const slots = [
      slot(0, { action: "export", exportRate: 12.99, socForecast: 40, socForecastEnd: 33 }),
      slot(30, { action: "export", exportRate: 12.32, socForecast: 33, socForecastEnd: 26 }),
      slot(60, { action: "export", exportRate: 12.04, socForecast: 26, socForecastEnd: 20 }),
      slot(90, { action: "demand", socForecast: 20 }),
    ];
    const windows = planWindows(slots, { now: T0 - 3600000 });
    expect(windows).toHaveLength(2);
    expect(windows[0].label).toBe("Export battery to the grid");
    expect(windowWhen(windows[0].start, windows[0].end, T0 - 3600000, TZ)).toBe("Tonight 23:30–01:00");
    expect(priceRange(windows[0].price)).toBe("12.0–13.0p");
    expect(windows[0].soc).toEqual({ start: 40, end: 20 });
  });

  it("merges charges with different targets and reads the last target", () => {
    const windows = planWindows(
      [
        slot(0, { action: "charge", actionKey: "charge", targetPercent: 37, importRate: 6.67 }),
        slot(30, { action: "charge", actionKey: "charge", targetPercent: 100, importRate: 6.67 }),
      ],
      { now: T0 },
    );
    expect(windows).toHaveLength(1);
    expect(windows[0].short).toBe("Charge → 100%");
    expect(windows[0].phase).toBe("current");
  });

  it("folds overnight sell-and-rebuy exports into the charge window and explains them", () => {
    const windows = planWindows(liveSlots, { now: liveNow, to: liveNow + 12 * 3600000, reserve: 4 });
    expect(windows.map((w) => `${w.short} ${windowWhen(w.start, w.end, liveNow, TZ)}`)).toEqual([
      "Power home Now until 18:00",
      "Export → 35% Tonight 18:00–19:00",
      "Power home Tonight 19:00–00:00",
      "Charge → 100% Tonight 00:00–05:30",
    ]);
    const charge = windows[3];
    expect(charge.trades).not.toBeNull();
    expect(charge.trades!.count).toBe(3);
    expect(charge.soc).toEqual({ start: 4, end: 100 });
    expect(tradeNote(charge.trades!, TZ)).toMatch(
      /^Also sells 5\.5 kWh overnight at 13\.9–14\.4p and buys it back at 6\.67p \(about 7\.\dp\/kWh profit\)$/,
    );
    expect(windows[1].tags).toContain("saving");
    // Home use shows where the battery goes, from 35% down to the 4% reserve.
    expect(windows[2].soc).toEqual({ start: 35, end: 4 });
    expect(windows[2].cost).toBeCloseTo(0.27, 2);
  });

  it("folds an export and a short break inside the cheap window into one charge", () => {
    const cheap = { importRate: 6.67 };
    const windows = planWindows(
      [
        slot(0, { ...cheap, action: "charge", targetPercent: 22, socForecast: 4, socForecastEnd: 22 }),
        slot(30, {
          ...cheap,
          action: "export",
          exportRate: 13.99,
          socForecast: 22,
          socForecastEnd: 4,
          socChangeKwh: -2.4,
        }),
        slot(60, { ...cheap, action: "demand", socForecast: 4 }),
        slot(90, { ...cheap, action: "charge", targetPercent: 100, socForecast: 4, socForecastEnd: 100 }),
      ],
      { now: T0 - MIN },
    );
    expect(windows).toHaveLength(1);
    expect(windows[0].short).toBe("Charge → 100%");
    expect(windows[0].trades).toMatchObject({ count: 1, kwh: 2.4, buy: 6.67 });
    expect(windows[0].pauses).toHaveLength(1);
  });

  it("does not fold an export followed by home power at the full price", () => {
    const windows = planWindows(
      [
        slot(0, { importRate: 6.67, action: "charge" }),
        slot(30, { importRate: 6.67, action: "export" }),
        slot(60, { importRate: 31.73, action: "demand" }),
        slot(90, { importRate: 6.67, action: "charge" }),
      ],
      { now: T0 - MIN },
    );
    expect(windows.map((w) => w.key)).toEqual(["charge", "export", "demand", "charge"]);
  });

  it("names an export at the reserve for what it is", () => {
    const [w] = planWindows([slot(0, { action: "export", socForecast: 4, socForecastEnd: 4 })], {
      now: T0,
      reserve: 4,
    });
    expect(w.atReserve).toBe(true);
    expect(w.label).toBe("Export window – battery at reserve");
    expect(w.tone).toBe("neutral");
  });

  it("folds a 30-minute home-power break into a note on the window", () => {
    const windows = planWindows(
      [slot(0, { action: "charge" }), slot(30, { action: "demand" }), slot(60, { action: "charge" })],
      { now: T0 - MIN },
    );
    expect(windows).toHaveLength(1);
    expect(windows[0].pauses).toEqual([{ start: T0 + 30 * MIN, end: T0 + 60 * MIN }]);
  });

  it("keeps a longer break as its own window", () => {
    const windows = planWindows(
      [
        slot(0, { action: "charge" }),
        slot(30, { action: "demand" }),
        slot(60, { action: "demand" }),
        slot(90, { action: "charge" }),
      ],
      { now: T0 - MIN },
    );
    expect(windows.map((w) => w.key)).toEqual(["charge", "demand", "charge"]);
  });

  it("uses the next slot's level when an older plan has no end level", () => {
    const [w] = planWindows(
      [slot(0, { action: "charge", socForecast: 20 }), slot(30, { action: "demand", socForecast: 45 })],
      {
        now: T0,
      },
    );
    expect(w.soc).toEqual({ start: 20, end: 45 });
  });

  it("tags free sessions and estimated prices", () => {
    const [w] = planWindows([slot(0, { action: "charge", importRate: 0, importRateType: "copy" })], { now: T0 });
    expect(w.tags).toEqual(["free", "estimated"]);
  });
});

describe("relative day words", () => {
  const at = (iso: string) => Date.parse(iso);
  const evening = at("2026-10-05T15:54:00Z");
  it("calls this evening and the small hours after it tonight", () => {
    expect(dayWord(at("2026-10-05T22:30:00Z"), evening, TZ)).toBe("Tonight");
    expect(dayWord(at("2026-10-06T01:00:00Z"), evening, TZ)).toBe("Tonight");
    expect(dayWord(at("2026-10-06T06:00:00Z"), evening, TZ)).toBe("Tomorrow");
    expect(dayWord(at("2026-10-06T22:30:00Z"), evening, TZ)).toBe("Tomorrow night");
    expect(dayWord(at("2026-10-05T12:00:00Z"), evening, TZ)).toBe("Today");
    expect(dayWord(at("2026-10-08T12:00:00Z"), evening, TZ)).toBe("Thu 8 Oct");
  });
  it("in the small hours, the night still in progress is tonight", () => {
    const small = at("2026-10-06T01:00:00Z");
    expect(dayWord(at("2026-10-06T03:00:00Z"), small, TZ)).toBe("Tonight");
    expect(dayWord(at("2026-10-06T09:00:00Z"), small, TZ)).toBe("Today");
    expect(dayWord(at("2026-10-05T20:00:00Z"), small, TZ)).toBe("Tonight");
  });
  it("calls the morning after the cheap night tomorrow, not tonight", () => {
    // Live, 5 Oct at 18:08: "Power your home · Tonight 05:30–07:00" read as if it were this evening.
    const at1808 = Date.parse("2026-10-05T17:08:00Z");
    const when = planWindows(liveSlots, { now: at1808, to: at1808 + 24 * 3600000, reserve: 4 }).map(
      (w) => `${w.short} ${windowWhen(w.start, w.end, at1808, TZ)}`,
    );
    expect(when).toContain("Charge → 100% Tonight 00:00–05:30");
    expect(when).toContain("Power home Tomorrow 05:30–07:00");
    expect(when).toContain("Hold at target Tomorrow 07:00–08:30");
    expect(when.filter((w) => w.includes("Tonight 05:30"))).toEqual([]);
    expect(windowWhen(at("2026-10-06T04:30:00Z"), at("2026-10-06T08:00:00Z"), at1808, TZ)).toBe("Tomorrow 05:30–09:00");
    expect(dayWord(at("2026-10-06T03:30:00Z"), at1808, TZ)).toBe("Tonight");
  });
  it("reads the night before as last night", () => {
    expect(dayWord(at("2026-10-04T23:30:00Z"), evening, TZ)).toBe("Last night");
  });
});

describe("shortAction", () => {
  it("adds targets to charge and export only", () => {
    expect(shortAction(slotAction({ action: "charge", targetPercent: 94 }))).toBe("Charge → 94%");
    expect(shortAction(slotAction({ action: "freeze-charge", targetPercent: 50 }))).toBe("Hold");
    expect(shortAction(slotAction({ action: "demand", actionId: "hold-for-car" }))).toBe("Hold for car");
  });
});

describe("battery flow under a window", () => {
  it("says from battery only when the battery gives energy", () => {
    expect(batteryFlowText({ kwh: -1.86 })).toBe("1.9 kWh from battery");
    // A sunny "power your home" window, 50% → 100%: solar fills the battery.
    expect(batteryFlowText({ kwh: 5 })).toBe("5.0 kWh into the battery");
    expect(batteryFlowText({ kwh: 0.04 })).toBe("");
    expect(batteryFlowText({ kwh: null })).toBe("");
  });
  it("names the reserve from Predbat's own setting", () => {
    const [w] = planWindows(
      [
        slot(0, { action: "demand", socForecast: 30, socForecastEnd: 15, cost: 0.4 }),
        slot(30, { action: "charge", socForecast: 15 }),
      ],
      { now: T0 - MIN },
    );
    expect(windowNotes(w, TZ, 15)).toEqual(["The battery reaches its reserve; the grid covers the rest."]);
    expect(windowNotes(w, TZ, 4)).toEqual([]);
    expect(windowNotes(w, TZ)).toEqual([]);
  });
});

describe("prices in What's next", () => {
  const windows = planWindows(liveSlots, { now: liveNow, to: liveNow + 30 * 3600000, reserve: 4 });
  const saving = windows.find((w) => w.tags.includes("saving"))!;
  const evening = windows.find((w) => windowWhen(w.start, w.end, liveNow, TZ) === "Tonight 19:00–00:00")!;
  it("headlines what an export pays, not the import price", () => {
    const p = headlinePrice(saving);
    expect(p.kind).toBe("export");
    expect(p.text).toBe("33.4–34.7p");
    // Graded on the export scale, a well-paid export is the greenest band, never the import scale's amber.
    expect(sellBand(p.value, 13.3, 34.7)).toBe(4);
    expect(headlinePrice(windows[0]).kind).toBe("import");
  });
  it("colours a price range by its time-weighted average, not its cheap end", () => {
    // 19:00–00:00 is 31.73p until the cheap rate at 23:30: mostly dear.
    expect(priceRange(evening.importPrice)).toBe("6.7–31.7p");
    const avg = averagePrice(evening.slots, "import")!;
    expect(avg).toBeGreaterThan(28);
    expect(priceBand(avg, 6.67, 40.23)).toBeGreaterThanOrEqual(3);
  });
  it("counts the running total from now", () => {
    const s = (min: number, cost: number): PlanSlotLike => slot(min, { cost });
    // Half-hours at £0.20, £0.40 and £0.60; now is 15 minutes into the second.
    expect(costFromNow([s(0, 0.2), s(30, 0.4), s(60, 0.6)], T0 + 45 * MIN)).toBeCloseTo(0.8, 5);
    expect(costFromNow([s(0, 0.2)], -Infinity)).toBeCloseTo(0.2, 5);
  });
});

describe("a window's cost, everywhere it shows", () => {
  // Power home from 19:00 to 21:00, now 19:45: £0.30 + £0.40 + £0.50 + £0.60 planned; the 19:00 half-hour and half of
  // the 19:30 one are gone, so £1.30 is still to come. The battery runs down to the 4% reserve.
  const start = Date.parse("2026-10-05T18:00:00Z");
  const s = (i: number, cost: number, soc: number) =>
    ({
      time: new Date(start + i * 30 * MIN).toISOString(),
      durationMinutes: 30,
      action: "demand",
      importRate: 31.73,
      cost,
      socForecast: soc,
      socForecastEnd: Math.max(4, soc - 3),
    }) as PlanSlotLike;
  const now = start + 45 * MIN;
  const [w] = planWindows([s(0, 0.3, 13), s(1, 0.4, 10), s(2, 0.5, 7), s(3, 0.6, 4)], { now, reserve: 4 });
  it("shows the part still to come for the window in progress, and the card, cell and note agree", () => {
    expect(w.phase).toBe("current");
    const shown = shownCost(w, now);
    expect(shown.partial).toBe(true);
    expect(shown.cost).toBeCloseTo(1.3, 5);
    // The desktop cell shows gbp(shown.cost); the phone card and Today's line say the same amount.
    const cell = gbp(shown.cost);
    expect(cell).toBe("£1.30");
    expect(moneyText(w, shown.cost, shown.partial)).toBe(`≈${cell} still to come`);
    expect(moneyWords(shown.cost, shown.partial)).toBe(`about ${cell} more from the grid`);
    // The note says what happens and leaves the money to the figure beside it: never a second, different amount.
    const notes = windowNotes(w, TZ, 4, shown.cost);
    expect(notes).toEqual(["The battery reaches its reserve; the grid covers the rest."]);
    expect(notes.join(" ")).not.toMatch(/£/);
  });
  it("shows the whole window's cost before it starts", () => {
    const shown = shownCost(w, start - MIN);
    expect(shown).toEqual({ cost: w.cost, partial: false });
    expect(moneyWords(shown.cost)).toBe("about £1.80 from the grid");
    expect(moneyWords(-1.98)).toBe("earns about £1.98");
  });
  it("reads the window in progress from the battery's measured level", () => {
    expect(socNowText(w, 7)).toBe("now 7% → 4%");
    expect(socNowText(w, null)).toBe("13% → 4%");
    expect(socNowText({ ...w, phase: "future" }, 7)).toBe("13% → 4%");
  });
});

describe("planSentence", () => {
  it("describes the plan with the same merged windows the page lists", () => {
    const at1808 = Date.parse("2026-10-05T17:08:00Z");
    const windows = planWindows(liveSlots, { now: at1808, reserve: 4 });
    const text = planSentence(windows, at1808, at1808 + 24 * 3600000, at1808, TZ);
    expect(text).toMatch(/^Next 24 h: Predbat plans to /);
    expect(text).toContain("charge to 100% tonight 00:00–05:30 at 6.67p");
    // No "charge from the grid Tue 6 01:00–03:00" fragments of the one overnight charge.
    expect(text).not.toMatch(/01:00–03:00/);
  });
  it("says so when nothing is planned", () => {
    expect(planSentence([], T0, T0 + 12 * 3600000, T0, TZ)).toBe("Next 12 h: no charging or exporting planned.");
  });
  it("says nothing about the plan ahead for a range that is all in the past", () => {
    const at1808 = Date.parse("2026-10-05T17:08:00Z");
    const windows = planWindows(liveSlots, { now: at1808, reserve: 4 });
    // "Past 24 h": the range ends now, or a few minutes into the half-hour in progress.
    expect(planSentence(windows, at1808 - 24 * 3600000, at1808, at1808, TZ)).toBe("");
    expect(planSentence(windows, at1808 - 24 * 3600000, at1808 + 22 * MIN, at1808, TZ)).toBe("");
    // A range that runs from the past into the future only counts the part ahead.
    expect(planSentence(windows, at1808 - 12 * 3600000, at1808 + 12 * 3600000, at1808, TZ)).toMatch(/^Next 12 h: /);
  });
});
