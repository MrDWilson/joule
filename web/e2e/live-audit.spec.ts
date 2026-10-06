import { test, expect, type Page } from '@playwright/test';
import { openPage } from './support/navigation';
const nav = openPage;
test('failed investigations remain visible with inspectable sanitized rejection evidence', async ({ page, request }) => {
  const payload = await (await request.get('/api/state')).json();
  const record = { id: 'owned-rejected', at: new Date().toISOString(), title: 'Owned rejected investigation', status: 'Failed', provider: 'ChatGpt', category: 'Analysis', summary: 'Rejected output', evidence: [], steps: [], toolEvidence: [{ id: 'owned-model', kind: 'model', retrievedAt: new Date().toISOString(), request: 'Rejected final reply', success: false, error: 'Invalid proposals', resultJson: JSON.stringify({ reply: 'Owned sanitized model excerpt [redacted]' }), sourceReferences: [] }] };
  payload.state.lastAnalysis = null;
  payload.state.investigations = [record];
  await page.route('**/api/state', route => route.fulfill({ json: payload }));
  await page.route('**/api/investigations/owned-rejected', route => route.fulfill({ json: record }));
  await page.goto('/');
  await expect(page.getByText('No investigations yet', { exact: true })).toBeHidden();
  // On Today a check that didn't finish is a grey dot in the AI card, never a problem.
  const ai = page.getByRole('region', { name: 'AI checks' });
  await expect(ai).toContainText('1 AI check today');
  await expect(ai.locator('.ai-legend-item')).toHaveText(['1 didn’t finish']);
  await expect(ai.locator('.run-dots .run-dot.v-didnt_finish')).toHaveCount(1);
  await expect(ai).not.toContainText('Problem');
  await nav(page, 'Checks');
  await page.locator('.run-row.is-unfinished .run-link').first().click();
  await page.getByText('Sources', { exact: true }).click();
  await page.getByText("An AI answer that failed Joule's checks").click();
  await expect(page.getByText('Owned sanitized model excerpt [redacted]', { exact: true })).toBeVisible();
});
test('structured observed sensors and invalid raw readings have distinct diagnostics', async ({ page }) => {
  const at = new Date().toISOString();
  await page.route('**/api/telemetry/status', route => route.fulfill({ json: { demo: false, configured: true, homeAssistantDirect: false, lastSource: 'Predbat mirror', timeZone: 'Europe/London', lastCollection: at, error: null, entityMappings: { intelligent_slots: 'binary_sensor.owned_slots', ev: 'sensor.owned_ev' }, missingMappings: [], maxGapMinutes: 15, latestReadings: { intelligent_slots: { value: null, unit: '', time: at, status: 'observed', entityId: 'binary_sensor.owned_slots', source: 'Predbat mirror', rawState: 'off', rawUnit: null }, ev: { value: null, unit: 'kWh', time: at, status: 'invalid', entityId: 'sensor.owned_ev', source: 'Predbat mirror', rawState: 'unknown', rawUnit: 'kWh' } } } }));
  await page.goto('/');
  await nav(page, 'Energy');
  const sensors = page.locator('.sensor-health');
  // The unreadable meter is listed as an exception straight away; the healthy slot sensor is behind "Show all".
  const ev = sensors.locator('.sensor-row').filter({ hasText: 'Car charging' });
  await expect(ev).toContainText("Can't read");
  await ev.locator('summary').click();
  await expect(ev).toContainText('unknown kWh');
  await expect(ev.locator('code').first()).toHaveText('sensor.owned_ev');
  await sensors.getByRole('button', { name: /^Show all/ }).click();
  const slots = sensors.locator('.sensor-row').filter({ hasText: 'Octopus smart-charge slots' });
  await expect(slots).toContainText('None now');
  await expect(slots).toContainText('Live');
});
test('custom dates save as a report for exactly the days shown', async ({ page, request }) => {
  const { timeZone } = await (await request.get('/api/telemetry/status')).json();
  const day = (ago: number) => new Intl.DateTimeFormat('en-CA', { timeZone, year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date(Date.now() - ago * 86400000));
  const windows: string[] = [];
  page.on('request', r => {
    if (!r.url().includes('/api/telemetry/summary?')) return;
    const q = new URL(r.url()).searchParams;
    windows.push(`${q.get('from')}|${q.get('to')}`);
  });
  await page.goto(`/#/energy?from=${day(3)}&to=${day(2)}`);
  await expect(page.locator('.energy-chips')).toBeVisible();
  const submitted = page.waitForRequest(r => r.url().endsWith('/api/reports/generate') && r.method() === 'POST');
  await page.getByRole('button', { name: 'Save as report', exact: true }).click();
  const body = (await submitted).postDataJSON();
  expect(body.kind).toBe('Custom');
  // The same window the figures on screen were read for: two whole local days.
  expect(windows).toContain(`${body.from}|${body.to}`);
  expect(Date.parse(body.to) - Date.parse(body.from)).toBeGreaterThan(46 * 3600000);
  // Two whole local days, midnight to midnight.
  expect(new Intl.DateTimeFormat('en-GB', { timeZone, hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }).format(new Date(body.from))).toBe('00:00');
  expect(new Intl.DateTimeFormat('en-GB', { timeZone, hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }).format(new Date(body.to))).toBe('00:00');
});

test('historical Predbat abbreviations use correct window labels and export tariffs', async ({ page, request }) => {
  const at = new Date(Date.now() + 3600000).toISOString();
  const payload = await (await request.get('/api/state')).json();
  payload.plan = { id: 'owned-alias', at, source: 'Predbat', slots: [
    { time: at, durationMinutes: 30, action: 'Chrg', loadForecast: 1, pvForecast: 0, socForecast: 50, importRate: 7, exportRate: 15, cost: 0 },
    { time: new Date(Date.parse(at) + 1800000).toISOString(), durationMinutes: 30, action: 'Exp', loadForecast: 1, pvForecast: 0, socForecast: 50, importRate: 31, exportRate: 16, cost: 0 },
  ] };
  await page.route('**/api/state', route => route.fulfill({ json: payload }));
  await page.goto('/');
  const coming = page.getByRole('region', { name: 'Coming up' });
  await expect(coming.getByText('Charge from the grid', { exact: true })).toBeVisible();
  const exporting = coming.locator('.window-item').filter({ hasText: 'Export battery to the grid' });
  await expect(exporting).toContainText('16p');
  await expect(exporting).not.toContainText('31p');
});
