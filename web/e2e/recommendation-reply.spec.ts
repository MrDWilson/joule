import { test, expect, type Page } from '@playwright/test';
import { openPage } from './support/navigation';
import { fetchFresh, fulfillRewritten } from './support/routes';

// A route handler still reading a fetched response when the test ends would fail it as the context closes.
test.afterEach(async ({ page }) => page.unrouteAll({ behavior: 'ignoreErrors' }));

// One disposable demo per spec file: the tests run in order against the same scripted demo.
async function openSuggestions(page: Page) {
  await page.goto('/');
  await openPage(page, 'Suggestions');
}

test('a disputed reply keeps the suggestion open with quick replies; an accepted one is remembered and closed', async ({ page }) => {
  await openSuggestions(page);
  const card = page.locator('article.suggestion-card').filter({ hasText: 'Bring the evening load forecast closer to reality' });
  await expect(card).toBeVisible();

  await card.getByRole('button', { name: 'Reply', exact: true }).click();
  await card.getByRole('textbox', { name: 'Your reply' }).fill('This is not right.');
  await card.getByRole('button', { name: 'Send reply' }).click();
  const replies = card.getByRole('list', { name: 'Replies' });
  await expect(replies.getByText('Disagrees', { exact: true })).toBeVisible();
  await expect(replies).toContainText('The evidence still supports this');
  const quick = card.getByRole('group', { name: 'Quick replies' });
  // Quick replies follow the answer: Thanks only acknowledges, "I'll do it" is for to-dos, Still disagree follows a disagreement.
  await expect(quick.getByRole('button', { name: 'Thanks', exact: true })).toBeVisible();
  await expect(quick.getByRole('button', { name: "I'll do it" })).toHaveCount(0);
  await expect(quick.getByRole('button', { name: 'Ask something else' })).toHaveCount(0);
  await quick.getByRole('button', { name: 'Still disagree' }).click();
  const box = card.getByRole('textbox', { name: 'Your reply' });
  await expect(box).toHaveValue('I still disagree: ');
  await box.fill('We cook late most evenings, so we prefer the higher forecast.');
  await card.getByRole('button', { name: 'Send reply' }).click();
  const dialog = page.getByRole('dialog', { name: 'The AI agreed' });
  await expect(dialog).toBeVisible();
  await expect(dialog).toContainText('Added to what Joule knows about your home');
  // Remembered as a third-person fact, not a quote of the reply.
  await expect(dialog).toContainText('The household cooks late most evenings');
  await dialog.getByRole('button', { name: 'Close', exact: true }).click();

  await expect(card).toHaveCount(0);
  await page.getByRole('button', { name: /^Closed/ }).click();
  const closed = page.locator('.closed-row').filter({ hasText: 'Bring the evening load forecast closer to reality' });
  await expect(closed).toContainText('Closed after your reply');
  await expect(closed).toContainText('Your note: “We cook late most evenings, so we prefer the higher forecast.”');

  await openPage(page, 'Insights');
  await page.getByRole('button', { name: /What Joule knows about your home/ }).click();
  const memory = page.getByRole('list', { name: 'Remembered facts' });
  await expect(memory).toContainText('The household cooks late most evenings');
  await expect(memory.getByText(/^From your reply · /)).toBeVisible();
  // Removing a fact can be undone.
  await memory.getByRole('button', { name: /^Remove: The household cooks late/ }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Removed “The household cooks late' })).toBeVisible();
  await page.getByRole('button', { name: 'Undo' }).click();
  await expect(page.getByRole('list', { name: 'Remembered facts' })).toContainText('The household cooks late most evenings');
});

test('a configuration file edit can be copied, marked applied, taken back and dismissed with a note', async ({ page, context }) => {
  await context.grantPermissions(['clipboard-read', 'clipboard-write']);
  await page.setViewportSize({ width: 390, height: 900 });
  await openSuggestions(page);

  const card = page.getByRole('article', { name: 'Add export_today so Predbat can compare planned and actual export' });
  await expect(card).toBeVisible();
  await expect(card).toContainText('File edit · Waiting for you');
  await expect(card.locator('.diff-add')).toHaveCount(2);
  await expect(card.locator('.diff-remove')).toHaveCount(0);
  expect(await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth)).toBeLessThanOrEqual(0);

  await card.getByRole('button', { name: 'Copy snippet' }).click();
  await expect(card.getByRole('button', { name: 'Copied' })).toBeVisible();
  expect(await page.evaluate(() => navigator.clipboard.readText())).toBe('  export_today:\n    - sensor.demo_inverter_export_today');

  await card.getByRole('button', { name: 'Mark as applied' }).click();
  await expect(card).toContainText('File edit · You applied this');
  await expect(card).toContainText('The next check confirms the edit took effect.');
  // Applied: no quick replies, and Undo is a plain secondary action.
  await expect(card.getByRole('group', { name: 'Quick replies' })).toHaveCount(0);
  await card.getByRole('button', { name: 'Undo marking this applied' }).click();
  await expect(card).toContainText('File edit · Waiting for you');
  await expect(card.getByRole('button', { name: 'Mark as applied' })).toBeVisible();

  // On a phone the reply opens as a sheet above the keyboard.
  await card.getByRole('button', { name: 'Reply', exact: true }).click();
  const sheet = page.getByRole('dialog', { name: /Reply about/ });
  await expect(sheet).toBeVisible();
  await sheet.getByRole('textbox', { name: 'Your reply' }).fill('Export is measured by a separate meter.');
  await sheet.getByRole('button', { name: 'Dismiss with this note' }).click();
  await expect(card).toHaveCount(0);
  await expect(page.getByRole('article', { name: "Find your inverter's daily export sensor in Home Assistant" })).toBeVisible();

  await page.getByRole('button', { name: /^Closed/ }).click();
  const closed = page.locator('.closed-row').filter({ hasText: 'Add export_today so Predbat can compare planned and actual export' });
  await expect(closed).toContainText('You dismissed this');
  await expect(closed).toContainText('Your note: “Export is measured by a separate meter.”');
  await closed.getByRole('button', { name: 'Reopen' }).click();
  await page.getByRole('button', { name: /^Waiting/ }).click();
  await expect(page.getByRole('article', { name: 'Add export_today so Predbat can compare planned and actual export' })).toBeVisible();
});

