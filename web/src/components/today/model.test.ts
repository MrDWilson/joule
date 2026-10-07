import { describe, expect, it } from "vitest";
import timeline from "./fixtures/live-timeline-2026-10-05.json";
import summary from "./fixtures/live-summary-2026-10-05.json";
import livePlan from "../plan/fixtures/live-plan-2026-10-05.json";
import {
  batteryNow,
  batteryOutlook,
  costView,
  gridView,
  coverageNote,
  directionText,
  homeUse,
  lastCheapWindow,
  lastNight,
  nextWindow,
  nightFlag,
  planAge,
  planEvery,
  planStaleness,
  stripLegend,
  type LastNight,
  presentAction,
  priceNow,
  runStrip,
  soFarSentence,
  topFinding,
  yesterdayByNow,
  type MetricDetail,
  type SummaryDetail,
} from "./model";
import { planWindows, windowWhen, type PlanSlotLike } from "../plan/windows";
import { slotAction } from "../../lib/planActions";
import { kwh } from "../../lib/format";
import type { Investigation } from "../../types";

const TZ = "Europe/London";
const NOW = Date.parse("2026-10-05T15:54:00Z"); // 16:54 BST
const recent = timeline.slots as PlanSlotLike[];
const plan = livePlan.slots as PlanSlotLike[];
const live = summary as unknown as SummaryDetail;

describe("presentAction", () => {
  it("speaks in the present tense and keeps variants' own wording", () => {
    expect(presentAction(slotAction({ action: "demand" }))).toBe("Powering your home");
    expect(presentAction(slotAction({ action: "charge" }))).toBe("Charging from the grid");
    expect(presentAction(slotAction({ action: "freeze-export" }))).toBe("Exporting solar, battery held");
    expect(presentAction(slotAction({ action: "demand", actionId: "hold-for-car" }))).toBe("Hold battery for the car");
  });
});

describe("batteryNow", () => {
  const reading = {
    value: 83.9,
    unit: "%",
    time: "2026-10-05T15:53:30Z",
    status: "observed",
    entityId: "x",
    source: "HA",
  };
  it("reads the direction from the measured level and the rate from the plan", () => {
    const b = batteryNow({ reading, recent, plan, now: NOW });
    expect(b.value).toBeCloseTo(83.9);
    expect(b.stale).toBe(false);
    // 85.4% at 16:00 → 83.9% now: discharging by the meter; Predbat's plan for the slot gives the rate.
    expect(b.direction).toBe("discharging");
    expect(b.directionSource).toBe("measured");
    expect(directionText(b)).toBe("Discharging · about 1.0 kW");
  });
  it("takes the direction from Predbat's plan when the meter barely moves, and says so", () => {
    const flat: PlanSlotLike[] = [
      { time: "2026-10-05T15:00:00Z", durationMinutes: 30, socActualStart: 84.4, socActual: 84.2 },
    ];
    const b = batteryNow({ reading: { ...reading, value: 84 }, recent: flat, plan, now: NOW });
    expect(b.direction).toBe("discharging");
    expect(b.directionSource).toBe("plan");
  });
  it("shows the last good level greyed when the sensor is offline", () => {
    const b = batteryNow({
      reading: {
        ...reading,
        value: null,
        status: "unavailable",
        lastObservedValue: 81,
        lastObservedAt: "2026-10-05T14:48:00Z",
      },
      recent,
      plan,
      now: NOW,
    });
    expect(b).toMatchObject({ value: 81, stale: true, time: "2026-10-05T14:48:00Z", direction: null });
  });
  it("trusts a measured rise over a flat plan", () => {
    const rising: PlanSlotLike[] = [
      { time: "2026-10-05T15:00:00Z", durationMinutes: 30, socActualStart: 70, socActual: 76 },
    ];
    const b = batteryNow({ reading: { ...reading, value: 80 }, recent: rising, plan: [], now: NOW });
    expect(b.direction).toBe("charging");
    expect(b.directionSource).toBe("measured");
    expect(b.kw).toBeNull();
  });
});

