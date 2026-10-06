import { test, expect, type Page } from "@playwright/test";
import { openPage } from "./support/navigation";

// A route handler still reading a fetched response when the test ends would fail it as the context closes.
test.afterEach(async ({ page }) => page.unrouteAll({ behavior: "ignoreErrors" }));

// The app shell: routing, focus and titles, the status chip, notifications, resilience when the server is slow or
// away, the session-expired screen and error boundaries.
test.use({ timezoneId: "Europe/London" });

const mainNav = (page: Page) => page.getByRole("navigation", { name: "Main navigation" });

/** Serves /api/state with a change applied (the server's own reply, edited). */
async function withState(page: Page, change: (payload: any) => void) {
  await page.route("**/api/state", async (route) => {
    const response = await route.fetch({ headers: { ...route.request().headers(), "if-none-match": "" } });
    const payload = await response.json();
    change(payload);
    await route.fulfill({ json: payload });
  });
}

test("a check that didn't finish shows on Today and Insights only, with when it tries again", async ({ page }) => {
  const retry = new Date(Date.now() + 90 * 60000);
  await withState(page, (p) => {
    p.state.analysisError = "The AI provider was busy.";
    p.state.lastAnalysis = new Date(Date.now() - 2 * 86400000).toISOString();
    p.ai.running = false;
    p.ai.schedule = { ...(p.ai.schedule ?? {}), nextRunAt: retry.toISOString() };
  });
  await page.goto("/#/today");
  const alert = page.locator(".alerts").getByRole("alert").filter({ hasText: "The last AI check didn't finish" });
  await expect(alert).toContainText(/The AI provider was busy\. It will try again (tomorrow )?at \d\d:\d\d\./);
  await page.goto("/#/plan");
  await expect(page.getByRole("heading", { level: 1, name: "Plan" })).toBeVisible();
  await expect(alert).toHaveCount(0);
  await page.goto("/#/setup");
  await expect(alert).toHaveCount(0);
});

