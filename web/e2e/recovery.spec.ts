import { test, expect, type Page } from '@playwright/test';
import { openPage } from './support/navigation';

const nav = openPage;

test('a rejected stale change remains visible after successful background polling', async ({ page, request }) => {
  await page.clock.install();
  await page.goto('/');
  await nav(page, 'Settings');
  const initial = (await (await request.get('/api/state')).json()).state;
  const pv = initial.settings.find((s: { key: string }) => s.key === 'pv_scaling');
  await page.getByRole('button', { name: /^House load scaling\b/ }).click();
  await page.getByRole('dialog').getByRole('spinbutton').fill('1.13');
  const changed = await request.post('/api/settings/pv_scaling', { data: { revision: initial.revision, value: pv.value === '0.99' ? '0.98' : '0.99' } });
  expect(changed.ok(), await changed.text()).toBeTruthy();
  await page.getByRole('dialog').getByRole('button', { name: 'Save change', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText(/changed|revision|stale/i);
  const poll = page.waitForResponse(r => r.url().endsWith('/api/state') && r.ok());
  await page.clock.fastForward(11000);
  await poll;
  await expect(page.getByRole('alert')).toContainText(/changed|revision|stale/i);
  await page.getByRole('button', { name: 'Dismiss error' }).click();
  await expect(page.getByRole('alert')).toBeHidden();
});

test('retry files recovers the selected redacted view after a transient content failure', async ({ page }) => {
  let fail = true;
  await page.route('**/api/files/*/view?*', async route => {
    if (fail) { fail = false; await route.fulfill({ status: 503, json: { error: 'Owned test fixture: transient file content failure' } }); }
    else await route.continue();
  });
  await page.goto('/');
  await nav(page, 'Files');
  await expect(page.getByRole('alert')).toContainText('transient file content failure');
  await page.getByRole('button', { name: 'Retry files' }).click();
  // The masked file sits behind "Show file layout".
  await page.getByText('Show file layout').click();
  await expect(page.locator('.file-text')).toBeVisible();
  await expect(page.getByRole('alert')).toBeHidden();
});

test('AI preferences prevent schedules outside the server safety limits before submitting', async ({ page, request }) => {
  await page.goto('/');
  await nav(page, 'AI checks');
  // The schedule's limits live with the Automatic checks switch, which saves as you flip it; off, they are disabled.
  const schedule = page.getByRole('region', { name: 'When Joule checks' });
  const interval = schedule.getByLabel('At least this far apart (minutes)');
  const runs = schedule.getByLabel('Most AI checks a day');
  const automatic = schedule.getByRole('switch', { name: 'Automatic checks' });
  // The demo runs its checks on their own; turned off, the limits are disabled and the triggers fold away.
  await expect(automatic).toBeChecked();
  await automatic.click();
  await expect(page.getByRole('status').filter({ hasText: 'Automatic checks are off.' })).toBeVisible();
  await expect(interval).toBeDisabled();
  await expect(schedule.getByText('In between, a quick check')).toHaveCount(0);
  await automatic.click();
  await expect(page.getByRole('status').filter({ hasText: 'Automatic checks are on.' })).toBeVisible();
  await expect(interval).toBeEnabled();
  const original = (await (await request.get('/api/state')).json()).state.ai;
  expect(original.scheduled).toBe(true);
  let saves = 0;
  page.on('request', r => { if (r.url().endsWith('/api/ai/preferences') && r.method() === 'POST') saves++; });
  const save = page.getByRole('region', { name: 'AI provider' }).getByRole('button', { name: 'Save', exact: true });
  const saveLimits = schedule.getByRole('button', { name: 'Save limits' });
  for (const bad of ['5', '1441']) {
    await interval.fill(bad);
    await saveLimits.click();
    await expect(interval).toBeFocused();
  }
  await interval.fill('15');
  await runs.fill('97');
  await saveLimits.click();
  await expect(runs).toBeFocused();
  await runs.fill('96');
  // Token prices only apply to a priced API; the browser still refuses values the server would.
  await page.getByRole('combobox', { name: 'Provider', exact: true }).selectOption('Api');
  for (const label of ['Input price, US$ per 1M tokens', 'Output price, US$ per 1M tokens']) {
    const rate = page.getByLabel(label);
    await rate.fill('10001');
    await save.click();
    await expect(rate).toBeFocused();
    await rate.fill('0');
  }
  // An API that isn't set up on the server can't be saved at all.
  await save.click();
  await expect(page.getByRole('region', { name: 'AI provider' }).getByRole('alert')).toContainText("The AI API isn't set up");
  await page.getByRole('combobox', { name: 'Provider', exact: true }).selectOption('Demo');
  expect(saves).toBe(0);
  expect((await (await request.get('/api/state')).json()).state.ai).toEqual(original);
  await saveLimits.click();
  await expect(page.getByRole('status').filter({ hasText: 'Check limits saved.' })).toBeVisible();
  expect((await (await request.get('/api/state')).json()).state.ai.intervalMinutes).toBe(15);
  expect((await (await request.get('/api/state')).json()).state.ai.maxRunsPerDay).toBe(96);
  // Saving the provider keeps the schedule just saved: each card saves only its own fields.
  await save.click();
  await expect(page.getByRole('status').filter({ hasText: 'AI settings saved.' })).toBeVisible();
  const after = (await (await request.get('/api/state')).json()).state.ai;
  expect([after.scheduled, after.intervalMinutes, after.maxRunsPerDay]).toEqual([true, 15, 96]);
});

test('the explicitly named live UI fixture cannot select Demo as its investigation provider', async ({ page, request }) => {
  const payload = await (await request.get('/api/state')).json();
  payload.connection.demo = false;
  payload.state.ai.provider = 'Api';
  await page.route('**/api/state', route => route.fulfill({ json: payload }));
  await page.goto('/');
  await nav(page, 'AI checks');
  await expect(page.getByRole('combobox', { name: 'Provider', exact: true }).selectOption('Demo', { timeout: 500 })).rejects.toThrow();
  await expect(page.getByRole('combobox', { name: 'Provider', exact: true })).toHaveValue('Api');
});

test('investigation history pages retain an older selection and retrieve evidence only on disclosure', async ({ page, request }) => {
  const payload = await (await request.get('/api/state')).json();
  const template = payload.state.investigations[0];
  payload.state.investigations = Array.from({ length: 25 }, (_, n) => ({ ...template, id: `owned-history-fixture-${n}`, title: `Owned history fixture ${n}`, headline: null, verdict: 'problem', status: 'Completed', repeatOf: null, at: new Date(Date.now() - (25 - n) * 3600000).toISOString(), toolEvidence: [] }));
  const retrieved: string[] = [];
  await page.route('**/api/state', route => route.fulfill({ json: payload }));
  await page.route('**/api/investigations/owned-history-fixture-*', async route => {
    const id = route.request().url().split('/').at(-1)!;
    retrieved.push(id);
    await route.fulfill({ json: payload.state.investigations.find((i: { id: string }) => i.id === id) });
  });
  await page.goto('/');
  await nav(page, 'Checks');
  const rows = page.locator('.run-row').filter({ hasText: /Owned history fixture/ });
  await expect(rows).toHaveCount(14);
  expect(retrieved).toEqual([]);
  await page.getByRole('button', { name: 'Show older checks', exact: true }).click();
  await expect(rows).toHaveCount(25);
  await page.locator('a.run-link[href$="/owned-history-fixture-4"]').click();
  await expect(page).toHaveURL(/#\/insights\/inv\/owned-history-fixture-4$/);
  const pane = page.getByRole('article', { name: 'Owned history fixture 4' });
  await pane.locator('.investigation-evidence > summary').first().click();
  await expect(pane.getByText(/No data was saved with this check/)).toBeVisible();
  expect(retrieved).toEqual(['owned-history-fixture-4']);
  await page.getByRole('button', { name: 'Close this check' }).click();
  await expect(page).toHaveURL(/#\/insights$/);
  await expect(rows).toHaveCount(25);
});

test('Monitor and pending reload states disable unsupported configuration actions with a reason', async ({ page, request }) => {
  const mode = await request.post('/api/mode', { data: { mode: 'Monitor' } });
  expect(mode.ok()).toBeTruthy();
  try {
    await page.goto('/');
    await nav(page, 'Settings');
    // One status line by the mode picker, with a way to unlock editing; the sheet repeats the reason where it matters.
    await expect(page.getByText(/never changes Predbat, and editing here is locked/)).toBeVisible();
    await expect(page.getByRole('button', { name: 'Unlock editing', exact: true })).toBeVisible();
    await page.getByRole('button', { name: /^House load scaling\b/ }).click();
    await expect(page.getByRole('dialog').getByText(/Editing is locked while Joule is in Watch only/)).toBeVisible();
    await expect(page.getByRole('dialog').getByRole('button', { name: 'Save change', exact: true })).toBeDisabled();
    await page.keyboard.press('Escape');
    await nav(page, 'Changes');
    for (const button of await page.getByRole('button', { name: /^Undo:/ }).all()) await expect(button).toBeDisabled();
    await nav(page, 'Files');
    for (const button of await page.getByRole('button', { name: 'Restore these files', exact: true }).all()) await expect(button).toBeDisabled();
    const payload = await (await request.get('/api/state')).json();
    payload.state.mode = 'Recommend';
    payload.state.pendingFileReload = true;
    await page.route('**/api/state', route => route.fulfill({ json: payload }));
    await page.reload();
    await nav(page, 'Changes');
    for (const button of await page.getByRole('button', { name: /^Undo:/ }).all()) await expect(button).toBeDisabled();
    await expect(page.getByRole('alert').filter({ hasText: 'Restart Predbat' })).toBeVisible();
  } finally {
    await request.post('/api/mode', { data: { mode: 'Recommend' } });
  }
});

for (const available of [false, true]) test(`owned alternative forecast fixture renders ${available ? 'matched comparisons' : 'missing evidence'}`, async ({ page }) => {
  await page.route('**/api/telemetry/plans/*/alternative', route => route.fulfill({ json: {
    available, reason: available ? 'Owned test fixture: frozen alternative forecast available.' : 'Owned test fixture: alternative source missing.',
    methodology: 'Owned browser fixture; no live model or shadow execution.', entityId: 'sensor.owned_test_forecast', source: 'Owned fixture', capturedAt: '2026-10-02T00:00:00Z',
    matchedSlots: available ? 2 : 0, predbatMaeKwhPerHalfHour: available ? .3 : null, alternativeMaeKwhPerHalfHour: available ? .1 : null,
    slots: available ? [{ time: '2026-10-02T00:00:00Z', durationMinutes: 30, predbatKwh: 1, alternativeKwh: .8, actualKwh: .7 }, { time: '2026-10-02T00:30:00Z', durationMinutes: 30, predbatKwh: 1, alternativeKwh: .8, actualKwh: .7 }] : []
  } }));
  await page.goto('/');
  await nav(page, 'Plan');
  const heading = page.getByRole('heading', { name: 'Second load forecast', exact: true });
  if (available) {
    const panel = page.locator('.panel').filter({ has: heading });
    await expect(panel).toContainText('frozen alternative forecast available');
    await expect(panel).toContainText('2 measured slots in both'); await expect(panel).toContainText('0.30 kWh'); await expect(panel).toContainText('0.10 kWh'); await expect(panel.locator('figure svg').first()).toBeVisible();
  } else {
    // Without a configured source the comparison is left out entirely (setting it up lives under Setup, not here).
    await expect(page.getByRole('heading', { name: 'What’s next' })).toBeVisible();
    await expect(page.getByText(/Second load forecast|see the README/)).toHaveCount(0);
    await expect(heading).toHaveCount(0);
    await expect(page.getByText('alternative source missing')).toHaveCount(0);
  }
});