describe("prices and what comes next", () => {
  it("prefers the fresh tariff sensor and finds the cheapest price ahead", () => {
    const p = priceNow({
      importReading: {
        value: 31.7296,
        unit: "p/kWh",
        time: "2026-10-05T15:53:30Z",
        status: "observed",
        entityId: "",
        source: "",
      },
      plan,
      now: NOW,
    });
    expect(p.import).toBeCloseTo(31.73);
    expect(p.export).toBe(22.1);
    expect(p.cheap).toBe(false);
    expect(p.cheapest).toEqual({ rate: 6.67, at: Date.parse("2026-10-05T22:30:00Z") });
  });
  it("shows the saving session's plan price, not the tariff sensor's usual rate", () => {
    // Live, 5 Oct 18:08: the export sensor read 24.9p while Predbat planned the saving session at 33.4p.
    const at = Date.parse("2026-10-05T17:08:00Z");
    const reading = (value: number) => ({
      value,
      unit: "p/kWh",
      time: "2026-10-05T17:07:00Z",
      status: "observed",
      entityId: "",
      source: "",
    });
    const p = priceNow({ importReading: reading(31.73), exportReading: reading(24.9), plan, now: at });
    expect(p.export).toBe(33.4);
    // The import sensor stays: the session pays for export, it doesn't change what importing costs.
    expect(p.import).toBeCloseTo(31.73);
    // A free session shows its own 0p import price.
    const free = plan.map((x) => (x.time.startsWith("2026-10-05T18:00") ? { ...x, importRate: 0 } : x));
    expect(priceNow({ importReading: reading(31.73), plan: free, now: at }).import).toBe(0);
    // Outside a session the fresh sensor wins.
    expect(priceNow({ exportReading: { ...reading(22.4), time: "2026-10-05T15:53:00Z" }, plan, now: NOW }).export).toBe(
      22.4,
    );
  });
  it("names the next charge, export or hold, and where the battery goes", () => {
    const windows = planWindows(plan, { now: NOW, reserve: 4 });
    const next = nextWindow(windows, NOW)!;
    expect(next.short).toBe("Export → 35%");
    expect(windowWhen(next.start, next.end, NOW, TZ)).toBe("Tonight 18:00–19:00");
    expect(batteryOutlook(windows, plan, NOW, TZ)).toBe("Falls to 4% by 23:00 · charges to 100% tonight");
  });
});

describe("gridView", () => {
  it("says what was bought and sold so far, in kWh and pounds", () => {
    const g = gridView(live, TZ)!;
    expect(g.importKwh).toBeCloseTo(live.metrics.grid_import.energyKwh!, 6);
    expect(g.importGbp).toBeCloseTo(live.importCostGbp!, 6);
    expect(g.averagePence).toBeCloseTo((live.importCostGbp! * 100) / live.metrics.grid_import.energyKwh!, 6);
    expect(g.exported).toBe(`Exported ${kwh(live.metrics.grid_export.energyKwh)} · earned £0.77`);
    expect(g.note).toBeNull();
  });
  it("says so when nothing has been exported, and is empty without grid meters", () => {
    const quiet = { ...live, metrics: { ...live.metrics, grid_export: { ...live.metrics.grid_export, energyKwh: 0 } } };
    expect(gridView(quiet, TZ)!.exported).toBe("Nothing exported yet");
    const barely = {
      ...live,
      metrics: { ...live.metrics, grid_import: { ...live.metrics.grid_import, energyKwh: 0.04 } },
    };
    expect(gridView(barely, TZ)!.averagePence).toBeNull();
    expect(gridView({ ...live, metrics: { load: live.metrics.load } }, TZ)).toBeNull();
  });
});

