import { test, expect, type Page } from "@playwright/test";
import { MIDNIGHT, todaySummary, useLive5Oct } from "./support/live-5oct";
import { openPage } from "./support/navigation";

/**
 * The Energy page: one calm page for what the meters recorded, the sensors' health and the saved reports. Checked with the
 * live-shaped 5 Oct snapshot at phone and desktop widths, and on the demo for the period presets.
 */
test.use({ timezoneId: "Europe/London" });

const noOverflow = (page: Page) => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth);

for (const [width, height] of [
  [1440, 900],
  [390, 844],
]) {
  test(`live-shaped Energy page reads plainly at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height });
    await useLive5Oct(page);
    await page.goto("/#/energy?period=today");
    await expect(page.locator(".energy-chips")).toBeVisible();
    const main = page.locator("main#main");
    // No schema dump, engine caveats or hedges repeated per tile.
    for (const text of [
      "telemetry_samples",
      "observedNetCostGbp",
      "Not compared",
      "Totals only count",
      "is exactly 'observed'",
    ])
      await expect(main).not.toContainText(text);
    await expect(main.getByText(/^Compared with this time yesterday$/)).toHaveCount(1);
    // Changes read in words, never a bare arrow: money is better or worse, energy more or less.
    for (const text of await main.locator(".energy-change").allTextContents()) expect(text).not.toMatch(/[▲▼]/);
    for (const word of await main.locator(".energy-change-word").allTextContents())
      expect(word).toMatch(/^(better|worse|more|less)$/);
    // The explainer: four plain bullets (a fifth about the car when the house meter includes it), installer detail folded away.
    const how = page.locator("details.energy-how");
    await how.locator(":scope > summary").click();
    await expect(how.locator(".energy-how-list > li")).toHaveCount(5);
    await expect(how).toContainText("Standing charges aren’t included.");
    await expect(how.locator("details.energy-installers")).not.toHaveAttribute("open", "");
    // The sensors are one line when all is well, and at most two screens with every row open on a phone.
    const sensors = page.locator("section.sensor-health");
    await expect(sensors).toContainText(/All 11 sensors OK · checked \d+ min ago/);
    expect((await sensors.boundingBox())!.height).toBeLessThan(160);
    await sensors.getByRole("button", { name: "Show all 11" }).click();
    expect((await sensors.boundingBox())!.height).toBeLessThanOrEqual(2 * height);
    // The house meter is never called "Home use": that figure leaves out the car and covers the period.
    await expect(sensors.locator(".sensor-name").getByText("Home use", { exact: true })).toHaveCount(0);
    await expect(sensors.locator(".sensor-name").getByText(/^(House meter|Home use incl\. car)$/)).toHaveCount(1);
    const ev = sensors.locator(".sensor-row").filter({ hasText: "Car charging" });
    await expect(ev).toContainText("Idle");
    await ev.locator("summary").click();
    await expect(ev).toContainText("Not charging (the charger reports unknown between sessions).");
    await expect(sensors.locator(".sensor-row").filter({ hasText: "Octopus smart-charge slots" })).toContainText(
      "None now",
    );
    expect(await noOverflow(page)).toBe(true);
    await page.screenshot({ path: `../.cache/screenshots/energy-live-${width}.png`, fullPage: true });
  });
}

test("an outage names the meter and hours once; a figure missing hours is a floor, not ≈", async ({ page }) => {
  await useLive5Oct(page);
  const gap = { from: MIDNIGHT, to: "2026-10-05T01:47:00Z", reason: "offline", knownKwh: 0.62 };
  await page.route("**/api/telemetry/summary?*", (route) => {
    const s = todaySummary(MIDNIGHT, new URL(route.request().url()).searchParams.get("to")!);
    s.metrics.grid_export = {
      ...s.metrics.grid_export,
      state: "partial",
      gaps: [gap],
      reconciled: false,
      energyKwh: 5.71,
    };
    return route.fulfill({ json: { ...s, exportCostCoverage: 0.8 } });
  });
  await page.goto("/#/energy?period=today");
  const net = page.getByRole("article", { name: "Net cost", exact: true });
  await expect(net.locator(".stat-value")).toHaveText("≈ £1.10");
  await expect(net).toContainText("Export meter offline 00:00–02:47");
  const chip = page.getByRole("listitem", { name: "Grid export", exact: true });
  await expect(chip.locator(".energy-chip-value")).toHaveText("5.7 kWh");
  await expect(chip).toContainText("Missing 00:00–02:47");
  await expect(chip).toContainText("Meter says 6.3 kWh");
  await expect(page.locator(".energy-notes")).toHaveText(
    "Export meter offline 00:00–02:47 · 0.62 kWh in that time isn't counted.",
  );
  await expect(page.locator(".energy-match")).toHaveCount(0);
});

test("the house meter's own total and missing hours sit under Home use when it doesn't match", async ({ page }) => {
  await useLive5Oct(page);
  const gap = { from: "2026-10-05T03:00:00Z", to: "2026-10-05T05:30:00Z", reason: "offline" };
  await page.route("**/api/telemetry/summary?*", (route) => {
    const s = todaySummary(MIDNIGHT, new URL(route.request().url()).searchParams.get("to")!);
    s.metrics.load = { ...s.metrics.load, state: "partial", gaps: [gap], reconciled: false, energyKwh: 10.9 };
    s.home = { ...s.home, state: "partial", energyKwh: 6.3 };
    return route.fulfill({ json: s });
  });
  await page.goto("/#/energy?period=today");
  const home = page.getByRole("article", { name: "Home use", exact: true });
  await expect(home.locator(".stat-value")).toHaveText("6.3kWh");
  await expect(home.locator(".energy-hero-note")).toHaveText(["Missing 04:00–06:30", "House meter says 12.3 kWh"]);
  await page.locator("details.energy-how > summary").click();
  await expect(page.locator(".energy-how-list")).toContainText("Longer outages are left out, never guessed");
  await expect(page.locator(".energy-how-list")).not.toContainText("counted when it comes back");
});

test("a custom range in the address bar that can't be shown says why once, and loads nothing", async ({ page }) => {
  await useLive5Oct(page);
  const asked: string[] = [];
  page.on("request", (r) => {
    if (r.url().includes("/api/telemetry/summary")) asked.push(r.url());
  });
  await page.goto("/#/energy?from=2026-10-06&to=2026-10-07");
  const figures = page.locator("section.energy-figures");
  await expect(figures.getByRole("alert")).toHaveText("That's in the future: today is 5 Oct.");
  await expect(figures.getByRole("button", { name: /Try again|Retry/ })).toHaveCount(0);
  await expect(figures).not.toContainText("Couldn't load");
  await expect(figures).not.toContainText("Choose a positive");
  await expect(figures.locator(".energy-asof")).not.toContainText("Oct");
  await expect(figures.getByRole("button", { name: "Save as report" })).toHaveCount(0);
  await page.goto("/#/energy?from=2026-10-04&to=2026-10-02");
  await expect(figures.getByRole("alert")).toHaveText("The start date is after the end date.");
  // Other parts of the app read today's figures; nothing asks for the backwards or future window.
  const bad = asked.filter((u) => {
    const q = new URL(u).searchParams;
    return Date.parse(q.get("from")!) >= Date.parse(q.get("to")!) || q.get("from")! >= "2026-10-05T23";
  });
  expect(bad).toEqual([]);
  // Fixing the dates loads the figures.
  await page.getByLabel("Energy to date").fill("2026-10-05");
  await expect(figures.locator(".energy-chips")).toBeVisible();
  await expect(figures.getByRole("alert")).toHaveCount(0);
});

test("presets load straight away, live in the address bar and keep the old figures while the next load", async ({
  page,
}) => {
  await page.goto("/");
  await openPage(page, "Energy");
  const period = page.getByRole("group", { name: "Period" });
  // 7 days by default, clamped to when records began, with one line saying there is nothing earlier to compare with.
  await expect(period.getByRole("button", { name: "7 days" })).toHaveAttribute("aria-pressed", "true");
  await expect(page.locator("figure.chart-daily-energy")).toBeVisible();
  let release: () => void = () => {};
  const held = new Promise<void>((r) => (release = r));
  await page.route("**/api/telemetry/summary?*", async (route) => {
    await held;
    await route.continue();
  });
  const request = page.waitForRequest((r) => r.url().includes("/api/telemetry/summary?"));
  await period.getByRole("button", { name: "Yesterday" }).click();
  await request;
  await expect(page).toHaveURL(/#\/energy\?period=yesterday$/);
  // While the request is held, the previous figures stay on screen, dimmed.
  await expect(page.locator(".energy-body.is-stale .energy-chips")).toBeVisible();
  release();
  await expect(page.locator(".energy-body.is-stale")).toHaveCount(0);
  await expect(page.locator("figure.tl-day")).toBeVisible();
  await expect(page.locator(".energy-asof")).not.toContainText("up to");
  // A refresh keeps the period.
  await page.reload();
  await expect(page.getByRole("group", { name: "Period" }).getByRole("button", { name: "Yesterday" })).toHaveAttribute(
    "aria-pressed",
    "true",
  );
});

test("Energy is one page: no Measured/Reports tabs, and #/energy/reports lands on the saved reports", async ({
  page,
}) => {
  await page.goto("/#/energy/reports");
  await expect(page.getByRole("heading", { level: 1, name: "Energy" })).toBeVisible();
  await expect(page.getByRole("navigation", { name: "Energy sections" })).toHaveCount(0);
  await expect(page.getByRole("heading", { name: "Saved reports" })).toBeInViewport();
  expect(await noOverflow(page)).toBe(true);
});

test("failed meter requests read as errors with Try again, never as loading that doesn't end", async ({ page }) => {
  await page.route("**/api/telemetry/**", (route) => route.fulfill({ status: 500, body: "boom" }));
  await page.goto("/#/energy");
  const figures = page.locator("section.energy-figures");
  await expect(figures.getByText("Couldn't load your meter readings")).toBeVisible({ timeout: 15000 });
  await expect(figures.getByRole("button", { name: "Try again" })).toBeVisible();
  const sensors = page.locator("section.sensor-health");
  await expect(sensors).toContainText("Couldn't read the sensors");
  await expect(sensors.getByRole("button", { name: "Try again" })).toBeVisible();
  await expect(sensors).not.toContainText("Checking sensors");
});

test("a clipped period says how many days are recorded, and the Custom From box agrees", async ({ page }) => {
  const today = new Date().toISOString().slice(0, 10);
  await page.goto(`/#/energy?from=2026-01-01&to=${today}`);
  await expect(page.locator(".energy-chips")).toBeVisible();
  const note = page.locator(".energy-period-note");
  await expect(note).toContainText(/Only \d+ days recorded so far \(since \w{3} \d{1,2} \w{3}\)/);
  const from = page.getByLabel("Energy from date");
  const min = await from.getAttribute("min");
  expect(min).toBeTruthy();
  await expect(from).toHaveValue(min!);
});

test("Setup › Sensors lists every sensor with no Show all or Hide, and the installer help starts closed", async ({
  page,
}) => {
  await page.goto("/#/setup/sensors");
  const sensors = page.locator("section.sensor-health");
  await expect(sensors.locator(".sensor-row").first()).toBeVisible();
  await expect(sensors.getByRole("button", { name: /^(Show all|Hide)/ })).toHaveCount(0);
  // A working sensor says what its reading is rather than a bare "Live".
  await expect(sensors.locator(".sensor-state").getByText("Live", { exact: true })).toHaveCount(0);
  const help = page.locator("details.energy-installers");
  await expect(help.locator(":scope > summary")).toContainText("Set up or change sensors (for installers)");
  await expect(help).not.toHaveAttribute("open", "");
  await expect(page.locator("main#main")).not.toContainText("Setup › Sensors suggests");
});
