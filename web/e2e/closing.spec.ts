import { test, expect, type Page } from '@playwright/test';
import { openPage } from './support/navigation';

// One disposable demo per spec file: the tests run in order and each leaves the demo as it found it (Undo / Reopen).
test.afterEach(async ({ page }) => page.unrouteAll({ behavior: 'ignoreErrors' }));

const TODO = "Find your inverter's daily export sensor in Home Assistant";
const FILE = 'Add export_today so Predbat can compare planned and actual export';
const FINDING = "Predbat can't see today's export";

const toast = (page: Page, text: string | RegExp) => page.getByRole('status').filter({ hasText: text });

test('a to-do closes as not needed from Suggestions, the count drops at once, and Undo brings it back', async ({ page }) => {
  await page.goto('/');
  await openPage(page, 'Suggestions');
  await expect(page.getByRole('button', { name: 'Waiting (3)' })).toBeVisible();
  const card = page.getByRole('article', { name: TODO });
  await card.getByRole('button', { name: 'Not needed' }).click();

  await expect(toast(page, "Closed as not needed. Joule won't raise it again for 30 days.")).toBeVisible();
  await expect(card).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Waiting (2)' })).toBeVisible();
  await expect(page.getByRole('navigation', { name: 'Main navigation' }).getByRole('link', { name: /^Insights/ })).toContainText('2');

  await page.getByRole('button', { name: 'Undo' }).click();
  await expect(toast(page, 'Back on your list.')).toBeVisible();
  await expect(page.getByRole('article', { name: TODO })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Waiting (3)' })).toBeVisible();
});

test('Dismiss asks for an optional reason; the item shows under Closed with it and can be reopened', async ({ page }) => {
  await page.goto('/');
  await openPage(page, 'Suggestions');
  await page.getByRole('article', { name: FILE }).getByRole('button', { name: 'Dismiss…' }).click();
  const dialog = page.getByRole('dialog', { name: 'Dismiss this file edit?' });
  await expect(dialog).toContainText("Joule won't raise it again for 30 days.");
  await dialog.getByRole('textbox', { name: 'Why? (optional)' }).fill('We export through a different meter.');
  await dialog.getByRole('button', { name: 'Dismiss', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  await expect(page.getByRole('article', { name: FILE })).toHaveCount(0);

  await page.getByRole('button', { name: /^Closed/ }).click();
  const row = page.locator('.closed-row').filter({ hasText: 'Add export_today' });
  await expect(row).toContainText('You dismissed this');
  await expect(row).toContainText('Your note: “We export through a different meter.”');
  await row.getByRole('button', { name: 'Reopen' }).click();
  await expect(row).toHaveCount(0);
  await page.getByRole('button', { name: /^Waiting/ }).click();
  await expect(page.getByRole('article', { name: FILE })).toBeVisible();
});

test('a whole finding is dismissed from its check with everything from it, hidden from the list until Show closed', async ({ page }) => {
  await page.goto('/');
  await openPage(page, 'Checks');
  await page.locator('.run-row').filter({ hasText: FINDING }).getByRole('link').click();
  await page.getByRole('button', { name: 'Dismiss finding…' }).click();
  const dialog = page.getByRole('dialog', { name: 'Dismiss this finding?' });
  await expect(dialog).toContainText('The 2 things from it still waiting for you close too.');
  await dialog.getByRole('button', { name: 'Dismiss', exact: true }).click();
  await expect(toast(page, /^Finding dismissed, with everything from it\./)).toBeVisible();
  await expect(page.locator('.finding-closed-note')).toContainText('You dismissed this finding');

  // Its file edit and to-do left Needs you with it; the closed check leaves Recent checks once it isn't open.
  const needs = page.locator('.insights-needs');
  await expect(needs.getByText(TODO)).toHaveCount(0);
  await expect(needs.getByText('Add', { exact: false }).filter({ hasText: 'export_today' })).toHaveCount(0);
  await page.getByRole('button', { name: 'Close this check' }).click();
  const recent = page.locator('.recent-checks');
  await expect(recent.locator('.run-row').filter({ hasText: FINDING })).toHaveCount(0);
  await recent.getByRole('button', { name: 'Show closed (1)' }).click();
  const closedRow = recent.locator('.run-row').filter({ hasText: FINDING });
  await expect(closedRow).toContainText('Closed · You dismissed this');

  // Reopening the finding brings back exactly what closed with it.
  await closedRow.getByRole('link').click();
  await page.locator('.finding-closed-note').getByRole('button', { name: 'Reopen' }).click();
  await expect(page.locator('.finding-closed-note')).toHaveCount(0);
  await expect(page.locator('.insights-needs').getByText(TODO)).toBeVisible();
});

test('replying "nah" closes the to-do; the reply says so and Undo is offered', async ({ page }) => {
  await page.goto('/');
  await openPage(page, 'Suggestions');
  const card = page.getByRole('article', { name: TODO });
  await card.getByRole('button', { name: 'Reply', exact: true }).click();
  await card.getByRole('textbox', { name: 'Your reply' }).fill('Nah, leave it');
  await card.getByRole('button', { name: 'Send reply' }).click();
  const dialog = page.getByRole('dialog', { name: 'The AI agreed' });
  await expect(dialog).toContainText("Closed — I won't raise this again for 30 days.");
  await dialog.getByRole('button', { name: 'Undo' }).click();
  await expect(dialog).toHaveCount(0);
  await expect(page.getByRole('article', { name: TODO })).toBeVisible();
});

test('after an answer, a quick reply closes it as not needed', async ({ page }) => {
  await page.goto('/');
  await openPage(page, 'Suggestions');
  const card = page.getByRole('article', { name: TODO });
  await card.getByRole('button', { name: 'Reply again' }).click();
  await card.getByRole('textbox', { name: 'Your reply' }).fill('Why do I need this?');
  await card.getByRole('button', { name: 'Send reply' }).click();
  await expect(card.getByRole('list', { name: 'Replies' }).getByText('Answer', { exact: true }).last()).toBeVisible();
  // The question isn't the reason the next check reads: the note is just "Not needed".
  const sent = page.waitForRequest((r) => r.method() === 'POST' && /\/dismiss$/.test(r.url()));
  await card.getByRole('group', { name: 'Quick replies' }).getByRole('button', { name: 'Not needed: close it' }).click();
  expect((await sent).postDataJSON()).toMatchObject({ note: 'Not needed', outcome: 'not_needed' });
  await expect(toast(page, /^Closed as not needed\./)).toBeVisible();
  await expect(page.getByRole('article', { name: TODO })).toHaveCount(0);
  await page.getByRole('button', { name: 'Undo' }).click();
  await expect(page.getByRole('article', { name: TODO })).toBeVisible();
});

test("Today's Needs you closes a row on a phone and its count drops", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/');
  await openPage(page, 'Today');
  const needs = page.locator('section.needs-you');
  await expect(needs.locator('.needs-count')).toHaveText('3');
  await needs.getByRole('button', { name: /^Not needed: / }).first().click();
  await expect(needs.locator('.needs-count')).toHaveText('2');
  expect(await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth)).toBeLessThanOrEqual(0);
  await page.getByRole('button', { name: 'Undo' }).click();
  await expect(needs.locator('.needs-count')).toHaveText('3');
});
