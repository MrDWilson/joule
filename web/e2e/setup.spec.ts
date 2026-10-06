import { test, expect, type Page } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import { fetchFresh, fulfillRewritten } from "./support/routes";
import { openPage } from "./support/navigation";

/*
 * Setup: Predbat settings, the Changes timeline, Files and the setup checklist, in demo mode and with live-shaped
 * fixtures (a Predbat with 128 settings whose history is version updates and manual overrides, and a fresh install
 * with nothing configured). Every fixture value here is made up.
 */
test.use({ timezoneId: "Europe/London" });
// Rewritten polls may still be in flight when a test ends.
test.afterEach(async ({ page }) => page.unrouteAll({ behavior: "ignoreErrors" }));

type Json = Record<string, any>; // eslint-disable-line @typescript-eslint/no-explicit-any

async function axe(page: Page, label: string) {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze();
  const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
  expect(serious.map((v) => `${label} · ${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join(" | ")}`)).toEqual([]);
}

/** Rewrites every /api/state reply (always from a fresh 200, never a 304). */
async function rewriteState(page: Page, change: (payload: Json) => void) {
  await page.route(/\/api\/state(\?|$)/, async (route) => {
    const response = await fetchFresh(route);
    const payload = await response.json();
    change(payload);
    await fulfillRewritten(route, response, payload);
  });
}

const DAY = 86400000;
const iso = (msAgo: number) => new Date(Date.now() - msAgo).toISOString();

/** The shape of a real live install: 128 settings, a first copy, then only Predbat's own controls changing. */
function liveShaped(payload: Json) {
  const template = payload.state.settings.find((s: Json) => s.key === "load_scaling");
  const extra: Json[] = [];
  for (let i = 0; i < 90; i++)
    extra.push({
      ...template,
      key: `owned_tunable_${i}`,
      name: `Owned tunable ${i}`,
      entityId: `input_number.predbat_owned_tunable_${i}`,
      section: ["Battery", "Export", "Forecast", "Car & Octopus"][i % 4],
      category: "Battery",
      commonlyTuned: false,
      autoAllowed: false,
      kind: "tunable",
    });
  for (let i = 0; i < 25; i++)
    extra.push({
      ...template,
      key: `manual_owned_${i}`,
      name: `Owned manual override ${i}`,
      type: "select",
      value: "off",
      kind: "override",
      section: "Manual overrides",
      editable: false,
      autoEligible: false,
    });
  payload.state.settings = [
    ...payload.state.settings.filter((s: Json) => s.key !== "update"),
    {
      ...template,
      key: "update",
      name: "Predbat version",
      type: "select",
      value: "v9.3.5 Bug fixes cloud inverters & Misc",
      options: ["v9.3.5 Bug fixes cloud inverters & Misc", "v9.3.4 IOG started-dispatch fix", "main"],
      kind: "software",
      section: "Predbat software",
      editable: false,
      autoEligible: false,
      autoAllowed: false,
    },
    ...extra,
  ].slice(0, 128);
  payload.connection.demo = false;
  payload.connection.predbatConfigured = true;
  payload.connection.writesEnabled = false;
  payload.state.mode = "Recommend";
  payload.state.experiments = [];
  payload.state.revisions = [
    {
      id: 1,
      at: iso(3 * DAY),
      source: "Predbat",
      reason: "First copy of your Predbat settings",
      changes: [],
      values: { load_scaling: template.value },
      reverts: null,
      fileVersionBefore: null,
      fileVersionAfter: null,
    },
    {
      id: 2,
      at: iso(DAY + 3600000),
      source: "Predbat",
      reason: "Changed in Predbat: Manual charge slots off → +Sun 15:00",
      changes: [{ key: "manual_owned_0", before: "off", after: "+Sun 15:00" }],
      values: { load_scaling: template.value },
      reverts: null,
      fileVersionBefore: null,
      fileVersionAfter: null,
    },
    {
      id: 3,
      at: iso(DAY),
      source: "Predbat",
      reason: "Changed in Predbat: Predbat version …",
      changes: [
        { key: "update", before: "v9.3.4 IOG started-dispatch fix", after: "v9.3.5 Bug fixes cloud inverters & Misc" },
      ],
      values: { load_scaling: template.value },
      reverts: null,
      fileVersionBefore: null,
      fileVersionAfter: null,
    },
  ];
  payload.state.settingEvents = [
    {
      id: "owned-event-1",
      at: iso(DAY + 3600000),
      kind: "override",
      key: "manual_owned_0",
      name: "Manual charge slots",
      before: "off",
      after: "+Sun 15:00",
      title: "Manual charge slots set for Sun 15:00 (cleared after 20 min)",
      revertedAt: iso(DAY + 2400000),
    },
    {
      id: "owned-event-2",
      at: iso(DAY),
      kind: "software",
      key: "update",
      name: "Predbat version",
      before: "v9.3.4",
      after: "v9.3.5",
      title: "Predbat updated to v9.3.5",
      revertedAt: null,
    },
  ];
}

