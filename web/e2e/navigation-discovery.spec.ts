import { test, expect } from '@playwright/test';
import { openPage } from './support/navigation';

test.use({ timezoneId: 'Europe/London' });

test('the phone tab bar reaches the everyday destinations and the header cog opens Setup', async ({ page }) => {
  // The old 11-item drawer took two taps per page; the bottom bar is one tap and Back walks back through it.
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/');
  const bar = page.getByRole('navigation', { name: 'Main navigation' });
  await expect(bar).toBeVisible();
  await expect(bar.getByRole('link')).toHaveText([/^Today/, /^Plan/, /^Insights/, /^Energy/]);
  await expect(bar.getByRole('link', { name: 'Today' })).toHaveAttribute('aria-current', 'page');
  await bar.getByRole('link', { name: /^Plan/ }).click();
  await expect(page).toHaveURL(/#\/plan$/);
  await expect(bar.getByRole('link', { name: /^Plan/ })).toHaveAttribute('aria-current', 'page');
  await page.getByRole('link', { name: 'Setup', exact: true }).click();
  await expect(page).toHaveURL(/#\/setup$/);
  await expect(page.getByRole('heading', { level: 1, name: 'Setup' })).toBeFocused();
  // Every tap target in the bar is at least 44px tall.
  for (const height of await bar.getByRole('link').evaluateAll((links) => links.map((l) => l.getBoundingClientRect().height)))
    expect(height).toBeGreaterThanOrEqual(44);
  await page.goBack();
  await expect(page).toHaveURL(/#\/plan$/);
});

test('the navigation follows the window between phone and desktop widths', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/#/insights');
  await expect(page.locator('.tabbar')).toBeVisible();
  await expect(page.locator('.sidebar')).toBeHidden();
  await page.setViewportSize({ width: 1280, height: 900 });
  await expect(page.locator('.sidebar')).toBeVisible();
  await expect(page.locator('.tabbar')).toBeHidden();
  const nav = page.getByRole('navigation', { name: 'Main navigation' });
  await expect(nav.getByRole('link', { name: /^Insights/ })).toHaveAttribute('aria-current', 'page');
  await nav.getByRole('link', { name: /^Plan/ }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'Plan', exact: true })).toBeVisible();
  await page.setViewportSize({ width: 390, height: 844 });
  await expect(page.getByRole('navigation', { name: 'Main navigation' }).getByRole('link', { name: /^Plan/ })).toHaveAttribute('aria-current', 'page');
});

test('open discovery is explicitly separate from the question draft and focused submission', async ({ page, request }) => {
  // Mislabeling unrestricted discovery invites submitting a question through the wrong action.
  const payload = await (await request.get('/api/state')).json();
  payload.ai.running = false;
  await page.route('**/api/state', route => route.fulfill({ json: payload }));
  const runs: unknown[] = [];
  // Record only the browser's request; never invoke the provider or mutate a live investigation.
  await page.route('**/api/investigations/run', route => {
    runs.push(route.request().postDataJSON());
    return route.fulfill({ status: 204 });
  });
  await page.goto('/');
  await openPage(page, 'Checks');
  const question = page.getByRole('textbox', { name: 'Your question', exact: true });
  await page.getByText('Choose the time to look at').click();
  const from = page.getByLabel('From', { exact: true });
  const to = page.getByLabel('To', { exact: true });
  await question.fill('Why was overnight import higher?');
  await from.fill('2026-09-20T00:00');
  await to.fill('2026-09-21T00:00');
  // One composer: Ask sends the question and its dates, then clears them for the next question.
  await page.getByRole('button', { name: 'Ask', exact: true }).click();
  await expect.poll(() => runs.length).toBe(1);
  expect(runs[0]).toEqual({ question: 'Why was overnight import higher?', from: '2026-09-19T23:00:00.000Z', to: '2026-09-20T23:00:00.000Z' });
  await expect(question).toHaveValue('');
  // The dates fold away once the check starts; opened again they are blank.
  await expect(from).toHaveCount(0);
  await page.getByText('Choose the time to look at').click();
  await expect(from).toHaveValue('');
  // A full check is its own, clearly named action: no question at all.
  await page.getByRole('button', { name: 'Run a full check', exact: true }).click();
  await expect.poll(() => runs.length).toBe(2);
  expect(runs[1]).toEqual({ question: null, from: null, to: null });

});
