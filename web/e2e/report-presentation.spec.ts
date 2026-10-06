import { test, expect, type APIRequestContext, type Page } from "@playwright/test";
import { todaySummary } from "./support/live-5oct";

/**
 * Energy › Saved reports. A report stores only its period; opening it asks the server to recompute it. Titles say what the
 * period is in the household's time zone, empty periods are left out, related AI checks are listed by title, and opening a
 * report is reading it (one read state for the report and its notification).
 */
async function reportFixture(page: Page, request: APIRequestContext, reports: unknown[]) {
  const payload = await (await request.get("/api/state")).json();
  payload.state.reports = reports;
  payload.state.notifications = [];
  payload.state.reportPreferences = {
    dailyEnabled: true,
    weeklyEnabled: false,
    timeZone: "Europe/London",
    hourLocal: 7,
    defaultsVersion: 1,
  };
  await page.route("**/api/state", (route) => route.fulfill({ json: payload }));
  return payload;
}
const report = (id: string, from: string, to: string, extra: Record<string, unknown> = {}) => ({
  id,
  kind: "Daily",
  timeZone: "Europe/London",
  from,
  to,
  createdAt: to,
  title: `Server title ${id}`,
  summary: "You used 61.1 kWh. Standing charges aren't included.",
  isDemo: false,
  readAt: "2026-10-05T08:00:00Z",
  energySummary: null,
  days: [],
  investigationIds: [],
  textVersion: 2,
  ...extra,
});

test("saved reports read plainly, newest first, without empty periods", async ({ page, request }) => {
  await page.clock.setFixedTime(new Date("2026-10-05T09:57:00Z"));
  await reportFixture(page, request, [
    report("empty", "2026-09-30T23:00:00Z", "2026-10-01T23:00:00Z", { summary: "No meter readings for this period." }),
    report("sat", "2026-10-02T23:00:00Z", "2026-10-03T23:00:00Z"),
    report("legacy-part", "2026-10-01T23:00:00Z", "2026-10-02T20:14:30Z", {
      title: "Daily energy report · 2 Oct 2026",
      textVersion: 0,
    }),
    report("today", "2026-10-04T23:00:00Z", "2026-10-05T08:53:21Z", { readAt: null }),
    report("week", "2026-09-27T23:00:00Z", "2026-10-04T23:00:00Z", { kind: "Weekly" }),
  ]);
  await page.goto("/#/energy/reports");
  const list = page.locator(".report-list > li");
  await expect(list.locator("summary strong")).toHaveText([
    "Report · Mon 5 Oct, to 09:53",
    "Weekly report · 28 Sep – 4 Oct",
    "Daily report · Sat 3 Oct",
    "Report · Fri 2 Oct, to 21:14",
  ]);
  await expect(page.locator(".saved-reports-head")).toContainText("A daily report for yesterday arrives at 07:00.");
  await expect(page.locator(".saved-reports-head .report-count")).toHaveText("1 new");
  await expect(list.first()).toContainText("New");
});

test("opening a report recomputes it, lists related checks by title and marks it read once", async ({
  page,
  request,
}) => {
  const payload = await reportFixture(page, request, [
    report("r1", "2026-10-03T23:00:00Z", "2026-10-04T23:00:00Z", { readAt: null, investigationIds: ["a", "b"] }),
  ]);
  const summary = todaySummary("2026-10-03T23:00:00Z", "2026-10-04T23:00:00Z");
  const views: string[] = [];
  await page.route("**/api/reports/r1", (route) => {
    views.push(route.request().method());
    return route.fulfill({
      json: {
        ...payload.state.reports[0],
        partial: false,
        hasReadings: true,
        summary: "You used 12.3 kWh. Predbat sat in `FrzExp` from 2026-10-05T01:00:00Z. Net cost £1.10.",
        energySummary: summary,
        days: [summary],
        relatedChecks: [
          { id: "a", at: "2026-10-04T06:10:00Z", title: "Why export stopped at 01:05" },
          { id: "b", at: "2026-10-04T18:00:00Z", title: "Evening load ran above forecast" },
        ],
      },
    });
  });
  const reads: string[] = [];
  await page.route("**/api/reports/r1/read", (route) => {
    reads.push(route.request().method());
    return route.fulfill({ json: { ok: true } });
  });
  await page.goto("/#/energy/reports");
  const item = page.locator("#report-r1");
  await item.locator("summary").click();
  // The server's text through PlainText: codes become labels, UTC becomes local time, no backticks.
  const text = item.locator(".report-summary");
  await expect(text).toContainText("You used 12.3 kWh.");
  await expect(text).not.toContainText("`");
  await expect(text).not.toContainText("FrzExp");
  await expect(text).not.toContainText("Z.");
  await expect(item.locator(".report-figures")).toContainText("£1.10");
  await expect(item.locator(".report-figures")).toContainText("Paid £1.80 · Earned £0.70");
  const checks = item.locator("details.report-checks");
  await expect(checks.locator("summary")).toContainText("Related AI checks");
  await expect(checks.locator(".disclosure-count")).toHaveText("2");
  await checks.locator("summary").click();
  await expect(checks.getByRole("link", { name: "Why export stopped at 01:05" })).toHaveAttribute(
    "href",
    "#/insights/inv/a",
  );
  await expect.poll(() => reads).toEqual(["POST"]);
  expect(views).toEqual(["GET"]);
  // "Open these days in the chart" moves the figures to the report's own days.
  await item.getByRole("button", { name: "Open these days in the chart" }).click();
  await expect(page).toHaveURL(/#\/energy\?from=2026-10-04&to=2026-10-04$/);
});

test("report titles keep London midnight in a UTC browser, and a bell link opens its report", async ({
  browser,
  request,
}) => {
  const context = await browser.newContext({ timezoneId: "UTC" });
  const page = await context.newPage();
  try {
    await reportFixture(page, request, [report("london", "2026-10-01T23:00:00Z", "2026-10-02T23:00:00Z")]);
    await page.route("**/api/reports/london", (route) =>
      route.fulfill({
        json: {
          ...report("london", "2026-10-01T23:00:00Z", "2026-10-02T23:00:00Z"),
          partial: false,
          hasReadings: false,
          energySummary: null,
          relatedChecks: [],
        },
      }),
    );
    await page.goto("/#/energy/reports?report=london");
    const item = page.locator("#report-london");
    // In UTC the same range would read "Thu 1 Oct".
    await expect(item.locator("summary strong")).toHaveText("Daily report · Fri 2 Oct");
    await expect(item.locator("details")).toHaveAttribute("open", "");
    await expect(item.locator(".report-summary")).toContainText("You used 61.1 kWh.");
  } finally {
    await context.close();
  }
});
