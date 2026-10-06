import { test, expect, type Page, type APIRequestContext } from '@playwright/test';

/** One fixture check, opened on its own page (#/insights/inv/<id>). */
async function investigationFixture(page: Page, request: APIRequestContext, summary: string, evidence: string[] = []) {
  const payload = await (await request.get('/api/state')).json();
  const investigation = {
    ...payload.state.investigations[0],
    id: 'presentation-fixture', title: 'Readable investigation fixture', headline: null, verdict: 'problem',
    summary, evidence, toolEvidence: [], evidenceReferences: [], nextSteps: [], fileChanges: [],
    provider: 'Test fixture', status: 'Completed',
  };
  payload.state.investigations = [investigation];
  await page.route('**/api/state', route => route.fulfill({ json: payload }));
  await page.route('**/api/investigations/presentation-fixture', route => route.fulfill({ json: investigation }));
  await page.goto('/#/insights/inv/presentation-fixture');
  return page.getByRole('article', { name: investigation.title });
}

test('a long legacy summary opens as a short preview and expands into readable paragraphs without losing text', async ({ page, request }) => {
  // Summaries up to 1,200 characters show in full; longer legacy answers keep the disclosure. Removing it or dropping the remaining sentences would break this reader-facing contract.
  const summary = [
    'The battery imported 3.25 kWh during the overnight period while the house drew a steady baseline load from the grid-charged store.',
    'The observed load used most of that energy before sunrise, leaving the battery close to its reserve by the start of the morning peak.',
    'The import rate was 7.5 p/kWh during the configured window and rose sharply once the cheap period closed at the scheduled time.',
    'Later demand increased while generation fell, leaving a higher evening import requirement than the plan had anticipated for the day.',
    'This comparison uses observed portions of the period and does not establish savings against any alternative charging schedule.',
    'The tariff sample is incomplete and cannot support a whole-day cost estimate without further measured half-hour readings.',
    'Solar generation readings were missing for two intervals, so the midday self-consumption figure is a lower bound rather than a total.',
    'Battery state of charge followed the forecast closely overnight and only diverged after the first export window opened.',
    'Export credit was small because most surplus energy went into the battery rather than back to the grid during the afternoon.',
    'Continue collecting measured coverage before deciding whether the charge window needs to change for the coming week.',
    'A second review after seven full days of data would give a clearer picture of whether this pattern repeats.',
    'Final retained sentence confirms that the full answer remains available.',
  ].join(' ');
  const card = await investigationFixture(page, request, summary, ['Observed import: 3.25 kWh. This is a source observation, not a verified explanation.']);
  const expand = card.getByRole('button', { name: 'Read full summary' });
  await expect(expand).toHaveAttribute('aria-expanded', 'false');
  await expect(card).not.toContainText('Final retained sentence');
  const observations = card.locator('.investigation-observation-list');
  await expect(observations).toBeHidden();
  await card.locator('.investigation-observations > summary').click();
  await expect(observations).toBeVisible();
  await expect(observations).toContainText('Observed import: 3.25 kWh.');
  await expand.click();
  const text = card.locator('.investigation-summary .investigation-text');
  await expect(text.locator('p')).toHaveCount(6);
  await expect(text).toHaveText(summary, { useInnerText: true });
  await expect(text).toContainText('7.5 p/kWh');
  const collapse = card.getByRole('button', { name: 'Show less' });
  await expect(collapse).toHaveAttribute('aria-expanded', 'true');
  await collapse.click();
  await expect(card).not.toContainText('Final retained sentence');
  await card.locator('.investigation-evidence > summary').click();
  await expect(card).toContainText('No data was saved with this check');
});

test('explicit paragraphs and lists stay readable while model markup is rendered as safe text', async ({ page, request }) => {
  // A one-block renderer loses structure; raw HTML rendering turns untrusted text into DOM.
  const card = await investigationFixture(page, request,
    'First measured observation.\n\n## Coverage limits\n- Import coverage is partial.\n- Solar readings are missing.\n\n<img src=x onerror=alert(1)> remains quoted text.',
    ['Evidence <script>alert(1)</script> remains literal.']);
  const summary = card.locator('.investigation-summary');
  await expect(summary.getByRole('heading', { name: 'Coverage limits', exact: true })).toBeVisible();
  await expect(summary.getByRole('listitem')).toHaveCount(2);
  await expect(summary).toContainText('First measured observation.');
  await expect(summary).toContainText('<img src=x onerror=alert(1)>');
  await card.locator('.investigation-observations > summary').click();
  await expect(card).toContainText('Evidence <script>alert(1)</script> remains literal.');
  await expect(card.locator('img, script')).toHaveCount(0);
  await expect(summary.getByRole('button', { name: 'Read full summary' })).toHaveCount(0);
});

test('Predbat codes, markdown and entity ids read in plain words, with the original kept in a tooltip', async ({ page, request }) => {
  const card = await investigationFixture(page, request,
    'During the next FrzExp slot the SoC held at 66%. **Approve** the pending `combine_charge_slots=on` change and check `sensor.my_home_solar_generated` (FrzChg earlier).');
  const summary = card.locator('.investigation-summary');
  await expect(summary).toContainText("During the next Export solar, don't charge battery slot the battery level held at 66%.");
  await expect(summary.locator('abbr.plan-term').first()).toHaveAttribute('title', /^Predbat: FrzExp\. /);
  await expect(summary.locator('strong')).toHaveText('Approve');
  await expect(summary.locator('code.inline-code').first()).toHaveText('combine_charge_slots=on');
  await expect(summary).toContainText('Hold battery level earlier');
  await expect(summary).not.toContainText(/FrzExp|FrzChg|`|\*\*/);
});

test('a punctuation-free answer stays bounded and can be fully recovered on mobile', async ({ page, request }) => {
  // Splitting only on sentence punctuation leaves legacy notes as an unbounded initial block.
  const summary = `Opening note ${'measured coverage '.repeat(90)}final retained word`;
  await page.setViewportSize({ width: 390, height: 844 });
  const card = await investigationFixture(page, request, summary);
  await expect(card).not.toContainText('final retained word');
  await card.getByRole('button', { name: 'Read full summary' }).click();
  await expect(card.locator('.investigation-summary .investigation-text')).toHaveText(summary, { useInnerText: true });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBeTruthy();
});
