import { test, expect } from '@playwright/test';

// scripts/e2e.sh starts this spec's API with App__AccessKey set to this value.
const key = 'e2e-session-key-0123456789';

test('a correct access key is remembered, so a new tab does not ask again', async ({ page, context }) => {
  await page.goto('/');
  const field = page.getByLabel(/access key/i);
  await expect(field).toBeVisible();
  await field.fill(key);
  await field.press('Enter');
  await expect(field).toHaveCount(0);
  const cookies = await context.cookies();
  const session = cookies.find(c => c.name === 'joule_session');
  expect(session).toBeTruthy();
  expect(session!.httpOnly).toBe(true);
  expect(session!.sameSite).toBe('Strict');

  // sessionStorage is per tab; the HttpOnly cookie is shared by the browser, so the second tab goes straight in.
  const second = await context.newPage();
  await second.goto('/');
  await expect(second.locator('main')).toBeVisible();
  await expect(second.getByLabel(/access key/i)).toHaveCount(0);
  const state = await second.request.get('/api/state?view=header');
  expect(state.status()).toBe(200);
});

test('a cookie alone cannot change settings without the dashboard header', async ({ page, context }) => {
  await page.goto('/');
  await page.getByLabel(/access key/i).fill(key);
  await page.getByLabel(/access key/i).press('Enter');
  await expect(page.getByLabel(/access key/i)).toHaveCount(0);
  // The page request context shares the browser's cookies but adds no custom headers.
  const bare = await context.request.post('/api/mode', { data: { mode: 'Monitor' } });
  expect(bare.status()).toBe(403);
  const dashboard = await context.request.post('/api/mode', { data: { mode: 'Monitor' }, headers: { 'X-Joule-Request': '1' } });
  expect(dashboard.status()).toBe(200);
});

test('without a key or cookie the API asks for one', async ({ browser }) => {
  const fresh = await browser.newContext();
  const response = await fresh.request.get(new URL('/api/state', test.info().project.use.baseURL).toString());
  expect(response.status()).toBe(401);
  expect((await response.json()).authMode).toBe('AccessKey');
  await fresh.close();
});

test('one mistyped key, polled over and over by the sign-in screen, never locks out the right key', async ({ page }) => {
  const typo = key.slice(0, -1);
  const statuses: number[] = [];
  page.on('response', r => { if (r.url().includes('/api/')) statuses.push(r.status()); });
  await page.goto('/');
  const field = page.getByLabel(/access key/i);
  await field.fill(typo);
  await field.press('Enter');
  // The sign-in screen keeps polling with whatever was typed. Replay a few minutes' worth of those polls.
  for (let i = 0; i < 30; i++) {
    const poll = await page.request.get('/api/state', { headers: { 'X-Access-Key': typo } });
    expect(poll.status()).toBe(401);
    expect((await poll.json()).reason).toBe('wrong_key');
  }
  await expect(field).toBeVisible();
  await field.fill(key);
  await field.press('Enter');
  await expect(field).toHaveCount(0);
  await expect(page.locator('main')).toBeVisible();
  expect(statuses).not.toContain(429);
});