test("the settings default view is short on a phone, even with 128 live-shaped settings", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/#/setup/settings");
  await expect(page.getByRole("radiogroup", { name: "How the AI helps" })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollHeight), "demo settings height").toBeLessThan(4000);
  await rewriteState(page, liveShaped);
  await page.reload();
  await expect(page.getByRole("button", { name: /^Commonly tuned/ })).toHaveAttribute("aria-pressed", "true");
  // The search box counts the same settings as the All filter.
  const all = (await page.getByRole("button", { name: /^All/ }).locator(".filter-chip-count").textContent())!;
  await expect(page.getByPlaceholder(`Search ${all} settings`)).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollHeight), "live-shaped settings height").toBeLessThan(
    4000,
  );
  expect(await page.evaluate(() => document.documentElement.scrollWidth), "no sideways scroll").toBeLessThanOrEqual(
    390,
  );
  // Predbat's own controls are status only: no button, no editor, no automatic-change switch.
  await expect(page.getByRole("button", { name: /^Predbat version\b/ })).toHaveCount(0);
  await page.getByText("Predbat status").click();
  // The version reads 9.3.5, with its release title underneath.
  const version = page.locator(".settings-status li").filter({ hasText: "Predbat version" });
  await expect(version.locator(".value-chip")).toHaveText("9.3.5");
  await expect(version.locator(".setting-line-detail")).toHaveText("Bug fixes cloud inverters & Misc");
  // Search covers every setting and opens the sections it found.
  await page.getByRole("searchbox", { name: "Search settings" }).fill("Owned tunable 42");
  await expect(page.getByRole("button", { name: /^Owned tunable 42\b/ })).toBeVisible();
  await axe(page, "settings 390");
});

test("the setting sheet is labelled, links to Predbat's docs and asks before Automatic", async ({ page }) => {
  await page.goto("/#/setup/settings");
  await page.getByRole("button", { name: /^House load scaling\b/ }).click();
  const sheet = page.getByRole("dialog", { name: "House load scaling" });
  await expect(sheet.getByText("Predbat default")).toBeVisible();
  await expect(sheet.getByRole("link", { name: "Predbat docs (opens in new tab)" })).toHaveAttribute(
    "target",
    "_blank",
  );
  await expect(sheet.locator("code.entity-id")).toHaveText("input_number.predbat_load_scaling");
  await expect(sheet.getByRole("slider", { name: /House load scaling/ })).toBeVisible();
  await axe(page, "setting sheet");
  await page.keyboard.press("Escape");
  // The mode picker is a radiogroup; Automatic asks first and lists what it may change.
  const auto = page.getByRole("radio", { name: /^Automatic/ });
  await expect(page.getByRole("radio", { name: /^Suggest changes/ })).toHaveAttribute("aria-checked", "true");
  await auto.click();
  const confirm = page.getByRole("dialog", { name: "Turn on Automatic?" });
  await expect(confirm).toContainText(/no setting allows automatic changes|can change only these settings/);
  await confirm.getByRole("button", { name: "Cancel" }).click();
  await expect(auto).toHaveAttribute("aria-checked", "false");
});

test("the Changes timeline shows live-shaped revisions as events and never offers to undo or restore an update", async ({
  page,
}) => {
  await rewriteState(page, liveShaped);
  await page.goto("/#/setup/changes");
  const list = page.locator(".changes-page");
  await expect(list.getByText("Predbat updated to 9.3.5", { exact: true })).toBeVisible();
  await expect(
    list.getByText("Manual charge slots set for Sun 15:00 (cleared after 20 min)", { exact: true }),
  ).toBeVisible();
  await expect(list.getByText("First settings snapshot", { exact: true })).toBeVisible();
  // The legacy revisions for those controls aren't shown a second time.
  await expect(list.locator("li.change")).toHaveCount(3);
  await expect(page.getByRole("button", { name: /^Undo/ })).toHaveCount(0);
  // Only the settings copy has actions; the update and the override are information.
  await expect(list.locator("li.change", { hasText: "Predbat updated" }).getByRole("button")).toHaveCount(0);
  await expect(list.locator("li.change", { hasText: "Manual charge slots" }).getByRole("button")).toHaveCount(0);
  await expect(page.getByRole("heading", { level: 2, name: "Yesterday" })).toBeVisible();
  await axe(page, "changes");
});