describe("costView", () => {
  it("is paid minus earned with both sides shown", () => {
    const c = costView(live, TZ)!;
    expect(c).toMatchObject({ value: "£1.05", label: "Net cost today", approx: false, earning: false, note: null });
    expect(c.breakdown).toBe("Paid £1.83 · Earned £0.77");
  });
  it("adds today's standing charge so far when it is included, and keeps paid and earned as measured", () => {
    const standing = { standingChargeGbp: 0.27, standingChargePencePerDay: 53.68, standingChargeIncluded: true };
    const c = costView({ ...live, ...standing }, TZ)!;
    expect(c.value).toBe("£1.32");
    expect(c.breakdown).toBe("Paid £1.83 · Earned £0.77");
    expect(c.standing).toMatchObject({ text: "£0.27", rate: "£0.54/day", included: true });
    expect(soFarSentence({ cost: c, night: null, needs: 0 })).toBe(
      "Net cost £1.32 so far (paid £1.83, earned £0.77, standing charge £0.27) · nothing needs you",
    );
    expect(costView({ ...live, ...standing, standingChargeIncluded: false }, TZ)!.value).toBe("£1.05");
  });
  it("reads a negative net as earnings, in plain pounds", () => {
    const c = costView({ ...live, importCostGbp: 0.4, exportCreditGbp: 4.65, netCostGbp: -4.25 }, TZ)!;
    expect(c).toMatchObject({ value: "£4.25", label: "Net earnings today", earning: true });
  });
  it("marks an estimate and names the meter that was offline", () => {
    const export_ = {
      ...live.metrics.grid_export,
      coverageFraction: 0.82,
      gaps: [{ from: "2026-10-04T23:02:00Z", to: "2026-10-05T01:47:00Z", reason: "offline" }],
    };
    const c = costView({ ...live, exportCostCoverage: 0.82, metrics: { ...live.metrics, grid_export: export_ } }, TZ)!;
    expect(c.value).toBe("≈ £1.05");
    expect(c.note).toBe("Export meter offline 00:02–02:47");
  });
});

describe("coverageNote", () => {
  it("says nothing at 98% or better", () => {
    expect(coverageNote("pv", live.metrics.pv as MetricDetail, TZ)).toBeNull();
  });
  it("names the meter and the hours", () => {
    const m: MetricDetail = {
      energyKwh: 2,
      observedSeconds: 0,
      coverageFraction: 0.7,
      missingIntervals: 2,
      gaps: [
        { from: "2026-10-05T08:00:00Z", to: "2026-10-05T08:30:00Z", reason: "idle" },
        { from: "2026-10-05T10:00:00Z", to: "2026-10-05T12:00:00Z", reason: "offline" },
      ],
    };
    expect(coverageNote("pv", m, TZ)).toBe("Solar meter offline 11:00–13:00 and 1 more gap");
  });
});

describe("tiles", () => {
  it("home use leaves the car out and shows it alongside", () => {
    const h = homeUse(live as never)!;
    expect(h.metric?.energyKwh).toBeCloseTo(12.32, 2);
    expect(h.car).toBeCloseTo(4.58, 2);
  });
  it("compares with yesterday by now, or not at all", () => {
    expect(yesterdayByNow({ value: 42.04, coverage: 1 }, (n) => kwh(n))).toBe("Yesterday by now: 42.0 kWh");
    expect(yesterdayByNow({ value: 42, coverage: 0.5 }, (n) => kwh(n))).toBeNull();
    expect(yesterdayByNow({ value: null }, (n) => kwh(n))).toBeNull();
  });
});

