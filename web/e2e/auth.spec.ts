import { test, expect } from '@playwright/test';
import { openPage } from './support/navigation';
import { fetchFresh, fulfillRewritten } from './support/routes';

// A route handler still reading a fetched response when the test ends would fail it as the context closes.
test.afterEach(async ({ page }) => page.unrouteAll({ behavior: 'ignoreErrors' }));

test('no-auth mode opens the dashboard and persists changes without a login', async ({ page, request }) => {
  const reset = await request.post('/api/mode', { headers: { 'X-Joule-Request': '1' }, data: { mode: 'Monitor' } });
  expect(reset.ok()).toBeTruthy();
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Today', exact: true })).toBeVisible();
  await expect(page.getByLabel('Server access key')).toHaveCount(0);
  await openPage(page, 'Settings');
  await page.getByRole('radio', { name: /^Suggest changes/ }).click();
  await expect.poll(async () => (await (await request.get('/api/state')).json()).state.mode).toBe('Recommend');
  // Browser form submissions must not be able to change settings.
  expect((await request.post('/api/mode', { data: { mode: 'Monitor' } })).status()).toBe(403);
});

test('an upstream access denial does not request an application key', async ({ page }) => {
  await page.route('**/api/state', route => route.fulfill({ status: 401, contentType: 'text/html', body: 'Access denied' }));
  await page.goto('/');
  await expect(page.getByText(/access was denied by the server/i)).toBeVisible();
  await expect(page.getByLabel('Server access key')).toHaveCount(0);
  await page.unroute('**/api/state');
  await page.reload();
  await expect(page.getByRole('heading', { name: 'Today', exact: true })).toBeVisible();
});

test('the key form says a key was rejected only once the server has turned it down', async ({ page }) => {
  let release: () => void = () => {};
  let keyed = 0;
  await page.route('**/api/state', async route => {
    // Loads without a key are asked for one; the reply to the submitted key is held so the in-flight state can be checked.
    if (route.request().headers()['x-access-key']) {
      keyed++;
      await new Promise<void>(resolve => { release = resolve; });
    }
    await route.fulfill({ status: 401, json: { error: 'Enter your application access key.', authMode: 'AccessKey' } });
  });
  await page.goto('/');
  const key = page.getByLabel('Server access key');
  await expect(key).toBeVisible();
  await expect(page.getByText("That key wasn't accepted", { exact: false })).toHaveCount(0);
  await key.fill('wrong-key');
  await page.getByRole('button', { name: 'Connect' }).click();
  await expect.poll(() => keyed).toBe(1);
  await expect(page.getByText("That key wasn't accepted", { exact: false })).toHaveCount(0);
  release();
  await expect(page.getByText("That key wasn't accepted", { exact: false })).toBeVisible();
});

test('an explicit application key challenge still offers the key form', async ({ page }) => {
  await page.route('**/api/state', route => route.fulfill({ status: 401, json: { error: 'Enter your application access key.', authMode: 'AccessKey' } }));
  await page.goto('/');
  await expect(page.getByLabel('Server access key')).toBeVisible();
});

for (const available of [false, true]) {
  test(`ChatGPT ${available ? 'offers local sign-in' : 'explains remote setup without offering sign-in'}`, async ({ page }) => {
    await page.route('**/api/state', async route => {
      const response = await fetchFresh(route);
      const payload = await response.json();
      payload.ai.chatGptConnected = false;
      payload.ai.chatGptLocalSignInAvailable = available;
      await fulfillRewritten(route, response, payload);
    });
    await page.goto('/');
    await openPage(page, 'AI checks');
    await page.getByRole('combobox', { name: 'Provider', exact: true }).selectOption('ChatGpt');

    if (available) {
      await expect(page.getByRole('button', { name: 'Continue with ChatGPT' })).toBeVisible();
    } else {
      await expect(page.getByRole('button', { name: /Connect ChatGPT|Continue with ChatGPT/ })).toHaveCount(0);
      // The remote steps sit behind "Setup help" so the provider card stays short.
      await page.getByText('Setup help: connecting from another computer').click();
      await expect(page.getByText(/local loopback callback/i)).toBeVisible();
      await expect(page.getByText('http://127.0.0.1:5080', { exact: true })).toBeVisible();
      await expect(page.getByText('auth/chatgpt-credentials.json', { exact: true })).toBeVisible();
      await expect(page.getByText('chatgpt-host.json', { exact: true })).toBeVisible();
      await expect(page.getByRole('link', { name: /^Official remote setup guide/ })).toHaveAttribute('href', 'https://developers.openai.com/siwc/token-sharing-open-source/self-hosted-vms');
    }
    await page.unrouteAll({ behavior: 'wait' });
  });
}

test('a remote browser origin cannot offer local ChatGPT sign-in even when the server allows it', async ({ page, baseURL }) => {
  const remoteOrigin = 'http://predbat-remote.test';
  // Proxy the real app through a non-loopback browser origin without depending on DNS.
  await page.route(`${remoteOrigin}/**`, async route => {
    const url = route.request().url().replace(remoteOrigin, baseURL!);
    const response = await fetchFresh(route, { url });
    if (new URL(url).pathname === '/api/state') {
      const payload = await response.json();
      payload.ai.chatGptConnected = false;
      payload.ai.chatGptLocalSignInAvailable = true;
      await fulfillRewritten(route, response, payload);
    } else {
      await route.fulfill({ response });
    }
  });
  await page.goto(remoteOrigin);
  await openPage(page, 'AI checks');
  await page.getByRole('combobox', { name: 'Provider', exact: true }).selectOption('ChatGpt');
  await expect(page.getByRole('button', { name: /Connect ChatGPT|Continue with ChatGPT/ })).toHaveCount(0);
  await page.getByText('Setup help: connecting from another computer').click();
  await expect(page.getByRole('link', { name: /^Official remote setup guide/ })).toBeVisible();
  // Drain the synthetic-origin proxy before removing it; outstanding timeline
  // requests otherwise race unroute's automatic continuation to nonexistent DNS.
  await page.waitForLoadState('networkidle');
  await page.unrouteAll({ behavior: 'wait' });
});