test("a fresh unconfigured install opens the setup checklist, with what to set and a status chip", async ({ page }) => {
  await rewriteState(page, (payload) => {
    payload.connection.demo = false;
    payload.connection.predbatConfigured = false;
    payload.connection.writesEnabled = false;
    payload.state.lastCollection = null;
    payload.state.collectionError = "Predbat isn't configured.";
  });
  const steps = [
    ["predbat", "Connect to Predbat", false, true],
    ["collecting", "Read Predbat's plan", false, true],
    ["meters", "Map your Home Assistant meters", false, true],
    ["ai", "Choose an AI provider", false, true],
    ["mcp", "Let the AI read Predbat's logs (MCP)", false, false],
    ["reviews", "Turn on automatic reviews", false, false],
    ["writes", "Allow Joule to change Predbat", false, false],
  ].map(([key, label, done, required]) => ({ key, label, done, required }));
  const meters = [
    "load",
    "pv",
    "grid_import",
    "grid_export",
    "battery_charge",
    "battery_discharge",
    "ev",
    "soc",
    "import_tariff",
    "export_tariff",
  ].map((metric, i) => ({
    metric,
    envVar: `HomeAssistant__Entities__${["Load", "Pv", "GridImport", "GridExport", "BatteryCharge", "BatteryDischarge", "Ev", "Soc", "ImportTariff", "ExportTariff"][i]}`,
    required: metric === "load",
    entity: null,
    status: null,
    unit: null,
    value: null,
    profile: null,
    candidates:
      metric === "load"
        ? [{ entity: "sensor.owned_inverter_load_energy_today", name: "Load today", unit: "kWh", state: "7.4" }]
        : [],
  }));
  await page.route(/\/api\/setup$/, (route) =>
    route.fulfill({
      json: {
        demo: false,
        progress: { done: 0, total: 7, requiredDone: false, steps },
        predbat: {
          configured: false,
          address: null,
          lastCollection: null,
          error: "Predbat isn't configured.",
          version: null,
          writesEnabled: false,
        },
        sensors: {
          configured: false,
          direct: false,
          viaPredbat: false,
          address: null,
          lastCollection: null,
          error: null,
          meters,
        },
        mcp: { configured: false, connected: false, tools: 0, canReadApps: false, error: null, checkedAt: null },
      },
    }),
  );
  await page.goto("/");
  await expect(page).toHaveURL(/#\/setup$/);
  await expect(page.getByRole("heading", { name: "Get Joule running" })).toBeVisible();
  await expect(page.getByRole("button", { name: /^Status: Setup 0\/6/ })).toBeVisible();
  // The first step is open, with the exact line to add and a Copy button.
  await expect(page.getByLabel("Predbat address", { exact: true })).toContainText(
    "Predbat__BaseUrl=http://predbat:5052",
  );
  await expect(page.getByRole("button", { name: "Copy: Predbat address" })).toBeVisible();
  // The checklist itself replaces the "isn't connected" banner here.
  await expect(page.getByText("Predbat isn't connected yet")).toHaveCount(0);
  await page.getByText("Map your Home Assistant meters").click();
  await expect(page.getByLabel("Meter mappings", { exact: true })).toContainText(
    "HomeAssistant__Entities__Load=sensor.owned_inverter_load_energy_today",
  );
  await axe(page, "setup checklist");
  // Once per session: going back to the root later doesn't redirect again.
  await page.goto("/#/plan");
  await page.goto("/");
  await expect(page).toHaveURL(/#\/today$/);
});

test("demo Setup offers to connect your own Predbat, and Files shows keys with values masked", async ({ page }) => {
  await page.goto("/#/setup");
  await expect(page.getByRole("heading", { name: "You're looking at the demo" })).toBeVisible();
  // This demo is pinned by App__Demo (as a public demo would be), so it says how to go live instead of offering the form.
  await expect(page.getByText(/Demo mode is fixed by App__Demo/)).toBeVisible();
  await expect(page.getByRole("form", { name: "Connect my Predbat" })).toHaveCount(0);
  await expect(page.getByLabel("Go live", { exact: true })).toContainText("App__Demo=false");
  await expect(page.getByLabel("Go live", { exact: true })).toContainText(/App__AccessKey=[A-Za-z0-9_-]{32}/);
  await openPage(page, "Files");
  await expect(page.getByText(/^Keeping copies of runtime-settings.json/)).toBeVisible();
  // The saved copies come first; the masked file waits behind "Show file layout".
  await expect(page.getByRole("heading", { name: "Saved copies" })).toBeVisible();
  await page.getByText("Show file layout").click();
  const text = page.getByLabel("Saved copy of runtime-settings.json");
  await expect(text).toContainText('"load_scaling": •••');
  await expect(text).not.toContainText("1.08");
  // Hashes and ids are behind Technical details.
  await expect(page.locator("code.hash").first()).toBeHidden();
});

test("the demo tour shows on Today with a way to connect", async ({ page }) => {
  await page.goto("/");
  const tour = page.getByRole("complementary", { name: "Before you start" });
  await expect(tour.getByRole("link", { name: "Connect my Predbat" })).toHaveAttribute("href", "#/setup");
  await expect(tour.getByRole("link", { name: /What Predbat plans/ })).toHaveAttribute("href", "#/plan");
});

test("on a phone the demo tour stays short: one sideways row of places and the small print beside Got it", async ({
  page,
}) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/");
  const tour = page.getByRole("complementary", { name: "Before you start" });
  await expect(tour).toBeVisible();
  expect((await tour.boundingBox())!.height).toBeLessThan(300);
  await expect(tour.getByRole("link", { name: /What your meters recorded/ })).toHaveAttribute("href", "#/energy");
  const got = (await tour.getByRole("button", { name: "Got it" }).boundingBox())!;
  const note = (await tour.locator(".demo-tour-note").boundingBox())!;
  expect(Math.abs(got.y + got.height / 2 - (note.y + note.height / 2))).toBeLessThan(20);
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390);
});

