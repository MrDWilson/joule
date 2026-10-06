import { test, expect, type Page, type APIRequestContext } from '@playwright/test';
import { openPage } from './support/navigation';

async function manualFollowUpFixture(page: Page, request: APIRequestContext, repeated = false) {
  const payload = await (await request.get('/api/state')).json();
  const step = {
    id: 'tesla-step',
    title: 'Review conflicting Tesla service hooks',
    rationale: 'The discharge-stop call selects self_consumption after charge-start selected backup.',
    suggestedAction: 'Review the discharge-stop hook so it cannot undo an active charging command.',
    verification: 'During the next authorized window, check the final mode, measured charging power and SOC rise.',
    uncertainty: 'The separate 1.67 versus 5.0 kW limit remains unverified. <img src=x onerror=alert(1)>',
    evidenceReferences: ['control-log', 'service-config'],
  };
  const now = Date.now();
  const investigation = {
    ...payload.state.investigations[0],
    id: 'tesla-follow-up', at: new Date(now).toISOString(), title: 'Overnight charging control diagnosis', headline: null, verdict: 'problem',
    summary: 'A service-hook conflict may interrupt planned charging.', evidence: ['Charge-start selected backup before discharge-stop selected self_consumption.'],
    nextSteps: [step], fileChanges: [], provider: 'Test fixture', status: 'Completed', evidenceReferences: ['control-log'],
    toolEvidence: [
      { id: 'control-log', kind: 'mcp', request: '{"name":"get_log","arguments":{"search":"charge"}}', label: "Read Predbat's log for “charge”", retrievedAt: new Date(now).toISOString(), success: true, resultJson: '{"events":["charge_start: backup","discharge_stop: self_consumption"]}', sourceReferences: [], error: null },
      { id: 'service-config', kind: 'mcp', request: '{"name":"get_config"}', retrievedAt: new Date(now).toISOString(), success: true, resultJson: '{"discharge_stop_service":"self_consumption"}', sourceReferences: [], error: null },
    ],
  };
  payload.state.mode = 'Monitor'; payload.state.proposals = []; payload.ai.running = false;
  payload.state.investigations = repeated
    ? Array.from({ length: 12 }, (_, index) => ({ ...investigation, id: index === 11 ? investigation.id : `older-${index}`, at: new Date(now - (11 - index) * 3600000).toISOString(), nextSteps: [{ ...step, id: `s-${index}` }] }))
    : [investigation];
  await page.route('**/api/state', route => route.fulfill({ json: payload }));
  await page.route('**/api/investigations/tesla-follow-up', route => route.fulfill({ json: investigation }));
  const mutations: string[] = [];
  await page.route('**/api/**', async route => {
    if (!['GET', 'HEAD'].includes(route.request().method())) {
      mutations.push(route.request().url());
      return route.fulfill({ status: 204 });
    }
    return route.fallback();
  });
  await page.goto('/');
  return { investigation, mutations };
}

test('a to-do stays actionable with no setting changes and opens its evidence in words', async ({ page, request }) => {
  const { mutations } = await manualFollowUpFixture(page, request);
  await openPage(page, 'Suggestions');
  const card = page.getByRole('article', { name: 'Review conflicting Tesla service hooks' });
  await expect(card).toBeVisible();
  await expect(card).toContainText('To-do');
  await expect(card).toContainText('Review the discharge-stop hook');
  await expect(card.getByRole('heading', { name: 'How to check', exact: true })).toBeHidden();
  await card.getByText('Why, and how to check it', { exact: true }).click();
  await expect(card).toContainText('During the next authorized window');
  await expect(card).toContainText('1.67 versus 5.0 kW limit remains unverified');
  await expect(card.getByRole('button', { name: /Approve|Apply|Review/i })).toHaveCount(0);
  await expect(card.locator('img,script')).toHaveCount(0);
  await card.getByText('Show the evidence for this', { exact: true }).click();
  await expect(card.getByText("Read Predbat's log for “charge”")).toBeVisible();
  await expect(card.getByText("Read Predbat's config")).toBeVisible();
  await expect(card.locator('.tool-evidence > summary')).not.toContainText(['"name"']);
  await expect(card.locator('.tool-evidence > summary').first()).not.toContainText('"name"');
  await card.getByText("Read Predbat's log for “charge”").click();
  await expect(card).toContainText('charge_start: backup');
  await card.getByRole('button', { name: 'Open the check', exact: true }).click();
  await expect(page).toHaveURL(/#\/insights\/inv\/tesla-follow-up$/);
  await expect(page.getByRole('heading', { name: 'Overnight charging control diagnosis', exact: true })).toBeVisible();
  expect(mutations).toEqual([]);
});

test('repeated advice from later checks shows once, from the newest check', async ({ page, request }) => {
  await manualFollowUpFixture(page, request, true);
  await openPage(page, 'Suggestions');
  await expect(page.getByRole('article', { name: 'Review conflicting Tesla service hooks' })).toHaveCount(1);
  await openPage(page, 'Insights');
  await expect(page.getByRole('region', { name: /Needs you/ }).getByRole('listitem')).toHaveCount(1);
});

test('a to-do is marked done in one tap, and its wording stays readable on mobile', async ({ page, request }) => {
  const { mutations } = await manualFollowUpFixture(page, request);
  await page.setViewportSize({ width: 390, height: 844 });
  await openPage(page, 'Suggestions');
  const card = page.getByRole('article', { name: 'Review conflicting Tesla service hooks' });
  await card.getByText('Why, and how to check it', { exact: true }).click();
  await expect(card).toContainText('The separate 1.67 versus 5.0 kW limit remains unverified.');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBeTruthy();
  await card.getByRole('button', { name: 'Done' }).click();
  await expect.poll(() => mutations.some(u => /followups\/tesla-step\/dismiss$/.test(u))).toBe(true);
});

test('a running check can be stopped without submitting another run', async ({ page, request }) => {
  const payload = await (await request.get('/api/state')).json();
  payload.ai.running = true;
  await page.route('**/api/state', route => route.fulfill({ json: payload }));
  const actions: string[] = [];
  await page.route('**/api/investigations/*', route => {
    actions.push(route.request().url());
    payload.ai.running = false;
    return route.fulfill({ status: 200, json: { cancellationRequested: true } });
  });
  await page.goto('/');
  await openPage(page, 'Checks');
  await page.getByRole('region', { name: 'Check in progress' }).getByRole('button', { name: 'Stop', exact: true }).click();
  await expect(page.getByText('Stopping the check.', { exact: true })).toBeVisible();
  await expect(page.getByRole('region', { name: 'Check in progress' })).toHaveCount(0);
  expect(actions).toHaveLength(1);
  expect(actions[0]).toMatch(/\/api\/investigations\/cancel$/);
});
