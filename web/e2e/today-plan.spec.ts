import { test, expect, type Page } from "@playwright/test";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { fetchFresh, fulfillRewritten } from "./support/routes";
import { openPage } from "./support/navigation";

// A route handler still reading a fetched response when the test ends would fail it as the context closes.
test.afterEach(async ({ page }) => page.unrouteAll({ behavior: "ignoreErrors" }));

// Today and Plan: the Now hero, honest figures, merged plan windows and the Plan page's tables, at phone,
// tablet and desktop widths, on the demo and on live-shaped payloads.
test.use({ timezoneId: "Europe/London" });

const HALF_HOUR = 1800000;
const livePlan = JSON.parse(
  readFileSync(
    fileURLToPath(new URL("../src/components/plan/fixtures/live-plan-2026-10-05.json", import.meta.url)),
    "utf8",
  ),
) as { at: string; slots: { time: string }[] };

/** The live 5 Oct plan moved so that its first slot starts in the current half-hour (times of day are kept per slot). */
function shiftedLivePlan() {
  const first = Date.parse(livePlan.slots[0].time);
  const shift = Math.floor(Date.now() / HALF_HOUR) * HALF_HOUR - Math.floor(first / HALF_HOUR) * HALF_HOUR;
  return livePlan.slots.map((s) => ({ ...s, time: new Date(Date.parse(s.time) + shift).toISOString() }));
}

async function withState(page: Page, edit: (payload: any) => void) {
  await page.route("**/api/state", async (route) => {
    const response = await fetchFresh(route);
    const payload = await response.json();
    edit(payload);
    await fulfillRewritten(route, response, payload);
  });
}

test("the first phone screen has the Now hero and the net cost, and the battery lane shows", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.addInitScript(() => localStorage.setItem("joule.firstRunSeen", "1"));
  await page.goto("/#/today");
  const hero = page.getByRole("region", { name: /Right now/ });
  await expect(hero).toBeVisible();
  const cost = page.getByRole("article", { name: /^Net (cost|earnings) today$/ });
  await expect(cost).toBeVisible();
  const box = (await cost.boundingBox())!;
  expect(box.y).toBeLessThan(844);
  expect((await hero.boundingBox())!.y).toBeLessThan(200);
  // The timeline's battery lane is drawn on phones too.
  await expect(page.locator("svg.tl-svg").first()).toBeVisible();
  await expect(page.locator("svg.tl-svg").first()).toContainText("Battery %");
  await expect(page.locator(".today-tiles")).not.toContainText(/not compared|% of today measured|incomplete/);
  await expect(page.getByRole("heading", { name: "Battery outlook" })).toHaveCount(0);
  await page.screenshot({ path: "../.cache/screenshots/today-390.png", fullPage: true });
});

test("net cost is paid minus earned, and a negative net reads as earnings", async ({ page }) => {
  let net = 1.1;
  await page.route("**/api/telemetry/summary?*", async (route) => {
    const response = await fetchFresh(route);
    const summary = await response.json();
    const url = new URL(route.request().url());
    // Only today so far (from local midnight to now), not yesterday or a window.
    if (Math.abs(Date.parse(url.searchParams.get("to")!) - Date.now()) < 120000) {
      summary.importCostGbp = net + 0.7 + (net < 0 ? 0 : 0);
      summary.exportCreditGbp = 0.7;
      summary.netCostGbp = net;
      // The energy figures only: the standing charge on top has its own checks (money.spec.ts).
      summary.standingChargeGbp = null;
      summary.importCostCoverage = 1;
      summary.exportCostCoverage = 1;
      if (net < 0) {
        summary.importCostGbp = 0.4;
        summary.exportCreditGbp = 0.4 - net;
      }
    }
    await fulfillRewritten(route, response, summary);
  });
  await page.goto("/#/today");
  const cost = page.getByRole("article", { name: "Net cost today" });
  await expect(cost.locator(".stat-value")).toHaveText("£1.10");
  await expect(cost).toContainText("Paid £1.80 · Earned £0.70");
  await expect(page.getByRole("region", { name: /Right now/ })).toContainText(
    "Net cost £1.10 so far (paid £1.80, earned £0.70)",
  );
  net = -4.25;
  await page.getByRole("button", { name: "Refresh now" }).click();
  const earnings = page.getByRole("article", { name: "Net earnings today" });
  await expect(earnings.locator(".stat-value")).toHaveText("£4.25");
  await expect(earnings).toContainText("Paid £0.40 · Earned £4.65");
  await expect(page.getByRole("region", { name: /Right now/ })).toContainText(
    "Up £4.25 today (paid £0.40, earned £4.65)",
  );
  await expect(earnings.locator(".stat-value span")).toHaveCSS("color", "rgb(79, 211, 162)");
});

