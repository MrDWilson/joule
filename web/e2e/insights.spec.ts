import { test, expect, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { fetchFresh, fulfillRewritten } from './support/routes';
import { liveShaped } from './support/insights-fixture';

test.use({ timezoneId: 'Europe/London' });
// A poll can still be in flight through the rewriting route when a test ends; let it go quietly.
test.afterEach(async ({ page }) => page.unrouteAll({ behavior: 'ignoreErrors' }));

/** Serve every /api/state through `edit` (always from a fresh 200, so 304s never hide the rewrite). */
async function rewriteState(page: Page, edit: (payload: any) => any) { // eslint-disable-line @typescript-eslint/no-explicit-any
  await page.route('**/api/state', async route => {
    const response = await fetchFresh(route);
    await fulfillRewritten(route, response, edit(await response.json()));
  });
}
async function open(page: Page, hash = '#/insights') {
  await page.addInitScript(() => {
    sessionStorage.setItem('joule.setupAutoOpened', '1');
    localStorage.setItem('joule.firstRunSeen', '1');
  });
  await page.goto(`/${hash}`);
  await expect(page.locator('main#main')).toBeAttached({ timeout: 15000 });
}

test('a live-shaped Insights page fits a phone: what needs you first, quiet and unfinished checks folded', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await rewriteState(page, p => liveShaped(p));
  await open(page);
  const needs = page.getByRole('region', { name: /Needs you/ });
  await expect(needs).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Recent checks' })).toBeVisible();
  // The rows come before Ask Joule's suggestions in reading order only after Needs you.
  const order = await page.evaluate(() => ['Needs you', 'Ask Joule', 'Recent checks'].map(t =>
    [...document.querySelectorAll('h2')].findIndex(h => h.textContent?.startsWith(t))));
  expect(order[0]).toBeLessThan(order[1]);
  expect(order[1]).toBeLessThan(order[2]);
  const groups = page.locator('.run-group-toggle');
  expect(await groups.count()).toBeGreaterThan(2);
  await expect(groups.first()).toContainText(/\d+ quick checks? \d\d:\d\d–\d\d:\d\d/);
  await expect(page.getByText(/didn't finish/).first()).toBeVisible();
  const height = await page.evaluate(() => document.documentElement.scrollHeight);
  expect(height).toBeLessThan(4000);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  // No raw codes, markdown or the scheduled prompt on the page.
  const text = await page.locator('main').innerText();
  expect(text).not.toMatch(/FrzExp|`|\bPROBLEM\b|Scheduled review of the period/);
  // Starting a full check is never hidden off the edge of a phone: every Ask Joule control sits inside the card's padding.
  const ask = page.getByRole('region', { name: 'Ask Joule' });
  const full = ask.getByRole('button', { name: 'Run a full check' });
  await full.scrollIntoViewIfNeeded();
  await expect(full).toBeInViewport({ ratio: 1 });
  const card = (await ask.boundingBox())!;
  for (const button of await ask.getByRole('button').all()) {
    const box = (await button.boundingBox())!;
    expect(box.x).toBeGreaterThanOrEqual(card.x + 8);
    expect(box.x + box.width).toBeLessThanOrEqual(card.x + card.width - 8);
  }
});

test('a check that didn\'t finish is a grey row with plain words and Try again resumes it', async ({ page }) => {
  await rewriteState(page, p => {
    liveShaped(p);
    // Make the newest check the one that didn't finish, so it stands alone.
    const newest = p.state.investigations.at(-1);
    Object.assign(newest, { status: 'Failed', verdict: null, failureKind: 'provider_busy', title: "Check didn't finish", providerCode: 'server_error' });
    return p;
  });
  const resumed: string[] = [];
  await page.route('**/api/investigations/*/resume', route => {
    resumed.push(route.request().url());
    return route.fulfill({ status: 202, json: { ok: true } });
  });
  await open(page);
  const row = page.locator('.run-row.is-unfinished').first();
  await expect(row).toContainText("ChatGPT didn't answer");
  await expect(row).toContainText('Nothing in Predbat was changed.');
  await expect(row).not.toContainText(/fallback|discarded/);
  await expect(row.locator('.chip-warn')).toHaveCount(0);
  await row.getByRole('button', { name: /Try again/ }).click();
  await expect.poll(() => resumed.length).toBe(1);
  expect(resumed[0]).toMatch(/\/api\/investigations\/live-1\/resume$/);
  // Its own page explains it with the provider's code behind Technical details.
  await row.getByRole('link').click();
  const detail = page.getByRole('article', { name: "ChatGPT didn't answer" });
  await expect(detail.getByText('Technical details', { exact: true })).toBeVisible();
  await detail.getByText('Technical details', { exact: true }).click();
  await expect(detail).toContainText('server_error');
});

test('a check opens beside the list on a wide screen and on its own screen on a phone; Back returns', async ({ page }) => {
  await rewriteState(page, p => liveShaped(p));
  await page.setViewportSize({ width: 1440, height: 900 });
  await open(page);
  const problem = page.locator('.run-row.tone-warn .run-link').first();
  const title = (await problem.locator('.run-headline').innerText()).replace(/^Problem:\s*/, '');
  await problem.click();
  await expect(page).toHaveURL(/#\/insights\/inv\/live-\d+$/);
  const pane = page.getByRole('complementary', { name: 'The open check' });
  await expect(pane.getByRole('heading', { level: 2 })).toHaveText(title);
  await expect(page.getByRole('heading', { name: 'Recent checks' })).toBeVisible();
  await pane.getByRole('button', { name: 'Close this check' }).click();
  await expect(page).toHaveURL(/#\/insights$/);

  await page.setViewportSize({ width: 390, height: 844 });
  await page.locator('.run-row.tone-warn .run-link').first().click();
  await expect(page.getByRole('heading', { name: 'Recent checks' })).toHaveCount(0);
  await expect(page.getByRole('link', { name: 'All checks' })).toBeVisible();
  await page.goBack();
  await expect(page).toHaveURL(/#\/insights$/);
  await expect(page.getByRole('heading', { name: 'Recent checks' })).toBeVisible();
});

test('the running check shows friendly steps and elapsed time, and Stop cancels it', async ({ page }) => {
  let running = true;
  await rewriteState(page, p => {
    p.ai.running = running;
    p.state.investigations.push({
      ...p.state.investigations[0], id: 'running-1', at: new Date(Date.now() - 75_000).toISOString(), status: running ? 'Running' : 'Completed',
      verdict: running ? null : 'opportunity', title: running ? 'Checking' : 'Evening load is overestimated',
      request: { question: 'Why did the battery charge overnight?', from: null, to: null, scheduled: false },
      stepDetails: [
        { at: new Date().toISOString(), kind: 'plan_vs_actual', label: 'Compared the plan with your meters, 00:00–06:00', detail: 'plan_vs_actual: …' },
        { at: new Date().toISOString(), kind: 'mcp', label: "Read Predbat's log for “Warn”, 00:00–06:00", detail: 'mcp: {"name":"get_log"}' },
      ],
    });
    return p;
  });
  const cancels: string[] = [];
  await page.route('**/api/investigations/cancel', route => {
    cancels.push(route.request().method());
    running = false;
    return route.fulfill({ status: 202, json: { ok: true } });
  });
  await open(page);
  const card = page.getByRole('region', { name: 'Check in progress' });
  await expect(card).toContainText('“Why did the battery charge overnight?”');
  await expect(card).toContainText("Read Predbat's log for “Warn”");
  await expect(card).toContainText(/Running for 1 min \d+ s/);
  await expect(card.getByRole('listitem').filter({ hasText: "Reading Predbat's log" })).toHaveAttribute('aria-current', 'step');
  await expect(card).not.toContainText('mcp:');
  // Only one Stop: the header's duplicate is gone on Insights.
  await expect(page.getByRole('button', { name: /^Stop/ })).toHaveCount(1);
  await card.getByRole('button', { name: 'Stop' }).click();
  await expect.poll(() => cancels).toEqual(['POST']);
  await expect(page.getByRole('region', { name: 'Check finished' })).toContainText('Evening load is overestimated');
});

test('Ask Joule: the question clears, and the answer appears as a finished card that opens the check', async ({ page }) => {
  await open(page);
  await expect(page.getByRole('button', { name: 'Run check' })).toHaveCount(0);
  const box = page.getByRole('textbox', { name: 'Your question' });
  await box.fill('Why did the battery charge overnight?');
  await page.getByRole('button', { name: 'Ask', exact: true }).click();
  await expect(box).toHaveValue('');
  const done = page.getByRole('region', { name: 'Check finished' });
  await expect(done).toBeVisible({ timeout: 20000 });
  // The banner repeats what you asked, and the "checking" toast has been replaced.
  await expect(done).toContainText('Answer to “Why did the battery charge overnight?”');
  await expect(page.getByRole('status').filter({ hasText: 'Asked. Joule is checking.' })).toHaveCount(0);
  await done.getByRole('link', { name: /Read it/ }).click();
  await expect(page).toHaveURL(/#\/insights\/inv\//);
  // The question heads the list row and quotes above the answer.
  await expect(page.getByText('You asked: “Why did the battery charge overnight?”').first()).toBeVisible();
});

test('approving a suggestion applies it with a toast and starts a trial led by the change', async ({ page }) => {
  await open(page, '#/insights/suggestions');
  const card = page.locator('article.suggestion-card').first();
  await expect(card).toContainText('House load scaling');
  await expect(card).toContainText('108%');
  await expect(card).toContainText('Saving: not estimated');
  await expect(card).not.toContainText('/ month');
  // One header for every suggestion card: icon and kind, then who suggested it; the date on the right. The demo made the
  // suggestion shortly before it started, which is yesterday in the first minutes after midnight.
  await expect(card.locator('.suggestion-top')).toContainText(/Setting change · Suggested by the demo\s*(Today|Yesterday) \d\d:\d\d/);
  await card.getByRole('button', { name: 'Review' }).click();
  const sheet = page.getByRole('dialog', { name: 'Review this change' });
  await expect(sheet.getByRole('button', { name: 'Apply change' })).toBeEnabled();
  await sheet.getByRole('button', { name: 'Apply change' }).click();
  await expect(page.getByText('Change applied. Joule is tracking it as a trial.')).toBeVisible();
  await page.getByRole('navigation', { name: 'Insights sections' }).getByRole('link', { name: /Trials/ }).click();
  // The demo also has a solar trim running; this trial is the one for the change just applied.
  const trial = page.locator('article.trial-card').filter({ has: page.getByRole('heading', { level: 3, name: /^House load scaling/ }) });
  // The trial reads exactly as the suggestion did: the same friendly diff, not the server's raw 1.08 → 1.00.
  await expect(trial.getByRole('heading', { level: 3 })).toHaveText('House load scaling 108% → 100%');
  await expect(trial).toContainText(/Too early to tell: 0 of 7 days/);
  await expect(trial.getByRole('progressbar', { name: 'Trial progress' })).toBeVisible();
  await expect(trial).not.toContainText(/MAE|Confounders prevent|GBP\/day/);
  await trial.getByRole('button', { name: 'Keep it' }).click();
  await expect(page.getByText('Kept. Trial closed.')).toBeVisible();
  // The solar trial still running may mention this change in its comparison notes; only its own card has it as the heading.
  await expect(page.locator('article.trial-card').filter({ has: page.getByRole('heading', { level: 3, name: /^House load scaling/ }) })).toHaveCount(0);
  await page.getByText('Closed trials').click();
  const closed = page.locator('article.trial-row').filter({ hasText: 'House load scaling' }).first();
  await expect(closed).toContainText(/You kept it Today \d\d:\d\d/);
  await expect(closed.getByRole('button')).toHaveCount(0);
  await expect(closed).not.toContainText(/Check-in|revert/i);
});

test('a trial Joule closed says so, and never claims you closed it', async ({ page }) => {
  await rewriteState(page, p => {
    const at = new Date(Date.now() - 3600_000).toISOString();
    p.state.experiments = [{
      id: 'migrated-trial', title: 'Changed in Predbat: Predbat mode', status: 'Closed', source: 'Predbat', revisionId: -1,
      startedAt: new Date(Date.now() - 86400_000).toISOString(), reviewAt: at, hypothesis: '',
      result: "Not a tunable change: Predbat mode is one of Predbat's own controls, now shown as an event in History.",
      financialMethod: '', baselineError: null, currentError: null, baselineCostGbpPerDay: null, currentCostGbpPerDay: null,
      baselineCostCoverage: 0, currentCostCoverage: 0, confounders: [], revertEligible: false, automaticRevertEligible: false,
      revertReason: '', decisions: [{ at, decision: 'close', notes: 'Not a tunable change', extendedDays: null }],
    }];
    return p;
  });
  await open(page, '#/insights/experiments');
  await page.getByText('Closed trials').click();
  const row = page.locator('article.trial-row').first();
  await expect(row).toContainText('Changed in Predbat · Yesterday · Closed by Joule: not a setting you tune');
  await expect(row).not.toContainText(/you closed/i);
});

test('with live writes off a suggestion is made in Predbat by hand and marked done', async ({ page }) => {
  await rewriteState(page, p => {
    p.connection = { ...p.connection, demo: false, writesEnabled: false, predbatConfigured: true };
    p.state.proposals = [{ ...p.state.proposals[0], id: 'writes-off-fixture', status: 'Pending', staleKeys: [], thread: [] }];
    return p;
  });
  const done: string[] = [];
  await page.route('**/api/proposals/*/done', route => {
    done.push(route.request().method());
    return route.fulfill({ json: { ok: true } });
  });
  await page.route('**/api/setup', route => route.fulfill({ json: { demo: false, progress: { done: 6, total: 6, requiredDone: true, steps: [] } } }));
  await open(page, '#/insights/suggestions');
  await expect(page.getByText('This install is read-only')).toBeVisible();
  await page.locator('article.suggestion-card').first().getByRole('button', { name: 'Review' }).click();
  const sheet = page.getByRole('dialog', { name: 'Review this change' });
  await expect(sheet.getByRole('region', { name: 'Change this in Predbat yourself' })).toContainText('Set House load scaling');
  await expect(sheet.getByRole('button', { name: /Copy the new value for House load scaling/ })).toBeVisible();
  await expect(sheet.getByRole('button', { name: 'Apply change' })).toHaveCount(0);
  await sheet.getByRole('button', { name: 'Mark as done' }).click();
  await expect.poll(() => done).toEqual(['POST']);
});

test('a suggestion is only stale when one of its own settings moved', async ({ page }) => {
  let stale: string[] = [];
  await rewriteState(page, p => {
    p.state.proposals = [{ ...p.state.proposals[0], id: 'stale-fixture', status: 'Pending', baseRevision: 0, staleKeys: stale, thread: [] }];
    return p;
  });
  await open(page, '#/insights/suggestions');
  const card = page.locator('article.suggestion-card').first();
  await expect(card).not.toContainText('changed since');
  await card.getByRole('button', { name: 'Review' }).click();
  await expect(page.getByRole('button', { name: 'Apply change' })).toBeEnabled();
  await page.getByRole('button', { name: 'Not now' }).click();
  stale = ['load_scaling'];
  await page.reload();
  await expect(page.locator('article.suggestion-card').first().locator('.chip-warn')).toContainText('changed since');
  await page.locator('article.suggestion-card').first().getByRole('button', { name: 'Review' }).click();
  await expect(page.getByRole('button', { name: 'Apply change' })).toBeDisabled();
  await expect(page.getByRole('dialog')).toContainText('House load scaling changed since this was suggested');
});

test('closed items list why they closed and can be reopened', async ({ page }) => {
  await rewriteState(page, p => {
    p.state.proposals = p.state.proposals.map((x: any) => ({ // eslint-disable-line @typescript-eslint/no-explicit-any
      ...x, status: 'Denied', decidedAt: new Date().toISOString(), decisionNote: 'We cook late',
      thread: [{ at: new Date().toISOString(), role: 'ai', text: 'Agreed: evening use is higher.', verdict: 'accept' }],
    }));
    return p;
  });
  const reopened: string[] = [];
  await page.route('**/api/proposals/*/reopen', route => {
    reopened.push(route.request().url());
    return route.fulfill({ json: { ok: true } });
  });
  await open(page, '#/insights/suggestions?view=closed');
  const row = page.locator('.closed-row').filter({ hasText: 'Bring the evening load forecast closer to reality' });
  await expect(row).toContainText('Closed after your reply');
  await expect(row).toContainText('Your note: “We cook late”');
  await expect(row).toContainText('Joule: Agreed: evening use is higher.');
  await row.getByRole('button', { name: 'Reopen' }).click();
  await expect.poll(() => reopened.length).toBe(1);
});

test('AI checks leads with the last day, hides dollars for a ChatGPT plan and blocks saving without a model', async ({ page }) => {
  await rewriteState(page, p => liveShaped(p));
  await open(page, '#/setup/ai');
  const summary = page.getByRole('region', { name: 'Today' });
  await expect(summary).toContainText('AI checks');
  await expect(summary).toContainText('Included in your ChatGPT plan');
  await expect(page.getByText(/US\$|USD|Unavailable/)).toHaveCount(0);
  await expect(page.getByRole('figure', { name: 'Tokens per day' })).toBeVisible();
  await expect(page.getByRole('region', { name: 'Last 10 AI checks' }).locator('.usage-row')).not.toHaveCount(0);
  await expect(page.getByRole('region', { name: 'When Joule checks' })).toContainText('About 5 minutes after each cheap or export window ends');
  const provider = page.getByRole('region', { name: 'AI provider' });
  await provider.getByRole('textbox', { name: 'Model' }).fill('');
  await provider.getByRole('button', { name: 'Save' }).click();
  await expect(provider.getByRole('alert')).toHaveText('Choose a model.');
  await expect(provider.getByRole('button', { name: 'Test connection' })).toBeDisabled();
  await provider.getByRole('combobox', { name: 'Provider' }).selectOption('Api');
  await expect(provider.getByRole('spinbutton', { name: /Input price/ })).toBeVisible();
});

test('the AI connection test reports what the provider said', async ({ page }) => {
  await page.route('**/api/ai/test', route => route.fulfill({ json: { ok: true, message: 'ChatGPT answered with gpt-5.6-sol in 1.2 s.', seconds: 1.2, inputTokens: 10, outputTokens: 2 } }));
  await rewriteState(page, p => liveShaped(p));
  await open(page, '#/setup/ai');
  const provider = page.getByRole('region', { name: 'AI provider' });
  await provider.getByRole('button', { name: 'Test connection' }).click();
  await expect(provider.getByRole('status')).toContainText('answered with gpt-5.6-sol');
});

for (const width of [1440, 390]) {
  test(`a check's own page and the closed list have no serious accessibility problems at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: width < 700 ? 844 : 900 });
    await rewriteState(page, p => liveShaped(p));
    await open(page);
    await page.locator('.run-row.tone-warn .run-link').first().click();
    await expect(page).toHaveURL(/#\/insights\/inv\//);
    const problems: string[] = [];
    for (const step of ['detail', 'closed']) {
      if (step === 'closed') await page.goto('/#/insights/suggestions?view=closed');
      await page.waitForTimeout(500);
      const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze();
      for (const v of results.violations.filter(v => v.impact === 'serious' || v.impact === 'critical'))
        problems.push(`${step} · ${v.id}: ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join(' | ')}`);
    }
    expect(problems).toEqual([]);
  });
}
