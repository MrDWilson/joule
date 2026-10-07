import { test, expect, type Page } from "@playwright/test";

// Money on the demo: the Grid tile and lane on Today, the standing charge in net cost (and the switch that leaves it out),
// and grid import/export on the Energy page's daily charts. Each width also checks nothing scrolls sideways.
test.use({ timezoneId: "Europe/London" });

const noOverflow = (page: Page) => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth);
const pounds = (text: string) => Number(/£(\d+\.\d\d)/.exec(text)?.[1] ?? NaN);

for (const [width, height] of [
  [1440, 900],
  [390, 844],
]) {
  test(`Today shows the grid and the standing charge at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height });
    await page.goto("/#/today");
    const grid = page.getByRole("article", { name: "Grid", exact: true });
    await expect(grid).toContainText("Grid import");
    await expect(grid.locator(".stat-value")).toContainText(/\d+\.\d\s*kWh/);
    await expect(grid).toContainText(/Cost £\d+\.\d\d/);
    await expect(grid).toContainText(/Exported \d+\.\d kWh · earned £\d+\.\d\d|Nothing exported yet/);
    await expect(grid).toContainText(/Yesterday by now: \d+\.\d kWh/);
    await expect(grid.locator(".grid-trend rect.grid-bought").first()).toBeVisible();

    // The demo house reads its standing charge from a sensor: in the net cost by default, named on its own line.
    const net = page.getByRole("article", { name: /^Net (cost|earnings) today$/ });
    await expect(net).toContainText(/Standing charge £\d+\.\d\d · £0\.54\/day/);
    await expect(page.locator(".today-footnote")).toContainText("Costs include the standing charge (£0.54/day).");

    // The timeline has a grid lane, with at most five legend chips: on a phone the price takes a chip, so the grid lane
    // is named by its own label instead.
    const chart = page.locator(".today-timeline figure").first();
    await expect(chart.locator(".tl-lane-grid")).toBeVisible();
    await expect(chart.locator(".tl-lane-grid path.tl-series[data-series='grid']").first()).toBeAttached();
    await expect(chart.locator(".chart-chips .chart-chip").filter({ hasText: /^Grid$/ })).toHaveCount(
      width > 500 ? 1 : 0,
    );
    expect(await chart.locator(".chart-chips .chart-chip").count()).toBeLessThanOrEqual(5);
    await expect(chart.locator(".chart-caption")).toContainText(/kWh came from the grid/);
    expect(await noOverflow(page)).toBe(true);
    await page.screenshot({ path: `../.cache/screenshots/money-today-${width}.png`, fullPage: true });
  });
}

test("the standing charge switch takes it out of net cost everywhere, and back", async ({ page }) => {
  await page.goto("/#/today");
  const net = page.getByRole("article", { name: /^Net (cost|earnings) today$/ });
  await expect(net).toContainText("Standing charge");
  const withCharge = pounds((await net.locator(".stat-value").textContent())!);
  const charge = pounds((await net.locator(".stat-delta-line").textContent())!);

  await page.goto("/#/setup/sensors");
  const card = page.getByRole("region", { name: "Standing charge" });
  await expect(card).toContainText("53.68p a day (£0.54), read from Home Assistant.");
  await expect(card).toContainText("Used only on days the sensor has no reading.");
  const toggle = card.getByRole("switch", { name: "Include the standing charge in net cost" });
  await expect(toggle).toHaveAttribute("aria-checked", "true");
  await toggle.click();
  await expect(
    page.getByRole("status").filter({ hasText: "Net cost now leaves out the standing charge." }),
  ).toBeVisible();
  await expect(toggle).toHaveAttribute("aria-checked", "false");

  await page.goto("/#/today");
  await expect(net).not.toContainText("Standing charge");
  await expect(page.locator(".today-footnote")).toContainText("Costs leave out the standing charge (£0.54/day)");
  const without = pounds((await net.locator(".stat-value").textContent())!);
  expect(Math.abs(withCharge - without - charge)).toBeLessThanOrEqual(0.011);

  await page.goto("/#/setup/sensors");
  await card.getByRole("switch", { name: "Include the standing charge in net cost" }).click();
  await expect(card.getByRole("switch", { name: "Include the standing charge in net cost" })).toHaveAttribute(
    "aria-checked",
    "true",
  );
  // A figure typed in is checked before it is saved.
  const input = card.getByLabel("Standing charge (p/day)");
  await input.fill("abc");
  await expect(card.getByRole("button", { name: "Save", exact: true })).toBeDisabled();
  await input.fill("61.5");
  await card.getByRole("button", { name: "Save", exact: true }).click();
  await expect(page.getByRole("status").filter({ hasText: "Standing charge saved." })).toBeVisible();
  await expect(card.getByRole("button", { name: "Clear", exact: true })).toBeVisible();
});

for (const [width, height] of [
  [1440, 900],
  [390, 844],
]) {
  test(`the Energy page charts grid import and export each day at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height });
    await page.goto("/#/energy?period=7d");
    const grid = page.getByRole("figure", { name: /Grid each day/ });
    await expect(grid).toBeVisible();
    await expect(grid.locator("path.grid-bought").first()).toBeVisible();
    await expect(grid).toContainText(
      /Grid over 7 days: bought \d+\.\d kWh for £\d+\.\d\d, sold \d+\.\d kWh for £\d+\.\d\d\./,
    );
    await expect(grid).toContainText("Bought, above the line");
    // The net cost names the standing charge on its own line.
    await expect(page.locator(".energy-hero")).toContainText(/Standing charge £\d+\.\d\d · £0\.54\/day/);
    const how = page.locator("details.energy-how");
    await how.locator(":scope > summary").click();
    await expect(how).toContainText("Costs include the standing charge (£0.54/day).");
    expect(await noOverflow(page)).toBe(true);
    await page.screenshot({ path: `../.cache/screenshots/money-energy-${width}.png`, fullPage: true });
  });
}
