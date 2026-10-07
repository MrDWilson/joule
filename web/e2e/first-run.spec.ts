import { test, expect } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import { createServer, type Server } from "node:http";
import type { AddressInfo } from "node:net";

/*
 * The out-of-the-box container: no environment at all, so Joule starts in demo mode and Setup can switch it to a real
 * Predbat. scripts/e2e.sh runs this spec without App__Demo. A small stand-in serves Predbat's /api/state with made-up
 * entities, and the steps run in order against one server: demo → connect → restart → live checklist → meters.
 */
test.describe.configure({ mode: "serial" });

const STATE = {
  "predbat.status": { state: "Idle", attributes: {} },
  "update.predbat_version": { state: "on", attributes: { installed_version: "v8.30.1" } },
  "sensor.inverter_xx0000_load_energy_today_kwh": { state: "7.4", attributes: { unit_of_measurement: "kWh" } },
  "sensor.inverter_xx0000_pv_energy_today_kwh": { state: "9.1", attributes: { unit_of_measurement: "kWh" } },
  "sensor.inverter_xx0000_import_energy_today_kwh": { state: "3.2", attributes: { unit_of_measurement: "kWh" } },
  "sensor.inverter_xx0000_export_energy_today_kwh": { state: "1.1", attributes: { unit_of_measurement: "kWh" } },
  "sensor.inverter_xx0000_soc": { state: "54", attributes: { unit_of_measurement: "%", device_class: "battery" } },
};

let predbat: Server;
let predbatUrl = "";
let accessKey = "";

test.beforeAll(async () => {
  predbat = createServer((req, res) => {
    res.setHeader("Content-Type", "application/json");
    res.end(req.url?.startsWith("/api/state") ? JSON.stringify(STATE) : "{}");
  });
  await new Promise<void>((resolve) => predbat.listen(0, "127.0.0.1", resolve));
  predbatUrl = `http://127.0.0.1:${(predbat.address() as AddressInfo).port}`;
});
test.afterAll(async () => {
  // Joule keeps its connections to Predbat open between reads; close them rather than wait for them to idle out.
  predbat.closeAllConnections();
  await new Promise((resolve) => predbat.close(resolve));
});

test("a fresh install is the demo, and Setup connects it to Predbat without touching the environment", async ({
  page,
}) => {
  await page.goto("/");
  const tour = page.getByRole("complementary", { name: "Before you start" });
  await tour.getByRole("link", { name: "Connect my Predbat" }).click();
  await expect(page.getByRole("heading", { name: "You're looking at the demo" })).toBeVisible();
  const form = page.getByRole("form", { name: "Connect my Predbat" });
  await expect(form.getByRole("button", { name: "Switch to my Predbat" })).toBeDisabled();
  const results = await new AxeBuilder({ page }).include(".setup-golive").withTags(["wcag2a", "wcag2aa"]).analyze();
  expect(results.violations.filter((v) => v.impact === "serious" || v.impact === "critical")).toEqual([]);

  // A wrong address explains itself in plain words.
  await form.getByLabel("Predbat's address").fill("http://127.0.0.1:9");
  await form.getByRole("button", { name: "Test", exact: true }).click();
  await expect(form.getByText(/Nothing is listening there/)).toBeVisible();

  // The right one, typed without http://, answers with Predbat's version.
  await form.getByLabel("Predbat's address").fill(predbatUrl.replace("http://", ""));
  await form.getByRole("button", { name: "Test", exact: true }).click();
  await expect(form.getByText(/Predbat 8\.30\.1 answered/)).toBeVisible();
  await expect(form.getByLabel("Predbat's address")).toHaveValue(predbatUrl);

  // An access key is made up front; it's what this browser signs in with after the switch.
  accessKey = await form.getByLabel("Your access key").inputValue();
  expect(accessKey).toMatch(/^[A-Za-z0-9_-]{32}$/);

  await form.getByRole("button", { name: "Switch to my Predbat" }).click();
  await expect(page.getByText(/Saving and restarting Joule/)).toBeVisible();
  // Joule restarts in-process, the page reloads signed in, and the live checklist takes over.
  await expect(page.getByRole("heading", { name: "Get Joule running" })).toBeVisible({ timeout: 60_000 });
  await expect(page.getByText(/^Read (just now|\d+ min)/).first()).toBeVisible({ timeout: 30_000 });
  // Predbat is read and its meters were found on the first reading, with nothing configured: only the AI is left to do.
  await expect(page.getByRole("button", { name: /^Status: Setup 2\/6/ })).toBeVisible({ timeout: 30_000 });
});

test("the live checklist finds the meters from Predbat by itself, and any of them can be changed", async ({ page }) => {
  await page.goto("/");
  await page.evaluate((key) => sessionStorage.setItem("joule-access", key), accessKey);
  await page.goto("/#/setup");
  await page.reload();
  // Nothing was configured: Joule found the five meters Predbat's sensors make clear, on its first reading.
  const summary = page.getByText("5 of 11 meters mapped · 5 found automatically");
  await expect(summary).toBeVisible({ timeout: 30_000 });
  await summary.click();
  const meters = page.locator(".check-step").filter({ hasText: "Map your Home Assistant meters" }).locator("li.meter");
  const home = meters.filter({ hasText: "Home use" });
  await expect(home).toContainText("sensor.inverter_xx0000_load_energy_today_kwh");
  await expect(home).toContainText("Found automatically from Predbat");
  await expect(meters.filter({ hasText: "Battery level" })).toContainText("sensor.inverter_xx0000_soc");

  // Change opens the choices at that meter. Not mapped keeps Joule from finding it again.
  await page.getByRole("button", { name: "Change Grid export" }).click();
  const exportChoice = page.getByLabel("Grid export");
  await expect(exportChoice).toBeFocused();
  await expect(exportChoice).toHaveValue("sensor.inverter_xx0000_export_energy_today_kwh");
  await exportChoice.selectOption("");
  await expect(page.getByText("Joule won't look for this one again")).toBeVisible();
  await page.getByRole("button", { name: "Use these meters (1 change)" }).click();
  const after = page.getByText("4 of 11 meters mapped · 4 found automatically");
  await expect(after).toBeVisible({ timeout: 60_000 });
  await after.click();
  await expect(
    page
      .locator(".check-step")
      .filter({ hasText: "Map your Home Assistant meters" })
      .locator("li.meter")
      .filter({ hasText: "Grid export" }),
  ).toContainText("Left unmapped");
});

test("a new browser has to sign in with the key once the dashboard is live", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByLabel(/access key/i).first()).toBeVisible();
  const health = await (await page.request.get("/api/health")).json();
  expect(health.status).toBe("ok");
  expect(await (await page.request.get("/api/state?view=header")).status()).toBe(401);
});