test('when the AI cannot read a reply the item stays open, the note is kept and Try again sends it again', async ({ page }) => {
  // A live provider without credentials cannot answer: nothing is dismissed and the note isn't offered as memory.
  let calls = 0;
  await page.route('**/api/proposals/*/reply', route => {
    calls++;
    return route.fulfill({ json: {
      verdict: 'unavailable', reply: "The AI couldn't read your reply just now. Nothing was dismissed.",
      retired: false, memory: null, suggestedMemory: null, notice: null, provider: 'Api', thread: [],
    } });
  });
  const remembered: unknown[] = [];
  await page.route('**/api/memory', route => {
    if (route.request().method() === 'POST') { remembered.push(route.request().postDataJSON()); return route.fulfill({ json: { ok: true } }); }
    return route.fallback();
  });
  await page.route('**/api/state', async route => {
    const response = await fetchFresh(route); const payload = await response.json();
    payload.state.proposals = [{ ...payload.state.proposals[0], id: 'fallback-fixture', status: 'Pending', thread: [] }];
    await fulfillRewritten(route, response, payload);
  });
  await openSuggestions(page);
  const card = page.locator('article.suggestion-card').first();
  await card.getByRole('button', { name: 'Reply', exact: true }).click();
  await card.getByRole('textbox', { name: 'Your reply' }).fill('The car is never charged from the battery.');
  await card.getByRole('button', { name: 'Send reply' }).click();
  const failed = card.getByRole('alert');
  await expect(failed).toContainText('Nothing was dismissed');
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(card.getByRole('textbox', { name: 'Your reply' })).toHaveValue('The car is never charged from the battery.');
  await failed.getByRole('button', { name: 'Try again' }).click();
  await expect.poll(() => calls).toBe(2);
  await expect(card).toBeVisible();
  expect(remembered).toEqual([]);
  // The reply refreshes state; don't leave that rewritten poll in flight for the next test.
  await page.unrouteAll({ behavior: 'ignoreErrors' });
});

