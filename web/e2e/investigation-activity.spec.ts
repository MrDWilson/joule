import { test, expect } from '@playwright/test';

test('check progress follows polling in plain words, stays bounded and gives way to the result', async ({ page, request }) => {
  const payload = await (await request.get('/api/state')).json();
  const at = new Date(Date.now() - 20_000).toISOString();
  const running = {
    ...payload.state.investigations[0], id: 'running-check', at, status: 'Running', verdict: null, title: 'Checking',
    request: { question: null, from: null, to: null, scheduled: true, trigger: 'the cheap window that ended at 05:30' },
    steps: [], stepDetails: [],
  };
  payload.ai.running = true;
  payload.state.investigations = [...payload.state.investigations, running];
  await page.route('**/api/state', route => route.fulfill({ json: payload }));
  await page.clock.install();
  await page.goto('/#/insights');
  const card = page.getByRole('region', { name: 'Check in progress' });
  await expect(card).toContainText('An automatic check, for the cheap window that ended at 05:30');
  await expect(card.getByRole('listitem').filter({ hasText: 'Reading your plan and meters' })).toHaveAttribute('aria-current', 'step');
  await expect(page.getByRole('button', { name: 'Run a full check' })).toBeDisabled();

  async function poll() {
    const response = page.waitForResponse(r => r.url().endsWith('/api/state') && r.ok());
    await page.clock.fastForward(3000);
    await response;
  }
  // Older servers send only raw steps: they are translated, never shown as JSON.
  running.steps = [
    'plan_vs_actual: 2026-10-05T04:00:00.0000000+01:00/2026-10-05T06:00:00.0000000+01:00 (retrieved; evidence tool-1)',
    'schema: MCP input schema: get_log (retrieved; evidence tool-2)',
    'mcp: {"name":"get_log","arguments":{"filter":"all","search":"Warn","start":"2026-10-05 04:00:00","end":"2026-10-05 06:00:00"}} (retrieved; evidence tool-3)',
  ];
  await poll();
  const steps = card.getByRole('list', { name: 'Latest steps' });
  await expect(steps).toContainText('Compared the plan with your meters');
  await expect(steps).toContainText("Read Predbat's log for “Warn”, 04:00–06:00");
  await expect(card).not.toContainText(/schema|"name"|tool-\d/);

  running.stepDetails = Array.from({ length: 9 }, (_, n) => ({ at, kind: 'mcp', label: `Read Predbat's log, batch ${n} <img src=x onerror=alert(1)>`, detail: 'raw' }));
  await poll();
  await expect(steps.getByRole('listitem')).toHaveCount(4);
  await expect(steps).not.toContainText('batch 4');
  await expect(steps).toContainText('batch 8');
  await expect(card.locator('img')).toHaveCount(0);

  payload.ai.running = false;
  Object.assign(running, { status: 'Completed', verdict: 'no_change', title: 'No material change since the last review' });
  await poll();
  await expect(card).toHaveCount(0);
  await expect(page.getByRole('region', { name: 'Check finished' })).toContainText('Checked, nothing new');
  await expect(page.getByRole('button', { name: 'Run a full check' })).toBeEnabled();
});
