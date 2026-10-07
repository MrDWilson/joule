import { test, expect, type Page } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";

/*
 * The notifications bell (a server-side inbox: read, dismiss, mark all read, clear all, kept across reloads) and Setup ›
 * Notifications (phone channels). The e2e demo is pinned by App__Demo, so Setup is read-only there; the editable page is
 * driven against a scripted copy of the server's answers. Every value here is made up.
 */
test.use({ timezoneId: "Europe/London" });
test.afterEach(async ({ page }) => page.unrouteAll({ behavior: "ignoreErrors" }));

type Json = Record<string, any>; // eslint-disable-line @typescript-eslint/no-explicit-any

const bell = (page: Page) => page.getByRole("button", { name: /^Notifications/ }).first();
const panel = (page: Page) => page.getByRole("dialog", { name: "Notifications" });
async function unread(page: Page) {
  const name = (await bell(page).getAttribute("aria-label")) ?? "";
  return Number(/(\d+) unread/.exec(name)?.[1] ?? 0);
}

test("Setup › Notifications is read-only in a public demo", async ({ page }) => {
  await page.goto("/#/setup/notifications");
  await expect(page.getByRole("heading", { name: "Ways to reach you" })).toBeVisible();
  await expect(page.getByText(/Demo mode is fixed by App__Demo/)).toBeVisible();
  await expect(page.getByRole("switch", { name: "Send notifications with ntfy" })).toBeDisabled();
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze();
  expect(results.violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => v.id)).toEqual(
    [],
  );
});

/** A scripted /api/push/* that behaves like the server: saving updates the fields, secrets never come back. */
async function scriptedPush(page: Page) {
  const view: Json = await (
    await page.request.get("/api/push/settings", { headers: { "X-Joule-Request": "1" } })
  ).json();
  view.canSave = true;
  view.locked = null;
  const posts: Json[] = [];
  const tests: string[] = [];
  const apply = (values: Record<string, string | null>) => {
    for (const [key, value] of Object.entries(values)) {
      for (const f of [
        ...view.general,
        ...view.channels.flatMap((c: Json) => [c.enabledField, c.eventsField, c.quietField, ...c.fields]),
      ])
        if (f.key === key)
          Object.assign(f, { value: f.secret ? null : value, set: !!value, source: value ? "saved" : null });
    }
    for (const c of view.channels) {
      c.enabled = c.enabledField.value === "true";
      if (c.eventsField.value) c.events = c.eventsField.value === "none" ? [] : c.eventsField.value.split(",");
      c.quietHours = c.quietField.value ?? null;
      c.ready = c.fields.every((f: Json) => !f.required || f.set);
      c.problem = c.ready ? null : "Add the topic.";
    }
  };
  await page.route("**/api/push/settings", async (route) => {
    if (route.request().method() === "POST") {
      const body = route.request().postDataJSON();
      posts.push(body.values);
      apply(body.values);
    }
    await route.fulfill({ json: view });
  });
  await page.route("**/api/push/test/*", async (route) => {
    tests.push(route.request().url().split("/").pop()!);
    view.log.unshift({
      id: `t${tests.length}`,
      channel: "Ntfy",
      channelName: "ntfy",
      title: "Test message",
      status: "sent",
      error: null,
      attempts: 1,
      at: new Date().toISOString(),
      nextAt: null,
      test: true,
    });
    await route.fulfill({ json: { ok: true, error: null } });
  });
  return { posts, tests };
}

