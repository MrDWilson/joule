import { test, expect } from '@playwright/test';
import { todayAtLeast } from './support/clock';
// Tile sparklines: today from midnight, half-hour averages (never a single poll), a forecast tail and an honest peak.
test.beforeEach(async ({ request }) => {
  const response = await request.post('/api/telemetry/collect', { headers: { 'X-Joule-Request': '1' }, data: {} });
  expect(response.ok(), await response.text()).toBeTruthy();
});

// The longest fixture is three hours of today: early in the London day the browser's clock is moved on to 04:00.
let now = () => Date.now();
test.beforeEach(async ({ page }) => {
  now = await todayAtLeast(page, 4 * 60);
});

/** 5-minute solar intervals ending at the last whole half-hour, so each half-hour bin is fully covered. */
function fixture(values: (number | null)[], status = (v: number | null) => (v === null ? 'reset' : 'observed')) {
  const end = Math.floor((now() - 60000) / 1800000) * 1800000, start = end - values.length * 300000;
  return {
    from: new Date(start).toISOString(), to: new Date(end).toISOString(), method: 'Whole observed meter interval average power in kW.', truncated: false, limit: 1000,
    intervals: values.map((value, n) => ({ metric: 'pv', start: new Date(start + n * 300000).toISOString(), end: new Date(start + (n + 1) * 300000).toISOString(), averageKw: value, source: 'HomeAssistant', entityId: 'sensor.owned_pv', status: status(value) })),
  };
}
const card = (page: import('@playwright/test').Page) => page.getByRole('article', { name: 'Solar', exact: true });

test('a skipped-update zig-zag reads as its half-hour average, and the peak names its half-hour', async ({ page }) => {
  // Home Assistant skipped every other update: 0, 4, 0, 4 … kW per poll is 2 kW over the half-hour, not a 4 kW peak.
  await page.route('**/api/telemetry/trends?*', (r) => r.fulfill({ json: fixture(Array.from({ length: 12 }, (_, i) => (i % 2 ? 4 : 0))) }));
  await page.goto('/');
  await expect(card(page).getByRole('img', { name: /Solar power today, half-hour averages/ })).toBeVisible();
  await expect(card(page).locator('.trend-caption')).toContainText(/Today\s*peak 2\.0 kW \(half-hour, \d\d:\d\d\)/);
  await expect(card(page).locator('.metric-trend path.trend-line')).toHaveCount(1);
  // The rest of today is Predbat's forecast, dashed and faint; "now" is a dot.
  await expect(card(page).locator('.metric-trend path.trend-forecast')).toHaveCount(1);
  await expect(card(page).locator('.metric-trend circle.trend-now')).toHaveCount(1);
});

test('unknown or reset intervals stay gaps and truncation is disclosed', async ({ page }) => {
  await page.route('**/api/telemetry/trends?*', (r) => r.fulfill({ json: { ...fixture([null, null]), truncated: true } }));
  await page.goto('/');
  await expect(card(page)).toContainText('No readings yet today');
  await expect(card(page).locator('.metric-trend svg')).toHaveCount(0);
  await expect(card(page)).toContainText('Showing the latest 1000 readings only');
});

test('failed trend retrieval preserves measured totals and supports retry', async ({ page }) => {
  let fail = true;
  await page.route('**/api/telemetry/trends?*', (r) => (fail ? r.fulfill({ status: 503, json: { error: 'Owned trend outage' } }) : r.fulfill({ json: fixture([1, 1, 1, 1, 1, 1]) })));
  await page.goto('/');
  await expect(page.getByRole('alert')).toContainText('Owned trend outage');
  await expect(card(page).locator('.stat-value')).not.toContainText('—');
  await expect(card(page)).toContainText('Trend unavailable');
  fail = false;
  await page.getByRole('alert').filter({ hasText: 'Couldn’t load the 24-hour graphs' }).getByRole('button', { name: 'Try again' }).click();
  await expect(card(page).getByRole('img', { name: /Solar power today/ })).toBeVisible();
  await expect(page.getByRole('alert').filter({ hasText: 'Owned trend outage' })).toHaveCount(0);
});

test('a long run without readings is a faint wash, a short one is bridged, and neither is drawn as zero', async ({ page }) => {
  // Half an hour measured, an hour and a half missing, half an hour measured, a 30-minute gap, half an hour measured.
  const pattern = [...Array(6).fill(1), ...Array(18).fill(null), ...Array(6).fill(2), ...Array(6).fill(null), ...Array(6).fill(2)];
  await page.route('**/api/telemetry/trends?*', (r) => r.fulfill({ json: fixture(pattern, (v) => (v === null ? 'invalid' : 'observed')) }));
  await page.goto('/');
  const svg = card(page).locator('.metric-trend svg');
  await expect(svg).toBeVisible();
  await expect.poll(async () => Number(await svg.getAttribute('data-gap-bands'))).toBeGreaterThanOrEqual(1);
  await expect.poll(async () => Number(await svg.getAttribute('data-bridged-gaps'))).toBeGreaterThanOrEqual(1);
  await expect(card(page).locator('.metric-trend path.trend-line')).toHaveCount(1);
  expect(await card(page).locator('.metric-trend path.trend-line').getAttribute('d')).toMatch(/M.* M/);
});

test('the battery tile shows its level as a sparkline: measured, then planned', async ({ page }) => {
  await page.goto('/');
  const battery = page.getByRole('article', { name: 'Battery', exact: true });
  await expect(battery.getByRole('img', { name: 'Battery level, last 12 hours measured and next 12 hours planned' })).toBeVisible();
  await expect(battery.locator('.battery-gauge')).toHaveCount(0);
  await expect(battery).toContainText('Last 12 h · next 12 h planned');
});
