import { test, expect } from '@playwright/test';

// The Vite dev server does not send the API's security headers, so these checks load the production build straight
// from the API (src/Joule.Api/wwwroot). CI builds it first; locally run `npm run build` and copy web/dist there.
const api = process.env.JOULE_API_URL ?? process.env.PREDBAT_API_URL;

test.beforeEach(async ({ request }) => {
  test.skip(!api, 'Needs JOULE_API_URL (run through scripts/e2e.sh).');
  const shell = await request.get(`${api}/`);
  test.skip(shell.status() !== 200 || !(await shell.text()).includes('id="root"'), 'Build web into src/Joule.Api/wwwroot to check the production headers.');
});

for (const viewport of [{ width: 1440, height: 900 }, { width: 390, height: 844 }]) {
  test(`the production build runs under the Content-Security-Policy at ${viewport.width}px`, async ({ page }) => {
    await page.setViewportSize(viewport);
    await page.addInitScript(() => {
      const w = window as unknown as { __csp: string[] };
      w.__csp = [];
      document.addEventListener('securitypolicyviolation', e => w.__csp.push(`${e.violatedDirective} ${e.blockedURI}`));
    });
    const errors: string[] = [];
    page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
    const response = await page.goto(`${api}/`);
    const headers = response!.headers();
    expect(headers['content-security-policy']).toContain("default-src 'self'");
    expect(headers['x-frame-options']).toBe('DENY');
    expect(headers['cache-control']).toBe('no-cache');
    // Charts render, and their inline styles (series colours from the tokens) are applied rather than blocked.
    const chart = page.locator('svg.tl-svg').first();
    await expect(chart).toBeVisible({ timeout: 20_000 });
    const line = chart.locator('path.tl-series').first();
    expect(await line.evaluate(el => getComputedStyle(el).stroke)).toMatch(/^rgb/);
    await page.waitForLoadState('networkidle');
    expect(await page.evaluate(() => (window as unknown as { __csp: string[] }).__csp)).toEqual([]);
    expect(errors.filter(e => /Content Security Policy|Refused to/i.test(e))).toEqual([]);
  });
}

test('hashed assets are cached for a year and compressed', async ({ request }) => {
  const html = await (await request.get(`${api}/`)).text();
  const script = /src="(\/assets\/[^"]+\.js)"/.exec(html)?.[1];
  expect(script).toBeTruthy();
  const asset = await request.get(`${api}${script}`, { headers: { 'Accept-Encoding': 'br, gzip' } });
  expect(asset.headers()['cache-control']).toBe('public, max-age=31536000, immutable');
  expect(['br', 'gzip']).toContain(asset.headers()['content-encoding']);
});