for (const width of [1440, 390]) {
  test(`a phone channel can be set up, tested and turned on at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: width < 700 ? 844 : 900 });
    const { posts, tests } = await scriptedPush(page);
    await page.goto("/#/setup/notifications");
    const ntfy = page.locator('[data-channel="Ntfy"]');
    const toggle = ntfy.getByRole("switch", { name: "Send notifications with ntfy" });
    // Nothing to switch on until the topic is there.
    await expect(toggle).toBeDisabled();
    await ntfy.getByText("Settings", { exact: true }).click();
    await ntfy.getByLabel("Topic").fill("joule-made-up-topic");
    await ntfy.getByLabel("Access token (optional)").fill("tk_made_up");
    await ntfy.getByRole("checkbox", { name: /Daily summary/ }).check();
    await ntfy.getByRole("checkbox", { name: /Hold messages overnight/ }).check();
    await ntfy.getByLabel("From").fill("23:00");
    await ntfy.getByRole("button", { name: "Save and test" }).click();
    await expect(ntfy.getByText("Sent. Check that it arrived.")).toBeVisible();
    expect(tests).toEqual(["Ntfy"]);
    expect(posts[0]).toMatchObject({
      "Notifications:Ntfy:Topic": "joule-made-up-topic",
      "Notifications:Ntfy:Token": "tk_made_up",
      "Notifications:Ntfy:Events": "needs_you,problem,unfinished,offline,summary",
      "Notifications:Ntfy:QuietHours": "23:00-07:00",
    });
    // The saved token isn't shown again; the box says it's saved.
    await expect(ntfy.getByLabel("Access token (optional)")).toHaveValue("");
    await expect(ntfy.getByLabel("Access token (optional)")).toHaveAttribute("placeholder", /Saved/);
    await expect(toggle).toBeEnabled();
    await toggle.click();
    await expect(ntfy.getByText("On", { exact: true })).toBeVisible();
    expect(posts.at(-1)).toEqual({ "Notifications:Ntfy:Enabled": "true" });
    // A saved token can be removed, not only replaced.
    await ntfy.getByRole("button", { name: "Remove saved access token" }).click();
    await expect(ntfy.getByLabel("Access token (optional)")).not.toHaveAttribute("placeholder", /Saved/);
    expect(posts.at(-1)).toEqual({ "Notifications:Ntfy:Token": null });
    await expect(ntfy.getByRole("button", { name: "Remove saved access token" })).toHaveCount(0);
    await expect(page.locator(".push-log")).toContainText("Test · ntfy");
    expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBe(
      0,
    );
  });
}

test("the bell's notifications can be opened, dismissed, read and cleared, and it sticks", async ({ page }) => {
  await page.goto("/#/today");
  await expect.poll(() => unread(page)).toBeGreaterThan(1);
  const start = await unread(page);
  await bell(page).click();
  const list = panel(page);
  const rows = list.locator(".notification-list > li");
  await expect(rows.first()).toBeVisible();
  const count = await rows.count();
  expect(count).toBeGreaterThanOrEqual(start);

  // Opening one goes to it and marks it read.
  const first = rows.first();
  const href = await first.getByRole("link").getAttribute("href");
  await first.getByRole("link").click();
  await expect(page).toHaveURL(new RegExp(href!.replace(/[?]/g, "\\?") + "$"));
  await expect.poll(() => unread(page)).toBe(start - 1);

  // Dismissing one removes it from the list.
  await bell(page).click();
  const second = rows.nth(1);
  const title = (await second.locator("strong").innerText()).replace(/^Unread: /, "");
  await second.getByRole("button", { name: /^Dismiss: / }).click();
  await expect(list.getByText(title, { exact: true })).toHaveCount(0);
  await expect.poll(() => unread(page)).toBe(start - 2);

  // Mark all as read clears the badge, and a reload (another browser, a restart) keeps it cleared.
  await list.getByRole("button", { name: "Mark all as read" }).click();
  await expect(bell(page)).toHaveAccessibleName("Notifications");
  await expect(page.locator(".icon-button .dot-count")).toHaveCount(0);
  await page.reload();
  await expect(bell(page)).toHaveAccessibleName("Notifications");
  await bell(page).click();
  await expect(rows).toHaveCount(count - 1);
  await list.getByRole("button", { name: "Clear all" }).click();
  await expect(list.getByText("Nothing new.", { exact: false })).toBeVisible();
  await expect(list.getByRole("link", { name: "Get these on your phone" })).toHaveAttribute(
    "href",
    "#/setup/notifications",
  );
  await page.reload();
  await bell(page).click();
  await expect(rows).toHaveCount(0);
});