test('on a phone a reply the AI cannot read shows its failure and Try again inside the reply sheet', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  let calls = 0;
  await page.route('**/api/proposals/*/reply', route => {
    calls++;
    return route.fulfill({ json: {
      verdict: 'unavailable', reply: "The AI couldn't read your reply just now. Nothing was dismissed.",
      retired: false, memory: null, suggestedMemory: null, notice: null, provider: 'Api', thread: [],
    } });
  });
  await page.route('**/api/state', async route => {
    const response = await fetchFresh(route); const payload = await response.json();
    payload.state.proposals = [{ ...payload.state.proposals[0], id: 'fallback-fixture', status: 'Pending', thread: [] }];
    await fulfillRewritten(route, response, payload);
  });
  await openSuggestions(page);
  const card = page.locator('article.suggestion-card').first();
  await card.getByRole('button', { name: 'Reply', exact: true }).click();
  const sheet = page.getByRole('dialog', { name: /Reply about/ });
  await sheet.getByRole('textbox', { name: 'Your reply' }).fill('The car is never charged from the battery.');
  await sheet.getByRole('button', { name: 'Send reply' }).click();
  // The failure is in the sheet the user is looking at, not behind its backdrop.
  const failed = sheet.getByRole('alert');
  await expect(failed).toContainText('Nothing was dismissed');
  await expect(failed).toBeInViewport();
  await expect(page.getByRole('alert').filter({ hasText: 'Nothing was dismissed' })).toHaveCount(1);
  await expect(sheet.getByRole('textbox', { name: 'Your reply' })).toHaveValue('The car is never charged from the battery.');
  await failed.getByRole('button', { name: 'Try again' }).click();
  await expect.poll(() => calls).toBe(2);
  // Closing the sheet leaves the failure in the card, still with Try again.
  await sheet.getByRole('button', { name: 'Close reply' }).click();
  await expect(card.getByRole('alert')).toContainText('Nothing was dismissed');
  await expect(card.getByRole('alert').getByRole('button', { name: 'Try again' })).toBeVisible();
  await page.unrouteAll({ behavior: 'ignoreErrors' });
});

test('a slow AI reply (longer than the 20 s poll deadline) still arrives and is shown', async ({ page }) => {
  // Live replies call the model synchronously and often take more than 20 s; the browser must not give up on them.
  test.setTimeout(90_000);
  let aborted = false;
  await page.route('**/api/proposals/*/reply', async route => {
    await new Promise(resolve => setTimeout(resolve, 25_000));
    await route.fulfill({ json: {
      verdict: 'accept', reply: 'That makes sense. I have noted that the household cooks late.',
      retired: true, memory: null, suggestedMemory: null, notice: null, provider: 'ChatGpt', thread: [],
    } }).catch(() => { aborted = true; });
  });
  await page.route('**/api/state', async route => {
    const response = await fetchFresh(route); const payload = await response.json();
    payload.state.proposals = [{ ...payload.state.proposals[0], id: 'slow-reply-fixture', status: 'Pending', thread: [] }];
    await fulfillRewritten(route, response, payload);
  });
  await openSuggestions(page);
  const card = page.locator('article.suggestion-card').first();
  await card.getByRole('button', { name: 'Reply', exact: true }).click();
  await card.getByRole('textbox', { name: 'Your reply' }).fill('We cook late most evenings.');
  await card.getByRole('button', { name: 'Send reply' }).click();
  await expect(card.getByRole('status')).toContainText('Joule is reading your reply');
  await page.waitForTimeout(21_000);
  await expect(page.getByText('Joule took too long to answer.')).toHaveCount(0);
  const dialog = page.getByRole('dialog', { name: 'The AI agreed' });
  await expect(dialog).toBeVisible({ timeout: 15_000 });
  await expect(dialog).toContainText('cooks late');
  expect(aborted).toBe(false);
});