test("the live plan reads as a few merged windows with price ranges and day words", async ({ page }) => {
  const slots = shiftedLivePlan();
  await withState(page, (p) => {
    p.plan = {
      id: "live-shaped",
      at: new Date().toISOString(),
      collectedAt: new Date().toISOString(),
      source: "Predbat",
      slots,
    };
  });
  await page.goto("/#/today");
  const coming = page.getByRole("region", { name: "Coming up" });
  const items = coming.locator(".window-item");
  await expect(items.first()).toBeVisible();
  // 12 hours of a plan that changes action ~30 times reads as a handful of windows.
  expect(await items.count()).toBeLessThanOrEqual(6);
  await expect(coming).toContainText(/Saving session/);
  await expect(coming).toContainText(/Also sells 5\.5 kWh (overnight )?at 13\.9–14\.4p and buys it back at 6\.67p/);
  await expect(coming).not.toContainText(/FrzExp|Demand slot|\/ Upcoming/);
  await coming.getByRole("button", { name: "Show the next 48 hours" }).click();
  expect(await items.count()).toBeGreaterThan(6);
});

test("an export window on the Plan headlines what it pays, on its own green scale", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 });
  const slots = shiftedLivePlan();
  await withState(page, (p) => {
    p.plan = {
      id: "live-shaped",
      at: new Date().toISOString(),
      collectedAt: new Date().toISOString(),
      source: "Predbat",
      slots,
    };
  });
  await page.goto("/#/plan");
  const next = page.getByRole("region", { name: "What's next, window by window" });
  // The saving-session export: paid 33.4–34.7p, not the 40.23p import price painted as dear.
  const row = next.locator(".plan-window-row").filter({ hasText: "Export → 35%" });
  await expect(row.locator(".price-pill")).toHaveText("33.4–34.7p");
  await expect(row.locator(".price-pill")).toHaveClass(/sell-4/);
  await expect(row).not.toContainText("40.23p");
  await page.setViewportSize({ width: 390, height: 844 });
  const card = page.locator(".next-card").filter({ hasText: "Export → 35%" });
  await expect(card.locator(".price-pill")).toHaveClass(/sell-4/);
});

test("an export run split by Agile prices is one window", async ({ page }) => {
  const start = Math.ceil(Date.now() / HALF_HOUR) * HALF_HOUR + 2 * HALF_HOUR;
  const slot = (i: number, extra: object) => ({
    time: new Date(start + i * HALF_HOUR).toISOString(),
    durationMinutes: 30,
    loadForecast: 0.3,
    pvForecast: 0,
    socForecast: 60 - i * 7,
    importRate: 24,
    exportRate: 12,
    cost: -0.1,
    action: "demand",
    ...extra,
  });
  await withState(page, (p) => {
    p.plan = {
      id: "split-export",
      at: new Date().toISOString(),
      collectedAt: new Date().toISOString(),
      source: "Predbat",
      slots: [
        slot(0, { action: "Exp", exportRate: 12.99 }),
        slot(1, { action: "Exp", exportRate: 12.32 }),
        slot(2, { action: "Exp", exportRate: 12.04 }),
        slot(3, { action: "Demand", cost: 0 }),
      ],
    };
  });
  await page.goto("/#/today");
  const exports = page.getByRole("region", { name: "Coming up" }).locator('.window-item[data-action="export"]');
  await expect(exports).toHaveCount(1);
  await expect(exports).toContainText("12.0–13.0p");
});

test("a plan Joule couldn't refresh says so instead of pretending it's current", async ({ page }) => {
  await withState(page, (p) => {
    const old = new Date(Date.now() - 3 * 3600000).toISOString();
    p.state.lastCollection = old;
    p.state.collectionError = "Predbat did not answer within 10 seconds";
    if (p.plan) p.plan.collectedAt = old;
  });
  await page.goto("/#/today");
  await expect(page.getByRole("region", { name: "Coming up" })).toContainText(
    "Plan last updated 3 h ago – Predbat unreachable",
  );
  await expect(page.getByRole("region", { name: /Right now/ })).toContainText("Plan last updated 3 h ago");
  await openPage(page, "Plan");
  await expect(page.getByRole("region", { name: "Predbat status" })).toContainText("Predbat unreachable");
});

test("a check that didn't finish is never a problem on Today", async ({ page }) => {
  await withState(page, (p) => {
    const now = Date.now();
    p.state.investigations = p.state.investigations.map((i: any, n: number) => ({
      ...i,
      at: new Date(now - (n + 1) * 600000).toISOString(),
      status: n === 0 ? "Failed" : i.status,
      verdict: n === 0 ? "problem" : "no_change",
      dismissedAt: null,
    }));
  });
  await page.goto("/#/today");
  const ai = page.getByRole("region", { name: "AI checks" });
  await expect(ai).toContainText("Nothing needs you right now");
  await expect(ai.locator(".run-dot.v-didnt_finish").first()).toBeAttached();
  await expect(ai.locator(".run-dot.v-problem")).toHaveCount(0);
});