test("a later completed check clears the old failure, and a running check is one quiet row with Stop", async ({
  page,
}) => {
  await withState(page, (p) => {
    p.state.analysisError = "The AI provider was busy.";
    // Completed after every failed check on record.
    p.state.lastAnalysis = new Date(Date.now() + 60000).toISOString();
    p.ai.running = true;
  });
  await page.route("**/api/investigations/cancel", (r) => r.fulfill({ status: 204 }));
  await page.goto("/#/plan");
  await expect(page.getByRole("heading", { level: 1, name: "Plan" })).toBeVisible();
  await expect(page.getByText("The last AI check didn't finish")).toHaveCount(0);
  const row = page.locator(".check-running");
  await expect(row).toContainText("An AI check is running");
  await expect(row.getByRole("link", { name: "An AI check is running" })).toHaveAttribute("href", "#/insights");
  await expect(row.getByRole("button", { name: "Stop" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Stop check" })).toHaveCount(0);
  // Setup › AI checks has its own running state.
  await page.goto("/#/setup/ai");
  await expect(page.getByRole("heading", { level: 1, name: "Setup" })).toBeVisible();
  await expect(page.locator(".check-running")).toHaveCount(0);
  // Phones keep it in the header bar.
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/#/energy");
  await expect(page.locator(".check-running")).toBeHidden();
  await expect(page.getByRole("button", { name: "Stop the AI check" })).toBeVisible();
});

test("on a phone the cog marks Setup as current and the current tab is scrolled into view", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/#/setup/about");
  const cog = page.getByRole("banner").getByRole("link", { name: "Setup" });
  await expect(cog).toHaveAttribute("aria-current", "page");
  const tabs = page.getByRole("navigation", { name: "Setup sections" });
  const about = tabs.getByRole("link", { name: "About" });
  await expect(about).toBeInViewport({ ratio: 1 });
  // More tabs off to the left: that edge fades.
  await expect(tabs).toHaveAttribute("data-fade", /start|both/);
  const color = await cog.evaluate((el) => getComputedStyle(el).backgroundColor);
  expect(color).not.toBe("rgba(0, 0, 0, 0)");
});

test("the phone status chip says one word when something needs saying", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/");
  const chip = page.getByRole("button", { name: /^Status: Demo/ });
  await expect(chip).toHaveText("Demo", { useInnerText: true });
  await page.setViewportSize({ width: 768, height: 1024 });
  await expect(chip.locator(".status-label")).toBeVisible();
});

test("Back walks back through pages and a reload keeps the page", async ({ page }) => {
  await page.goto("/");
  await expect(page).toHaveURL(/#\/today$/);
  await expect(page).toHaveTitle("Today · Joule");
  await mainNav(page).getByRole("link", { name: /^Plan/ }).click();
  await expect(page).toHaveURL(/#\/plan$/);
  await expect(page).toHaveTitle("Plan · Joule");
  // Focus moves to the new page's title and the change is announced.
  await expect(page.getByRole("heading", { level: 1, name: "Plan", exact: true })).toBeFocused();
  await expect(page.locator('[aria-live="polite"].sr-only')).toHaveText("Plan page");
  await mainNav(page)
    .getByRole("link", { name: /^Insights/ })
    .click();
  await expect(page).toHaveURL(/#\/insights$/);
  await expect(page.getByRole("heading", { level: 1, name: "Insights", exact: true })).toBeFocused();
  await page.goBack();
  await expect(page).toHaveURL(/#\/plan$/);
  await expect(page.getByRole("heading", { level: 1, name: "Plan", exact: true })).toBeVisible();
  await page.goBack();
  await expect(page).toHaveURL(/#\/today$/);
  await expect(page.getByRole("heading", { level: 1, name: "Today", exact: true })).toBeVisible();
  await page.goto("/#/plan");
  await page.reload();
  await expect(page.getByRole("heading", { level: 1, name: "Plan", exact: true })).toBeVisible();
  await expect(mainNav(page).getByRole("link", { name: /^Plan/ })).toHaveAttribute("aria-current", "page");
});

test("old page names and sections have their own addresses", async ({ page }) => {
  await page.goto("/#/recommendations");
  await expect(page).toHaveURL(/#\/insights\/suggestions$/);
  await expect(
    page.getByRole("navigation", { name: "Insights sections" }).getByRole("link", { name: /^Suggestions/ }),
  ).toHaveAttribute("aria-current", "page");
  await expect(page).toHaveTitle("Suggestions · Insights · Joule");
  await page.goto("/#/history");
  await expect(page).toHaveURL(/#\/setup\/changes$/);
  await page.goto("/#/data");
  await expect(page).toHaveURL(/#\/energy$/);
  await page.goto("/#/setup/reports");
  await expect(page).toHaveURL(/#\/energy\/reports$/);
  for (const name of ["Settings", "AI checks", "Sensors", "Files", "Changes", "About", "Reports", "Trials"])
    await openPage(page, name);
});

test("a skip link jumps past the navigation, and the Insights count is spoken", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByRole("heading", { level: 1, name: "Today" })).toBeVisible();
  await page.keyboard.press("Tab");
  const skip = page.getByRole("link", { name: "Skip to main content" });
  await expect(skip).toBeFocused();
  await expect(skip).toBeInViewport();
  await page.keyboard.press("Enter");
  await expect(page.locator("main#main")).toBeFocused();
  await expect(page).toHaveURL(/#\/today$/);
  const insights = mainNav(page).getByRole("link", { name: /^Insights/ });
  await expect(insights).toHaveAccessibleName(/^Insights, \d+ waiting for you$/);
  await expect(mainNav(page).getByRole("link", { name: "Today" })).toHaveAttribute("aria-current", "page");
});

test("one status chip explains the connection, and the bell links to what needs you", async ({ page }) => {
  await page.goto("/");
  const chip = page.getByRole("button", { name: /^Status: Demo/ });
  await chip.click();
  const popover = page.getByRole("dialog", { name: "Connection status" });
  for (const part of ["Joule", "Predbat", "Home Assistant", "Changes to Predbat", "AI"])
    await expect(popover.getByText(part, { exact: true })).toBeVisible();
  await expect(popover.getByRole("link", { name: "Connect my Predbat" })).toHaveAttribute("href", "#/setup");
  await page.keyboard.press("Escape");
  await expect(popover).toHaveCount(0);
  await expect(chip).toBeFocused();
  // No page carries its own mode badge or a DEMO/LIVE banner any more.
  await expect(page.getByText(/^AI: /)).toHaveCount(0);
  await expect(page.getByText("DEMO", { exact: true })).toHaveCount(0);
  const bell = page.getByRole("button", { name: /^Notifications/ });
  await bell.click();
  const list = page.getByRole("dialog", { name: "Notifications" });
  const suggestion = list.getByRole("link", { name: /^Needs you \(\d+\)/ });
  await expect(suggestion).toHaveAttribute("href", "#/insights/suggestions");
  await suggestion.click();
  await expect(page).toHaveURL(/#\/insights\/suggestions$/);
  await expect(list).toHaveCount(0);
});

test("a slow server still updates the screen, one request at a time", async ({ page, request }) => {
  const payload = await (await request.get("/api/state")).json();
  payload.ai.running = true; // polls every 2.5 s, the case that froze before
  let inFlight = 0,
    maxInFlight = 0,
    delay = 0;
  await page.route("**/api/investigations/cancel", (r) => r.fulfill({ status: 204 }));
  await page.route("**/api/state", async (route) => {
    inFlight++;
    maxInFlight = Math.max(maxInFlight, inFlight);
    const snapshot = JSON.parse(JSON.stringify(payload));
    await new Promise((resolve) => setTimeout(resolve, delay));
    inFlight--;
    await route.fulfill({ json: snapshot }).catch(() => {});
  });
  await page.goto("/#/setup/settings");
  await expect(page.getByText(`Settings version ${payload.state.revision}`, { exact: true })).toBeVisible();
  // Every response now takes three times the polling interval.
  delay = 7500;
  maxInFlight = inFlight; // development mode mounts twice at start-up; count from here
  payload.state.revision = 4242;
  await expect(page.getByText("Settings version 4242", { exact: true })).toBeVisible({ timeout: 20000 });
  expect(maxInFlight).toBe(1);
  await page.unrouteAll({ behavior: "ignoreErrors" });
});

test("when Joule can't be reached the figures stay and one banner says so", async ({ page }) => {
  const errors: string[] = [];
  page.on("pageerror", (e) => errors.push(e.message));
  await page.goto("/");
  const cost = page.getByRole("article", { name: /^Net (cost|earnings) today$/ }).locator(".stat-value");
  await expect(cost).toHaveText(/£/);
  const before = await cost.textContent();
  await page.route("**/api/**", (route) => route.abort("internetdisconnected"));
  const banner = page.locator(".alerts").getByRole("alert").filter({ hasText: "Can't reach Joule" });
  await expect(banner).toBeVisible({ timeout: 15000 });
  await expect(banner).toContainText(/Showing data from Today \d{2}:\d{2}/);
  await expect(cost).toHaveText(before!);
  // The browser's own wording never reaches the screen.
  await expect(page.getByText(/Failed to fetch/)).toHaveCount(0);
  await expect(page.getByRole("button", { name: /^Status: Joule offline/ })).toBeVisible();
  await page.unrouteAll({ behavior: "ignoreErrors" });
  await banner.getByRole("button", { name: "Try again" }).click();
  await expect(banner).toHaveCount(0);
  expect(errors).toEqual([]);
});

test("the first screen names the server and keeps trying", async ({ page }) => {
  await page.route("**/api/state", (route) => route.abort("connectionrefused"));
  await page.goto("/");
  await expect(page.getByRole("alert")).toContainText(`Can't reach Joule at ${new URL(page.url()).host}`);
  // On this loopback test server the port hint is shown; it would not be on a remote address.
  await expect(page.getByText(/Is the Joule service running\?/)).toBeVisible();
  await expect(page.getByText(/Failed to fetch/)).toHaveCount(0);
  await page.unrouteAll({ behavior: "ignoreErrors" });
  await page.getByRole("button", { name: "Try again" }).click();
  await expect(page.getByRole("heading", { level: 1, name: "Today" })).toBeVisible();
});

test("a gateway answering while Joule restarts reads as offline, never as stale data", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByRole("heading", { level: 1, name: "Today" })).toBeVisible();
  // nginx's own error page during a deploy restart.
  await page.route("**/api/**", (route) =>
    route.fulfill({ status: 502, contentType: "text/html", body: "<html><body>502 Bad Gateway</body></html>" }),
  );
  const banner = page.locator(".alerts").getByRole("alert").filter({ hasText: "Joule isn't answering" });
  await expect(banner).toBeVisible({ timeout: 15000 });
  await expect(banner).toContainText("It may be restarting.");
  await expect(page.getByRole("button", { name: /^Status: Joule offline/ })).toBeVisible();
  // Not mistaken for an expired sign-in.
  await expect(page.getByText(/Sign in again/)).toHaveCount(0);
  await page.unrouteAll({ behavior: "ignoreErrors" });
  await banner.getByRole("button", { name: "Try again" }).click();
  await expect(banner).toHaveCount(0);
});

test("the first screen says Joule isn't answering when a proxy answers 500 for it", async ({ page }) => {
  await page.route("**/api/state", (route) => route.fulfill({ status: 500, body: "" }));
  await page.goto("/");
  await expect(page.getByRole("alert")).toHaveText(
    "Joule isn't answering right now. It may be restarting; it keeps trying on its own.",
  );
  await expect(page.getByText(/error 500/)).toHaveCount(0);
});

test("an expired sign-in asks you to sign in again instead of failing quietly", async ({ page }) => {
  await page.route("**/api/state", (route) =>
    route.fulfill({ status: 302, headers: { location: "https://login.example.test/oauth2/start" } }),
  );
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Sign in again" })).toBeVisible();
  await expect(page.getByRole("alert")).toContainText("Your session has expired");
  await page.unrouteAll();
  await page.route("**/api/state", (route) =>
    route.fulfill({ status: 200, contentType: "text/html", body: "<!doctype html><title>Login</title>" }),
  );
  await page.reload();
  await expect(page.getByRole("heading", { name: "Sign in again" })).toBeVisible();
});

test("one malformed record shows a contained error, not a blank app", async ({ page, request }) => {
  const payload = await (await request.get("/api/state")).json();
  // Today's top finding with a summary that isn't text.
  payload.state.investigations = [
    {
      ...payload.state.investigations[0],
      at: new Date().toISOString(),
      status: "Completed",
      verdict: "problem",
      dismissedAt: null,
      repeatOf: null,
      impactPence: 999,
      plain: null,
      summary: { malformed: true },
    },
  ];
  await page.route("**/api/state", (route) => route.fulfill({ json: payload }));
  await page.goto("/");
  const boundary = page.getByRole("alert").filter({ hasText: "Today couldn't be shown" });
  await expect(boundary).toBeVisible();
  await expect(boundary.getByRole("button", { name: "Try again" })).toBeVisible();
  await expect(boundary.getByRole("button", { name: "Copy details" })).toBeVisible();
  // The rest of Joule still works.
  await mainNav(page).getByRole("link", { name: /^Plan/ }).click();
  await expect(page.getByRole("heading", { level: 1, name: "Plan", exact: true })).toBeVisible();
  await expect(boundary).toHaveCount(0);
});

test("the welcome notice shows once and the footer carries the small print", async ({ page }) => {
  await page.goto("/");
  const welcome = page.getByRole("complementary", { name: "Before you start" });
  await expect(welcome).toContainText("AI suggestions can be wrong. Nothing changes without your approval.");
  await welcome.getByRole("button", { name: "Got it" }).click();
  await expect(welcome).toHaveCount(0);
  await page.reload();
  await expect(page.getByRole("heading", { level: 1, name: "Today" })).toBeVisible();
  await expect(page.getByRole("complementary", { name: "Before you start" })).toHaveCount(0);
  const footer = page.locator("footer.site-footer");
  await expect(footer).toContainText(/Joule \d+\.\d+/);
  await expect(footer).toContainText("not affiliated");
  await expect(footer.getByRole("link", { name: "Report a problem (opens in a new tab)" })).toBeVisible();
  await footer.getByRole("link", { name: "About" }).click();
  await expect(page).toHaveURL(/#\/setup\/about$/);
  await expect(page.getByText("Joule version", { exact: true })).toBeVisible();
});

test("one Refresh now updates Predbat and the sensors together", async ({ page }) => {
  const calls: string[] = [];
  await page.route("**/api/collect", (r) => {
    calls.push("collect");
    return r.continue();
  });
  await page.route("**/api/telemetry/collect", (r) => {
    calls.push("telemetry");
    return r.fulfill({ status: 503, json: { error: "Home Assistant timed out" } });
  });
  await page.goto("/");
  await page.getByRole("button", { name: "Refresh now" }).click();
  await expect.poll(() => calls.sort().join(",")).toBe("collect,telemetry");
  await expect(page.getByRole("status").filter({ hasText: "Plan updated" })).toContainText(
    "couldn't read Home Assistant: Home Assistant timed out.",
  );
});

test("the battery chart is visible on a phone and nothing scrolls sideways at 320px", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/");
  // The timeline's battery lane (the separate battery outlook panel is gone) is drawn on phones too.
  const timeline = page.getByRole("region", { name: "Today and the plan ahead" });
  await expect(timeline.locator("svg.tl-svg").first()).toBeVisible();
  await expect(timeline.locator("svg.tl-svg").first()).toContainText("Battery %");
  await page.setViewportSize({ width: 320, height: 640 });
  for (const hash of [
    "#/today",
    "#/plan",
    "#/insights",
    "#/insights/suggestions",
    "#/energy",
    "#/energy/reports",
    "#/setup",
    "#/setup/settings",
    "#/setup/ai",
  ]) {
    await page.goto(`/${hash}`);
    await expect(page.locator("main#main")).toBeVisible();
    await page.waitForTimeout(300);
    expect(await page.evaluate(() => document.documentElement.scrollWidth), `${hash} at 320px`).toBeLessThanOrEqual(
      320,
    );
  }
});

test("a hint stays open while the pointer is on it, and a dialog returns focus to its opener", async ({ page }) => {
  await page.goto("/#/kit");
  const hint = page.getByRole("button", { name: "What does export freeze mean?" });
  await hint.hover();
  const bubble = page.getByRole("tooltip");
  await expect(bubble).toBeVisible();
  await expect(hint).toHaveAccessibleDescription(/spare solar is exported/);
  await bubble.hover();
  await page.waitForTimeout(400);
  await expect(bubble).toBeVisible();
  await page.mouse.move(0, 0);
  await expect(bubble).toHaveCount(0);
  const opener = page.getByRole("button", { name: "Open a dialog" });
  await opener.click();
  await expect(page.getByRole("dialog", { name: "Review suggestion" })).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(opener).toBeFocused();
});