describe("last night", () => {
  it("finds the 23:30–05:30 cheap window and tells its story", () => {
    const w = lastCheapWindow(recent, NOW)!;
    expect(windowWhen(w.start, w.end, NOW, TZ)).toBe("Last night 23:30–05:30");
    expect(w.rate).toBe(6.67);
    const story = lastNight(
      w,
      {
        ...live,
        importCostGbp: 1.8,
        exportCreditGbp: 0.3,
        metrics: {
          ...live.metrics,
          grid_import: { ...live.metrics.grid_import, energyKwh: 26.96 },
          battery_charge: { ...live.metrics.battery_charge, energyKwh: 19.6 },
          ev: { ...live.metrics.ev, energyKwh: 4.58 },
        },
      },
      { level: 100, at: "2026-10-04T17:00:00Z" },
    );
    expect(Math.round(story.socStart!)).toBe(2);
    expect(Math.round(story.socEnd!)).toBe(90);
    expect(story.peak!.at).toBe(Date.parse("2026-10-05T04:30:00Z"));
    expect(story.averagePrice).toBeCloseTo(6.68, 2);
    expect(story.planned).toBe(100);
    expect(soFarSentence({ cost: costView(live, TZ), night: story, needs: 0 })).toBe(
      "Net cost £1.05 so far (paid £1.83, earned £0.77) · battery filled with 19.6 kWh overnight at 6.7p · nothing needs you",
    );
  });
  it("has no cheap window on a flat tariff", () => {
    expect(
      lastCheapWindow(
        recent.map((s) => ({ ...s, importRate: 25 })),
        NOW,
      ),
    ).toBeNull();
  });
  it("grades the night before while tonight's cheap window is still running", () => {
    // Two nights of 23:30–05:30 at 6.67p and 31.73p otherwise; at 02:00 BST on the second night, the first is graded.
    const base = Date.parse("2026-10-04T12:00:00Z");
    const slots: PlanSlotLike[] = Array.from({ length: 76 }, (_, i) => {
      const at = base + i * 30 * 60000;
      const h = (new Date(at).getUTCHours() + 1) % 24; // BST
      const m = new Date(at).getUTCMinutes();
      const cheap = h === 23 ? m >= 30 : h < 5 || (h === 5 && m < 30);
      return {
        time: new Date(at).toISOString(),
        durationMinutes: 30,
        importRate: cheap ? 6.67 : 31.73,
        action: "demand",
      };
    });
    const at = Date.parse("2026-10-06T01:00:00Z");
    const w = lastCheapWindow(
      slots.filter((s) => Date.parse(s.time) <= at),
      at,
    )!;
    expect(w).not.toBeNull();
    expect(new Date(w.start).toISOString()).toBe("2026-10-04T22:30:00.000Z");
    expect(new Date(w.end).toISOString()).toBe("2026-10-05T04:30:00.000Z");
  });
  it("waits until the cheap window has finished", () => {
    const at = Date.parse("2026-10-05T03:00:00Z");
    const w = lastCheapWindow(
      recent.filter((s) => Date.parse(s.time) + 30 * 60000 <= at + 30 * 60000),
      at,
    );
    expect(w).toBeNull();
  });
});