test("Today's Needs you is the same list as Insights, trials due a decision included", async ({ page }) => {
  await withState(page, (p) => {
    const now = Date.now();
    p.state.experiments = [
      {
        id: "trial-due",
        title: "Suggestion applied: load scaling",
        hypothesis: "",
        status: "Running",
        revisionId: 999,
        startedAt: new Date(now - 7 * 86400000).toISOString(),
        reviewAt: new Date(now - 3600000).toISOString(),
        result: "",
      },
    ];
  });
  await page.goto("/#/insights");
  const inbox = page.getByRole("region", { name: /Needs you/ });
  await expect(inbox.getByRole("listitem").first()).toBeVisible();
  const insightsCount = await inbox.locator(".count-pill").innerText();
  const insightsTitles = (await inbox.locator(".inbox-row h3").allInnerTexts()).map((t) => t.trim());
  await page.goto("/#/today");
  const needs = page.getByRole("region", { name: /Needs you/ });
  await expect(needs.locator(".needs-count")).toHaveText(insightsCount);
  // The same rows, in the same order, as far as Today shows them.
  const todayTitles = (await needs.locator(".needs-title").allInnerTexts()).map((t) => t.trim());
  expect(todayTitles).toEqual(insightsTitles.slice(0, todayTitles.length));
  if (Number(insightsCount) > todayTitles.length)
    await expect(needs.getByRole("link", { name: `See all ${insightsCount}` })).toBeVisible();
  // The kinds read as on Insights, and a trial goes to its decision.
  await expect(needs).toContainText("Setting change");
  await page.route("**/api/state", async (route) => {
    const response = await fetchFresh(route);
    const payload = await response.json();
    payload.state.experiments = [
      {
        id: "trial-due",
        title: "Suggestion applied: load scaling",
        status: "Running",
        revisionId: 999,
        hypothesis: "",
        result: "",
        startedAt: new Date(Date.now() - 7 * 86400000).toISOString(),
        reviewAt: new Date(Date.now() - 3600000).toISOString(),
      },
    ];
    payload.state.proposals = [];
    payload.state.investigations = [];
    await fulfillRewritten(route, response, payload);
  });
  await page.reload();
  const trial = needs.locator(".needs-row.needs-trial");
  await expect(trial).toContainText("Trial ready");
  await expect(trial.getByRole("link", { name: /^Decide:/ })).toHaveAttribute("href", "#/insights/experiments");
  // The sentence under the hero leaves the counting to the list.
  await expect(page.getByRole("region", { name: /Right now/ })).not.toContainText(/needs? you/);
});

test("the battery ring turns amber near the reserve and says the reserve in words, with no marks to decode", async ({
  page,
}) => {
  await page.route("**/api/telemetry/status", async (route) => {
    const response = await fetchFresh(route);
    const payload = await response.json();
    payload.latestReadings.soc = { ...payload.latestReadings.soc, value: 6, lastObservedValue: 6 };
    await fulfillRewritten(route, response, payload);
  });
  await page.goto("/#/today");
  const ring = page.locator(".now-hero .battery-ring");
  await expect(ring.locator(".ring-label")).toHaveText("6%");
  await expect(ring).toHaveClass(/is-low/);
  // The reserve is written out under the ring, not a tick beside it; the arc has flat ends, so nothing pokes past 12 o'clock.
  await expect(ring.locator(".ring-reserve")).toHaveText(/^Reserve \d+%$/);
  await expect(ring.locator("svg line")).toHaveCount(0);
  await expect(ring.locator("svg circle")).toHaveCount(2);
  await expect(ring.locator(".ring-value")).toHaveAttribute("stroke-linecap", "butt");
});

