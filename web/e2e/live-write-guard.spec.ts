import { test, expect, type Page } from '@playwright/test';
import { openPage } from './support/navigation';

const nav = openPage;

test('live deployment with writes disabled prevents runtime controls while allowing review', async ({ page, request }) => {
  const payload = await (await request.get('/api/state')).json();
  payload.connection.demo = false;
  payload.connection.predbatConfigured = true;
  payload.connection.writesEnabled = false;
  payload.state.mode = 'Recommend';
  payload.state.pendingFileReload = false;
  payload.state.writeUncertain = false;
  const setting = payload.state.settings.find((s: { key: string }) => s.key === 'load_scaling');
  setting.editable = true;
  const proposal = { ...payload.state.proposals[0], id: 'owned-disabled-proposal', title: 'Owned disabled live proposal', status: 'Pending', baseRevision: payload.state.revision };
  payload.state.proposals = [proposal];
  payload.state.revisions = [
    { id: payload.state.revision - 1, at: new Date().toISOString(), source: 'Owned fixture', reason: 'Owned prior revision', changes: [{ key: setting.key, before: '1.07', after: setting.value }], values: { [setting.key]: '1.07' } },
    { id: payload.state.revision, at: new Date().toISOString(), source: 'Owned fixture', reason: 'Owned current revision', changes: [], values: { [setting.key]: setting.value } },
  ];
  payload.state.experiments = [{ id: 'owned-disabled-trial', title: 'Owned disabled runtime trial', status: 'Running', source: 'Owned fixture', revisionId: -1, startedAt: new Date().toISOString(), reviewAt: new Date().toISOString(), hypothesis: 'Owned fixture', result: 'Awaiting observations', financialMethod: 'Observed covered periods only', baselineError: null, currentError: null, baselineCostGbpPerDay: null, currentCostGbpPerDay: null, baselineCostCoverage: 0, currentCostCoverage: 0, confounders: [], decisions: [], revertEligible: true, automaticRevertEligible: true, revertReason: 'Otherwise eligible' }];
  await page.route('**/api/state', route => route.fulfill({ json: payload }));
  const runtimeRequests: string[] = [];
  page.on('request', r => {
    if (r.method() === 'POST' && /\/api\/(settings\/|proposals\/[^/]+\/approve|revisions\/[^/]+\/(restore|revert))/.test(r.url())) runtimeRequests.push(r.url());
  });
  await page.goto('/');
  await nav(page, 'Settings');
  await expect(page.getByText(/This install is read-only/).first()).toBeVisible();
  // The value stays readable; the sheet explains once why it can't be saved, and the AI's limits can still be set.
  await page.getByRole('button', { name: new RegExp(`^${setting.name}\\b`) }).click();
  const sheet = page.getByRole('dialog', { name: setting.name });
  await expect(sheet.getByText(/Saving needs live writes turned on/)).toBeVisible();
  await expect(sheet.getByRole('button', { name: 'Save change', exact: true })).toBeDisabled();
  await expect(sheet.getByRole('switch', { name: `Allow automatic changes to ${setting.name}`, exact: true })).toBeEnabled();
  await page.keyboard.press('Escape');
  await nav(page, 'Changes');
  const undo = page.getByRole('button', { name: /^Undo:/ });
  await expect(undo).toHaveCount(1);
  await expect(undo).toBeDisabled();
  await expect(page.getByText(/Saving needs live writes turned on, so Undo is off/)).toBeVisible();
  await page.getByRole('button', { name: /^More actions for/ }).last().click();
  await expect(page.getByRole('button', { name: 'Restore settings to this point' })).toBeDisabled();
  await page.keyboard.press('Escape');
  await nav(page, 'Trials');
  const trial = page.getByRole('article', { name: 'Owned disabled runtime trial' });
  await expect(trial.getByRole('button', { name: 'Undo', exact: true })).toBeDisabled();
  await expect(trial.getByText('Live writes are off: undo it in Predbat yourself', { exact: true })).toBeVisible();
  await expect(trial.getByRole('button', { name: 'Keep it', exact: true })).toBeEnabled();
  await trial.getByRole('button', { name: 'More actions for Owned disabled runtime trial' }).click();
  await expect(page.getByRole('button', { name: 'Add a note', exact: true })).toBeEnabled();
  await page.keyboard.press('Escape');
  await nav(page, 'Suggestions');
  await page.locator('article.suggestion-card').getByRole('button', { name: 'Review', exact: true }).click();
  // Read-only: no Apply; the user makes the change in Predbat and marks it done.
  await expect(page.getByRole('button', { name: 'Apply change', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Mark as done', exact: true })).toBeEnabled();
  await expect(page.getByRole('button', { name: 'Decline…', exact: true })).toBeEnabled();
  expect(runtimeRequests).toEqual([]);
});