describe("soFarSentence", () => {
  const night = (start: string, end: string): LastNight =>
    ({
      window: { start: Date.parse(start), end: Date.parse(end), rate: 6.67, slots: [] },
      batteryKwh: 9.8,
      averagePrice: 6.7,
    }) as unknown as LastNight;
  it("names a daytime cheap window by its times, not 'overnight'", () => {
    expect(
      soFarSentence({
        cost: null,
        night: night("2026-10-05T12:00:00Z", "2026-10-05T15:00:00Z"),
        needs: 0,
        now: NOW,
        timeZone: TZ,
      }),
    ).toBe("Battery filled with 9.8 kWh 13:00–16:00 at 6.7p · nothing needs you");
  });
  it("leaves out a cheap window that ended before today", () => {
    expect(
      soFarSentence({
        cost: null,
        night: night("2026-10-03T22:30:00Z", "2026-10-04T04:30:00Z"),
        needs: 0,
        now: NOW,
        timeZone: TZ,
      }),
    ).toBe("Nothing needs you");
  });
  it("leads with what you're up on a good day, and leaves counting what needs you to the list below", () => {
    const cost = costView({ ...live, importCostGbp: 0.4, exportCreditGbp: 1.5, netCostGbp: -1.1 }, TZ);
    expect(soFarSentence({ cost, night: null, needs: 2 })).toBe("Up £1.10 today (paid £0.40, earned £1.50)");
    expect(soFarSentence({ cost, night: null, needs: 0 })).toBe(
      "Up £1.10 today (paid £0.40, earned £1.50) · nothing needs you",
    );
    expect(soFarSentence({ cost: null, night: null, needs: 1 })).toBe("");
  });
  it("marks an estimated net cost", () => {
    const cost = costView({ ...live, importCostCoverage: 0.5 }, TZ);
    expect(soFarSentence({ cost, night: null, needs: 1 })).toMatch(/^Net cost ≈£\d+\.\d\d so far \(paid /);
  });
});

describe("last night's flags", () => {
  const at = Date.parse("2026-10-05T02:00:00Z");
  it("names why the battery fell during a charge when the plan history knows", () => {
    expect(nightFlag({ text: "Battery fell during a charge", cause: "car" }, at)).toMatchObject({
      text: "Battery fell while the car was charging",
      tone: "warn",
    });
    expect(nightFlag({ text: "Battery fell during a charge", cause: "pause" }, at)).toMatchObject({
      text: "Battery fell during a planned pause",
      tone: "info",
    });
  });
  it("keeps the plain text when there is no cause", () => {
    expect(nightFlag("Battery fell during a charge", at)).toEqual({
      text: "Battery fell during a charge",
      at,
      cause: null,
      tone: "warn",
    });
    expect(nightFlag({ text: "Battery fell during a charge", cause: null }, at).text).toBe(
      "Battery fell during a charge",
    );
  });
});

const inv = (id: string, hoursAgo: number, extra: Partial<Investigation> = {}): Investigation =>
  ({
    id,
    at: new Date(NOW - hoursAgo * 3600000).toISOString(),
    title: id,
    summary: "",
    category: "",
    confidence: "",
    evidence: [],
    steps: [],
    provider: "Demo",
    request: { question: null, from: null, to: null },
    status: "Completed",
    toolEvidence: [],
    evidenceReferences: [],
    verdict: "no_change",
    ...extra,
  }) as Investigation;

describe("AI card", () => {
  it("surfaces the problem with most money at stake from the last 48 hours", () => {
    const list = [
      inv("quiet", 1),
      inv("small", 3, { verdict: "problem", impactPence: 20 }),
      inv("big", 10, { verdict: "opportunity", impactPence: 90 }),
      inv("old", 50, { verdict: "problem", impactPence: 900 }),
      inv("dismissed", 2, { verdict: "problem", impactPence: 500, dismissedAt: "x" }),
      inv("failed", 1, { status: "Failed", verdict: "problem", impactPence: 999 }),
      inv("repeat", 1, { verdict: "problem", impactPence: 800, repeatOf: "small" }),
    ];
    expect(topFinding(list, NOW)?.id).toBe("big");
    expect(topFinding([inv("quiet", 1)], NOW)).toBeNull();
  });
  it("counts today's checks, with failed ones as didn't finish", () => {
    const strip = runStrip(
      [
        inv("a", 2),
        inv("b", 1, { status: "Failed", verdict: null }),
        inv("c", 0.5, { verdict: "problem" }),
        inv("y", 20),
      ],
      NOW,
      TZ,
    );
    expect(strip.runs.map((r) => r.view)).toEqual(["no_change", "didnt_finish", "problem"]);
    expect(strip).toMatchObject({ quiet: 1, failed: 1, findings: 1, used: null });
    // The dots get a legend in the shared words, only for the outcomes present.
    expect(stripLegend(strip).map((l) => l.text)).toEqual(["1 found something", "1 nothing new", "1 didn’t finish"]);
  });
  it("groups problems and opportunities as found something, keeping both dot colours", () => {
    const strip = runStrip(
      [inv("p", 2, { verdict: "problem" }), inv("o", 1, { verdict: "opportunity" }), inv("q", 0.5)],
      NOW,
      TZ,
    );
    expect(stripLegend(strip)).toEqual([
      { key: "found", views: ["problem", "opportunity"], count: 2, text: "2 found something" },
      { key: "no_change", views: ["no_change"], count: 1, text: "1 nothing new" },
    ]);
  });
  it("counts the day as the server's daily allowance does", () => {
    // NOW is 13:00 BST. A check started before midnight and finished after it belongs to today, as the server counts it;
    // an Ask Joule question is an AI check too.
    const midnight = Date.parse("2026-10-04T23:00:00Z");
    const list = [
      inv("overnight", 0, {
        at: new Date(midnight - 5 * 60000).toISOString(),
        finishedAt: new Date(midnight + 60000).toISOString(),
      }),
      inv("late", 0, {
        at: new Date(midnight - 30 * 60000).toISOString(),
        finishedAt: new Date(midnight - 20 * 60000).toISOString(),
      }),
      inv("ask", 1, { request: { question: "Why did the battery fall?", from: null, to: null } }),
    ];
    const usage = [
      { at: new Date(midnight + 60000).toISOString(), status: "Completed", inputTokens: 10, outputTokens: 5 },
      { at: new Date(NOW - 3600000).toISOString(), status: "Completed", inputTokens: 10, outputTokens: 5 },
      // Failed before the AI answered: not counted against the allowance.
      { at: new Date(NOW - 1800000).toISOString(), status: "Failed", inputTokens: 0, outputTokens: 0 },
      { at: new Date(midnight - 20 * 60000).toISOString(), status: "Completed", inputTokens: 10, outputTokens: 5 },
    ];
    const strip = runStrip(list, NOW, TZ, usage);
    expect(strip.runs.map((r) => r.id)).toEqual(["overnight", "ask"]);
    expect(strip.used).toBe(2);
  });
});

describe("freshness", () => {
  it("flags a plan read more than two collections ago, and why", () => {
    expect(planStaleness({ collectedAt: new Date(NOW - 6 * 60000).toISOString(), now: NOW })).toBeNull();
    expect(
      planStaleness({ collectedAt: new Date(NOW - 3 * 3600000).toISOString(), collectionError: "timeout", now: NOW })
        ?.label,
    ).toBe("Plan last updated 3 h ago – Predbat unreachable");
  });
  it("lets an unchanged plan age through Predbat's own re-planning interval", () => {
    const ago = (m: number) => new Date(NOW - m * 60000).toISOString();
    // Predbat re-plans every 30 minutes and Joule reads it fine: a 25-minute-old plan is current.
    expect(planStaleness({ collectedAt: ago(25), lastCollection: ago(2), everyMinutes: 30, now: NOW })).toBeNull();
    expect(planStaleness({ collectedAt: ago(45), lastCollection: ago(2), everyMinutes: 30, now: NOW })?.label).toBe(
      "Plan last updated 45 min ago – not refreshed since",
    );
    // Every 10 minutes by default: 21 minutes is late.
    expect(planStaleness({ collectedAt: ago(19), lastCollection: ago(1), now: NOW })).toBeNull();
    expect(planStaleness({ collectedAt: ago(21), lastCollection: ago(1), now: NOW })).not.toBeNull();
    // When reads fail, two missed collections are enough.
    expect(
      planStaleness({ collectedAt: ago(12), lastCollection: ago(12), collectionError: "timeout", now: NOW })?.label,
    ).toBe("Plan last updated 12 min ago – Predbat unreachable");
    expect(
      planStaleness({ collectedAt: ago(12), lastCollection: ago(4), collectionError: "timeout", now: NOW }),
    ).toBeNull();
    expect(planEvery([{ key: "calculate_plan_every", value: "5" }])).toBe(5);
    expect(planEvery([])).toBeNull();
  });
  it("calls a plan old after three of Predbat's planning intervals", () => {
    expect(planAge(new Date(NOW - 25 * 60000).toISOString(), NOW, 10)).toMatchObject({ minutes: 25, old: false });
    expect(planAge(new Date(NOW - 31 * 60000).toISOString(), NOW, 10)).toMatchObject({ old: true });
  });
});