test("the Plan page leads with Predbat's status and never says Upcoming", async ({ page }) => {
  await page.goto("/#/plan");
  const status = page.getByRole("region", { name: "Predbat status" });
  await expect(status).toContainText(/Sample plan|Plan/);
  await expect(status).toContainText(/from \d\d:\d\d/);
  await expect(status).toContainText("Reserve");
  await expect(page.getByRole("heading", { name: "What happened (last 24 h)" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "What’s next" })).toBeVisible();
  await expect(page.locator("main")).not.toContainText(/Upcoming|see the README| · Predbat|Not measured/);
  // Windows expand to their half-hours.
  const next = page.getByRole("region", { name: /What's next, window by window/ });
  const expand = next.getByRole("button", { name: /^Show the half-hours of/ }).first();
  const before = await next.locator("tr.slot-row").count();
  await expand.click();
  expect(await next.locator("tr.slot-row").count()).toBeGreaterThan(before);
  // Scroll regions can be reached with the keyboard.
  await expect(next).toHaveAttribute("tabindex", "0");
  await page.screenshot({ path: "../.cache/screenshots/plan-1440.png", fullPage: true });
});

for (const width of [390, 768, 1440]) {
  test(`Today and Plan fit ${width}px without sideways scrolling`, async ({ page }) => {
    await page.setViewportSize({ width, height: width < 700 ? 844 : width < 1000 ? 1024 : 900 });
    for (const route of ["#/today", "#/plan"]) {
      await page.goto(`/${route}`);
      await expect(page.locator("main#main")).toBeVisible();
      await page.waitForTimeout(500);
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
      expect(overflow, route).toBeLessThanOrEqual(0);
      await page.screenshot({ path: `../.cache/screenshots/${route.slice(2)}-${width}.png`, fullPage: true });
    }
  });
}

for (const width of [390, 768]) {
  test(`at ${width}px the plan windows are cards, not a sideways table`, async ({ page }) => {
    await page.setViewportSize({ width, height: width < 700 ? 844 : 1024 });
    await page.goto("/#/plan");
    await expect(page.locator(".next-card").first()).toBeVisible();
    // Neither What happened nor What's next hides its cost or meter columns behind a sideways scroll.
    await expect(page.locator("table.plan-table")).toHaveCount(0);
  });
}

test("on a phone a What happened window expands to its half-hours", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/#/plan");
  const happened = page
    .locator("section")
    .filter({ has: page.getByRole("heading", { name: "What happened (last 24 h)" }) });
  const first = happened
    .locator(".next-card")
    .filter({ has: page.getByRole("button", { name: /^Show \d+ half-hours$/ }) })
    .first();
  // Found again by its time, since its button stops saying "Show" once it's open.
  const time = await first.locator(".next-card-time").first().innerText();
  const card = happened
    .locator(".next-card")
    .filter({ has: page.getByText(time, { exact: true }) })
    .first();
  const button = card.getByRole("button", { name: /^Show \d+ half-hours$/ });
  const count = Number((await button.innerText()).match(/\d+/)![0]);
  await button.click();
  await expect(card.getByRole("button", { name: /^Hide \d+ half-hours$/ })).toHaveAttribute("aria-expanded", "true");
  await expect(card.locator(".next-card-slots li")).toHaveCount(count);
  await expect(card.locator(".next-card-slots li").first()).toContainText(/Ended \d+%/);
});

test("misses against the plan read as points, with a spoken label", async ({ page }) => {
  // The demo battery follows its plan closely; measure it 20 points low so every finished window has a miss to show.
  await page.route("**/api/plans/timeline?*", async (route) => {
    const response = await fetchFresh(route);
    const timeline = await response.json();
    for (const slot of timeline.slots)
      for (const key of ["socActual", "socActualStart"])
        if (typeof slot[key] === "number") slot[key] = Math.max(0, slot[key] - 20);
    await fulfillRewritten(route, response, timeline);
  });
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto("/#/plan");
  const table = page.getByRole("region", { name: "What happened, window by window" });
  const pill = table.locator(".delta-pill").first();
  await expect(pill).toHaveText(/^[+−]\d+ pts$/);
  await expect(pill).toHaveAttribute("aria-label", /^Ended \d+ points? (below|above) plan$/);
  // The energy columns say their unit once, in the header.
  await expect(table.locator("thead")).toContainText("kWh");
});

test("an earlier plan keeps its stepper and picker in the banner at the top", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto("/#/plan");
  await page.getByText("Browse earlier plans", { exact: true }).click();
  const browse = page.locator(".plan-compare");
  // The day starts as today.
  await expect(browse.getByLabel("Snapshot generation date")).not.toHaveValue("");
  await browse.getByRole("button", { name: /Earlier/ }).click();
  const banner = page.getByRole("region", { name: "Earlier plan" });
  await expect(banner.getByRole("status")).toContainText(/Showing the plan Predbat made (Today|Yesterday)/);
  // The closed picker reads as a day and a time.
  const picker = banner.getByLabel("Plan snapshot", { exact: true });
  await expect(picker.locator("option:checked")).toHaveText(/^(Today|Yesterday) \d\d:\d\d$/);
  await expect(banner.getByRole("button", { name: /Later|Latest/ })).toBeEnabled();
  // The browse disclosure and the live status strip step aside while an earlier plan is on screen.
  await expect(page.locator(".plan-compare")).toHaveCount(0);
  await expect(page.getByRole("region", { name: "Predbat status" })).toHaveCount(0);
  await banner.getByRole("button", { name: "Back to the latest plan" }).click();
  await expect(page.getByRole("region", { name: "Predbat status" })).toBeVisible();
});
