import { test, expect } from "@playwright/test";
import { openPage } from './support/navigation';
test("investigation, proposal review and approval once create an experiment", async ({
  page,
  request,
}) => {
  await page.goto("/");
  await openPage(page, 'Settings');
  await page
    .getByRole("radio", { name: /^Suggest changes/ })
    .click();
  await openPage(page, "Checks");
  await page
    .getByRole("button", { name: "Run a full check", exact: true })
    .click();
  await expect(
    page.getByRole("button", { name: "Run a full check", exact: true }),
  ).toBeEnabled({ timeout: 20000 });
  await openPage(page, "Suggestions");
  await page
    .getByRole("button", { name: "Review", exact: true })
    .last()
    .click();
  await expect(page.getByRole("dialog").getByRole("region", { name: "What changes" })).toContainText(
    "House load scaling",
  );
  await expect(page.getByRole("dialog")).toContainText("The tradeoff");
  await page
    .getByRole("dialog")
    .getByRole("button", { name: "Apply change", exact: true })
    .click();
  await expect(page.getByRole("dialog")).toBeHidden();
  const approvedState = (await (await request.get("/api/state")).json()).state;
  expect(approvedState.settings.find((x: { key: string }) => x.key === "load_scaling").autoAllowed).toBe(false);
  await openPage(page, 'Trials');
  await expect(
    page.getByText("Trial running", { exact: true }).first(),
  ).toBeVisible();
  await expect(
    page.getByRole("button", { name: "Undo", exact: true }).first(),
  ).toBeVisible();
});
test("manual edits, explicit permission and reversible history persist", async ({
  page,
}) => {
  await page.goto("/");
  await expect(
    page.getByRole("heading", { name: "Today", exact: true }),
  ).toBeVisible();
  // The old DEMO/LIVE banner is now one status chip in the top bar, with the details in its popover.
  const status = page.getByRole("button", { name: /^Status: Demo/ });
  await expect(status).toBeVisible();
  await status.click();
  await expect(page.getByRole("dialog", { name: "Connection status" })).toContainText("approvals are saved here");
  await page.keyboard.press("Escape");
  await openPage(page, 'Settings');
  const row = () => page.getByRole("button", { name: /^House load scaling\b/ });
  await row().click();
  const dialog = page.getByRole("dialog", { name: "House load scaling" });
  const value = dialog.getByRole("spinbutton", { name: "New value for House load scaling" });
  const before = await value.inputValue();
  const after = before === "1.12" ? "1.11" : "1.12";
  await value.fill(after);
  await dialog.getByRole("button", { name: "Save change", exact: true }).click();
  await expect(dialog).toBeHidden();
  await page.reload();
  await openPage(page, 'Settings');
  await expect(row()).toContainText(after);
  // Automatic changes are allowed from the setting's sheet; the limits dialog replaces it and comes back to it.
  await row().click();
  const toggle = page.getByRole("switch", { name: "Allow automatic changes to House load scaling", exact: true });
  if ((await toggle.getAttribute("data-state")) === "checked") await toggle.click();
  await expect(toggle).not.toBeChecked();
  await toggle.click();
  await page.getByRole("dialog").getByRole("button", { name: "Allow future changes", exact: true }).click();
  await expect(toggle).toBeChecked();
  await toggle.click();
  await expect(toggle).not.toBeChecked();
  await page.keyboard.press("Escape");
  await openPage(page, 'Changes');
  await expect(page.getByText(`You changed House load scaling ${before} → ${after} via Joule`).first()).toBeVisible();
  // Restore lives in the overflow menu and asks first, listing what will change.
  await page.getByRole("button", { name: /^More actions for/ }).last().click();
  await page.getByRole("button", { name: "Restore settings to this point" }).click();
  const restore = page.getByRole("dialog", { name: "Restore settings to this point?" });
  await expect(restore).toContainText("will change");
  await restore.getByRole("button", { name: "Cancel", exact: true }).click();
  // Undo asks too, with the diff.
  await page.getByRole("button", { name: /^Undo: You changed House load scaling/ }).first().click();
  const undo = page.getByRole("dialog", { name: "Undo this change?" });
  await expect(undo).toContainText(`${after} → ${before}`);
  await undo.getByRole("button", { name: "Undo change", exact: true }).click();
  await expect(page.getByRole("status").filter({ hasText: "Change undone" })).toBeVisible();
  await openPage(page, 'Settings');
  await expect(row()).toContainText(before);
});
test("mobile navigation and plan snapshots remain accessible", async ({
  page,
}) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/");
  await expect(
    page.getByRole("heading", { name: "Today", exact: true }),
  ).toBeVisible();
  await openPage(page, 'Plan');
  await expect(
    page.getByRole("heading", { level: 1, name: "Plan", exact: true }),
  ).toBeVisible();
  // Plan windows are cards on a phone; the snapshot picker sits behind "Browse earlier plans".
  await expect(page.locator(".next-card").first()).toBeVisible();
  await page.getByText("Browse earlier plans", { exact: true }).click();
  await expect(
    page.getByRole("combobox", { name: "Plan snapshot" }),
  ).toBeVisible();
  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth > window.innerWidth,
  );
  expect(overflow).toBe(false);
});

test("an open setting draft retains its original revision after polling", async ({
  page,
  request,
}) => {
  await page.goto("/");
  await openPage(page, 'Settings');
  const initial = (await (await request.get("/api/state")).json()).state;
  const original = initial.settings.find(
    (x: { key: string }) => x.key === "pv_scaling",
  ).value;
  await page
    .getByRole("button", { name: /^House load scaling\b/ })
    .click();
  await page.getByRole("dialog").getByRole("spinbutton").fill("1.13");
  const changed = await request.post("/api/settings/pv_scaling", {
    data: {
      value: original === "0.99" ? "0.98" : "0.99",
      revision: initial.revision,
    },
  });
  expect(changed.ok()).toBeTruthy();
  await expect(
    page.getByText(`Settings version ${initial.revision + 1}`, {
      exact: true,
    }),
  ).toBeVisible({ timeout: 15000 });
  await page
    .getByRole("dialog")
    .getByRole("button", { name: "Save change", exact: true })
    .click();
  await expect(page.getByRole("alert")).toBeVisible();
  await expect(page.getByRole("dialog")).toBeVisible();
  const current = (await (await request.get("/api/state")).json()).state;
  expect(
    current.settings.find((x: { key: string }) => x.key === "load_scaling")
      .value,
  ).toBe(
    initial.settings.find((x: { key: string }) => x.key === "load_scaling")
      .value,
  );
  await request.post("/api/settings/pv_scaling", {
    data: { value: original, revision: current.revision },
  });
});
