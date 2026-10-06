import { test, expect } from "@playwright/test";
import { MIDNIGHT, useLive5Oct } from "./support/live-5oct";

/**
 * "The figures look wrong": with the live 5 Oct snapshot, the Energy page must show the figures the meters show.
 * Net cost is paid minus earned (£1.80 − £0.70 = £1.10), never the matched-period £0.06; grid export reads 6.33 kWh on the
 * page exactly as on the meter's own sensor row; nothing says "% measured" when the day is complete; and the half-hour
 * chart's caption quotes the same Home use and Solar as the headline, not its own sum of half-hours.
 */
test.use({ timezoneId: "Europe/London" });

for (const width of [1440, 390]) {
  test(`Energy figures match the meters at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: width < 500 ? 844 : 900 });
    await useLive5Oct(page);
    // Half-hours that sum to different figures (7.0 kWh home, 1.0 kWh solar): the caption must still quote the headline's.
    const slots = Array.from({ length: 20 }, (_, i) => ({
      time: new Date(Date.parse(MIDNIGHT) + i * 1800000).toISOString(),
      durationMinutes: 30,
      load: 0.5,
      home: 0.35,
      pv: 0.05,
      ev: 0.15,
    }));
    await page.route("**/api/telemetry/history?*", (route) => route.fulfill({ json: { slots } }));
    await page.goto("/#/energy?period=today");
    const figures = page.locator("section.energy-figures");
    await expect(figures.locator(".energy-chips")).toBeVisible();

    const net = figures.getByRole("article", { name: "Net cost", exact: true });
    await expect(net.locator(".stat-value")).toHaveText("£1.10");
    await expect(net.locator(".energy-paid")).toHaveText("Paid £1.80 · Earned £0.70");
    await expect(figures).not.toContainText("£0.06");

    const exportChip = figures.getByRole("listitem", { name: "Grid export", exact: true });
    await expect(exportChip.locator(".energy-chip-value")).toHaveText("6.3 kWh");
    await expect(exportChip).toHaveAttribute("title", "Grid export: 6.33 kWh");
    await expect(figures.getByRole("listitem", { name: "Solar", exact: true })).not.toContainText("measured");
    await expect(figures).not.toContainText(/\d+% measured/);
    await expect(figures.locator(".energy-match")).toHaveText("Matches Home Assistant’s meters");

    const home = figures.getByRole("article", { name: "Home use", exact: true });
    await expect(home.locator(".stat-value")).toHaveText("7.7kWh");
    const caption = figures.locator("figure.tl-day .chart-caption");
    await expect(caption).toContainText("home used 7.7 kWh");
    await expect(caption).toContainText("solar made 1.4 kWh");
    await expect(
      figures.getByRole("listitem", { name: "Solar", exact: true }).locator(".energy-chip-value"),
    ).toHaveText("1.4 kWh");

    // The meter's own row says the same, to two decimals.
    const sensors = page.locator("section.sensor-health");
    await expect(sensors).toContainText("All 11 sensors OK");
    await sensors.getByRole("button", { name: "Show all 11" }).click();
    const exportRow = sensors.locator(".sensor-row").filter({ hasText: "Grid export" });
    await expect(exportRow.locator(".sensor-value")).toHaveText("6.33 kWh today");
    // An idle car charger is not a fault, and the battery reads as a whole percentage.
    await expect(sensors.locator(".sensor-row").filter({ hasText: "Car charging" })).toContainText("Not charging");
    await expect(
      sensors.locator(".sensor-row").filter({ hasText: "Battery level" }).locator(".sensor-value"),
    ).toHaveText("74%");
  });
}
