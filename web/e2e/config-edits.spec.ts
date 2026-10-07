import { test, expect, type Page } from "@playwright/test";
import { openPage } from "./support/navigation";

/*
 * "Apply for me": the demo keeps a sample apps.yaml in its own folder, so the AI's file edit can be reviewed as a real diff,
 * applied (with a copy kept), recorded in Changes and Files, and put back with one click.
 */
test.afterEach(async ({ page }) => page.unrouteAll({ behavior: "ignoreErrors" }));

const TITLE = "Add export_today so Predbat can compare planned and actual export";

async function open(page: Page, hash: string) {
  await page.addInitScript(() => {
    sessionStorage.setItem("joule.setupAutoOpened", "1");
    localStorage.setItem("joule.firstRunSeen", "1");
  });
  await page.goto(`/${hash}`);
  await expect(page.locator("main#main")).toBeAttached({ timeout: 15000 });
}

test("an apps.yaml edit is reviewed as a real diff, applied with a copy kept, recorded, and restored in one click", async ({
  page,
}) => {
  await page.setViewportSize({ width: 390, height: 900 });
  await open(page, "#/insights/suggestions");
  const card = page.getByRole("article", { name: TITLE });
  await expect(card).toContainText("File edit · Waiting for you");
  // Joule can make it: Review and apply leads, copying by hand stays available.
  await expect(card.getByRole("button", { name: "Review and apply" })).toHaveClass(/button-primary/);
  await expect(card.getByRole("button", { name: "Copy snippet" })).toBeVisible();

  await card.getByRole("button", { name: "Review and apply" }).click();
  const dialog = page.getByRole("dialog", { name: "Review the edit to apps.yaml" });
  await expect(dialog).toBeVisible();
  const settings = dialog.getByRole("list", { name: "Settings this edit changes" });
  await expect(settings).toContainText("pred_bat › export_today");
  await expect(settings).toContainText("added");
  const diff = dialog.getByLabel("Changes to apps.yaml");
  await expect(diff.locator(".diff-add")).toHaveCount(2);
  await expect(diff.locator(".diff-add").first()).toContainText("export_today:");
  // Context from the real file around the new lines.
  await expect(diff).toContainText("import_today:");
  await expect(diff).toContainText("pv_today:");
  await expect(dialog).toContainText("Adds 2 lines under pred_bat, after import_today");
  await expect(dialog).toContainText("Joule saves a copy of apps.yaml exactly as it is now.");
  expect(await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth)).toBeLessThanOrEqual(0);

  await dialog.getByRole("button", { name: "Apply to apps.yaml" }).click();
  await expect(dialog).toHaveCount(0);
  await expect(page.getByRole("status").filter({ hasText: "Joule made the edit to apps.yaml" })).toBeVisible();
  await expect(card).toContainText("File edit · Joule applied this");
  await expect(card).toContainText("The demo has no real Predbat");
  await expect(card.getByRole("button", { name: "Review and apply" })).toHaveCount(0);
  await expect(card.getByRole("button", { name: "Undo marking this applied" })).toHaveCount(0);
  const restore = card.getByRole("button", { name: "Restore previous version" });
  await expect(restore).toBeVisible();

  // Recorded in Changes, as Joule's, with the setting it touched.
  await page.setViewportSize({ width: 1440, height: 1000 });
  await openPage(page, "Changes");
  const edited = page.locator("li.change").filter({ hasText: "Joule edited apps.yaml" });
  await expect(edited).toContainText("Through Joule");
  await expect(edited).toContainText("pred_bat › export_today");
  // And in Files: the copy taken before the edit, and the edit itself.
  await openPage(page, "Files");
  await expect(
    page.locator(".file-version-reason").filter({ hasText: /^Joule's edit: Add export_today/ }),
  ).toBeVisible();
  await expect(page.locator(".file-version-reason").filter({ hasText: /^Before Joule's edit/ })).toHaveCount(0);

  await openPage(page, "Suggestions");
  await page.getByRole("article", { name: TITLE }).getByRole("button", { name: "Restore previous version" }).click();
  await expect(
    page.getByRole("status").filter({ hasText: "apps.yaml is back as it was before Joule's edit." }),
  ).toBeVisible();
  const back = page.getByRole("article", { name: TITLE });
  await expect(back).toContainText("File edit · Waiting for you");
  await expect(back).toContainText("You put the previous apps.yaml back.");
  await expect(back.getByRole("button", { name: "Review and apply" })).toBeVisible();

  await openPage(page, "Changes");
  await expect(page.locator("li.change").filter({ hasText: "You put apps.yaml back as it was before" })).toBeVisible();

  // The review reads the restored file again: the same edit is offered once more.
  await openPage(page, "Suggestions");
  await page.getByRole("article", { name: TITLE }).getByRole("button", { name: "Review and apply" }).click();
  await expect(page.getByRole("dialog").getByRole("button", { name: "Apply to apps.yaml" })).toBeEnabled();
});

test("an edit Joule can't place safely says why and offers the snippet instead", async ({ page, context }) => {
  await context.grantPermissions(["clipboard-read", "clipboard-write"]);
  await page.route("**/api/investigations/*/filechanges/*/review", (route) =>
    route.fulfill({
      json: {
        file: "apps.yaml",
        canApply: false,
        reason: null,
        problem:
          "The lines this edit replaces aren't in apps.yaml exactly as the suggestion quotes them, so Joule can't place it safely. The file may have changed since the check.",
        hash: "0".repeat(64),
        placement: null,
        keys: [],
        lines: [],
        note: "Values that look like passwords or keys are hidden (•••); !secret references are shown as written.",
      },
    }),
  );
  await open(page, "#/insights/suggestions");
  const card = page.getByRole("article", { name: TITLE });
  await card.getByRole("button", { name: "Review and apply" }).click();
  const dialog = page.getByRole("dialog", { name: "Review the edit to apps.yaml" });
  await expect(dialog).toContainText("Joule won't make this edit.");
  await expect(dialog).toContainText("aren't in apps.yaml exactly as the suggestion quotes them");
  await expect(dialog.getByRole("button", { name: /^Apply/ })).toHaveCount(0);
  await dialog.getByRole("button", { name: "Copy the snippet instead" }).click();
  await expect(dialog).toHaveCount(0);
  expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(
    "  export_today:\n    - sensor.demo_inverter_export_today",
  );
  await expect(card).toContainText("File edit · Waiting for you");
});

test("Setup explains how to let Joule edit apps.yaml, and without it the edit stays copy-it-yourself", async ({
  page,
}) => {
  await open(page, "#/setup/files");
  const panel = page.locator("section.files-card").filter({ hasText: "Let Joule edit Predbat's config files" });
  await expect(panel).toContainText("On in the demo");
  await expect(panel).toContainText("The demo edits its own sample apps.yaml");

  // A live install that hasn't mounted Predbat's folder: the card points to Setup instead of offering to apply.
  await page.route("**/api/config-edits/status", (route) =>
    route.fulfill({
      json: {
        demo: false,
        configured: false,
        file: null,
        writable: false,
        allowed: false,
        allowedSource: null,
        quarantined: false,
        canApply: false,
        reason: "Joule can't see Predbat's apps.yaml.",
        root: null,
      },
    }),
  );
  // A fresh page load: the status is read once per page and shared by every card.
  await page.goto("/#/insights/suggestions");
  await page.reload();
  const card = page.getByRole("article", { name: TITLE });
  await expect(card).toBeVisible();
  await expect(card.getByRole("button", { name: "Review and apply" })).toHaveCount(0);
  await expect(card.getByRole("button", { name: "Copy snippet" })).toHaveClass(/button-primary/);
  await expect(card.getByRole("link", { name: "How to switch it on" })).toHaveAttribute("href", "#/setup/files");
});