test("risk notes are quiet, and AI may change lists each low-risk setting with its own switch", async ({ page }) => {
  await page.goto("/#/setup/settings");
  await expect(page.locator(".setting-tag").getByText("Change with care").first()).toBeVisible();
  await expect(page.locator(".settings-page")).not.toContainText("Needs care");
  await expect(page.locator(".settings-page")).not.toContainText("High risk");
  const chip = page.getByRole("button", { name: /^AI may change/ });
  // Outside Automatic the chip has no count: a number there would suggest the AI is acting.
  await expect(chip.locator(".filter-chip-count")).toHaveCount(0);
  await chip.click();
  const list = page.locator("section.auto-permissions");
  await expect(list).toContainText("These only apply when “How the AI helps” is set to Automatic");
  await expect(list.getByRole("switch", { name: "Allow automatic changes to House load scaling" })).toBeVisible();
  await list.getByRole("switch", { name: "Allow automatic changes to House load scaling" }).click();
  // Turning one on asks for its limits first.
  await expect(page.getByRole("dialog")).toBeVisible();
  await axe(page, "ai may change");
});

test("About names Predbat's version the way every other page does", async ({ page }) => {
  await page.goto("/#/setup/about");
  const versions = page.locator("dl.connection-details");
  await expect(versions).toContainText("Predbat version");
  await expect(versions).toContainText(/Predbat version\s*9\.3\.5/);
  await expect(versions).not.toContainText("Not reported");
  // The overview shows the same number on its About card, and no separate About strip.
  await page.goto("/#/setup");
  await expect(page.locator(".setup-card").filter({ hasText: "About" })).toContainText("Predbat 9.3.5");
  await expect(page.locator(".setup-about")).toHaveCount(0);
});

test("an undone change offers Redo, values carry units and the current snapshot has a chip, not Restore", async ({
  page,
}) => {
  await page.goto("/#/setup/settings");
  await page.getByRole("button", { name: /^House load scaling\b/ }).click();
  const sheet = page.getByRole("dialog", { name: "House load scaling" });
  await sheet.getByRole("button", { name: "Increase" }).click();
  await sheet.getByRole("button", { name: "Save change" }).click();
  await expect(sheet).toBeHidden();
  await page.goto("/#/setup/changes");
  const changes = page.locator(".changes-page");
  const latest = changes.locator("li.change").first();
  await expect(latest.locator(".change-current")).toHaveText("Current");
  // The menu sits in the title row; Restore isn't offered for the settings already in place.
  await latest
    .locator(".change-head")
    .getByRole("button", { name: /^More actions/ })
    .click();
  await expect(page.getByRole("button", { name: "Restore settings to this point" })).toHaveCount(0);
  await page.keyboard.press("Escape");
  await latest.getByRole("button", { name: /^Undo:/ }).click();
  const confirm = page.getByRole("dialog", { name: "Undo this change?" });
  await confirm.getByRole("button", { name: "Undo change" }).click();
  await expect(confirm).toBeHidden();
  const undo = changes.locator("li.change").first();
  await expect(undo).toContainText("You undid a change");
  await expect(undo.getByRole("button", { name: /^Redo:/ })).toBeVisible();
  await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
});
