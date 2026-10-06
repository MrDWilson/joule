import { test, expect, type Page } from '@playwright/test';
import { openPage } from './support/navigation';
// Predbat writes plan states as short codes ("FrzExp", "HoldChrg"…), sometimes decorated with arrows or the forced marker ⅎ.
// Every place an action is shown must use the plain-language label, never the raw code.
const HALF_HOUR = 1800000;
const codes = ['Chrg', 'Chrgⅎ', 'FrzExp', 'FrzExp ↗', 'HoldChrg', 'Demand↘', 'NoChrg', 'Exp', 'HoldExp', 'FrzChrg', 'Chrg/Exp', 'Mystery'];

async function stubPlan(page: Page, list: string[] = codes) {
  const payload = await (await page.request.get('/api/state')).json();
  const start = Math.ceil(Date.now() / HALF_HOUR) * HALF_HOUR + HALF_HOUR;
  const slots = list.map((action, i) => ({ time: new Date(start + i * HALF_HOUR).toISOString(), durationMinutes: 30, action, loadForecast: .5, pvForecast: .2, socForecast: 50, importRate: 20 + i, exportRate: 15, cost: 0 }));
  // A repeated rate and adjacent codes that normalise to the same action merge into one window.
  slots[1].importRate = slots[0].importRate;
  slots[3].exportRate = slots[2].exportRate;
  payload.plan = { id: 'owned-actions', at: new Date().toISOString(), collectedAt: new Date().toISOString(), source: 'Predbat', slots };
  await page.route('**/api/state', route => route.fulfill({ json: payload }));
  await page.route('**/api/plans/timeline?*', route => route.fulfill({ json: { slots: [] } }));
}

test('plan windows name every Predbat state in plain English and group decorated codes', async ({ page }) => {
  await stubPlan(page);
  await page.goto('/');
  const windows = page.getByRole('region', { name: 'Coming up' });
  const labels = await windows.locator('.window-item .window-title strong').allTextContents();
  expect(labels).toEqual([
    'Charge from the grid',
    "Export solar, don't charge battery",
    'Hold at charge target',
    'Power your home',
    'Charge slot, no charging needed',
    'Export battery to the grid',
    'Export paused at minimum level',
    'Hold battery level',
    'Charge, then export',
    'Other Predbat state',
  ]);
  await expect(windows).not.toContainText(/FrzExp|HoldChrg|NoChrg|HoldExp|FrzChrg|Chrg|ⅎ/);
  // Export-type windows quote the export price, the others the import price.
  await expect(windows.locator('.window-item').filter({ hasText: "Export solar, don't charge battery" })).toContainText('15p export');
  // Each label explains itself on hover and on keyboard focus.
  const freeze = windows.getByRole('button', { name: "What does “Export solar, don't charge battery” mean?" });
  await freeze.hover();
  await expect(page.getByRole('tooltip')).toContainText('spare solar is exported');
  await page.mouse.move(0, 0);
  await expect(page.getByRole('tooltip')).toHaveCount(0);
  await freeze.focus();
  await expect(freeze).toHaveAttribute('aria-describedby', /.+/);
  await expect(page.getByRole('tooltip')).toContainText("battery won't charge");
  await page.keyboard.press('Escape');
  await expect(page.getByRole('tooltip')).toHaveCount(0);
});

test('the plan table uses short badges that explain themselves, never raw codes or title tooltips', async ({ page }) => {
  await stubPlan(page);
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/');
  await openPage(page, 'Plan');
  const table = page.getByRole('region', { name: "What's next, window by window" });
  await expect(table.locator('.plan-badge').filter({ hasText: /^Hold at target$/ })).toHaveCount(1);
  await expect(table.locator('.plan-badge').filter({ hasText: /^Solar export$/ })).toHaveCount(1);
  await expect(table.locator('[title]')).toHaveCount(0);
  const hint = table.getByRole('button', { name: 'What does “Charge” mean?' });
  await hint.focus();
  await expect(page.getByRole('tooltip')).toContainText('The battery charges from the grid');
  await expect(table.locator('table')).not.toContainText(/FrzExp|HoldChrg|NoChrg|HoldExp|FrzChrg/);
});

test('hints open on tap and close on a second tap', async ({ page }) => {
  await stubPlan(page);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/');
  const hint = page.getByRole('button', { name: 'What does “Charge from the grid” mean?' });
  await hint.click();
  const tip = page.getByRole('tooltip');
  await expect(tip).toBeVisible();
  const box = (await tip.boundingBox())!;
  expect(box.x).toBeGreaterThanOrEqual(0);
  expect(box.x + box.width).toBeLessThanOrEqual(390);
  await hint.click();
  await expect(tip).toHaveCount(0);
});

test('other Predbat spellings, entities, targets, statuses and canonical keys read as plain labels', async ({ page }) => {
  // Spellings seen in Predbat's plan, status entity and older stored plans, plus the server's canonical keys.
  await stubPlan(page, ['FrzChg', 'Chrg&nearr; 70%', 'Hold for car', 'Exp&searr;', 'freeze-export', 'HoldChg', 'Demand, Hold for car', 'Read-Only', 'charge-export']);
  await page.goto('/');
  const windows = page.getByRole('region', { name: 'Coming up' });
  const labels = await windows.locator('.window-item .window-title strong').allTextContents();
  expect(labels).toEqual([
    'Hold battery level',
    'Charge to 70%',
    'Hold battery for the car',
    'Export battery to the grid',
    "Export solar, don't charge battery",
    'Hold at charge target',
    'Hold battery for the car',
    'Read-only: no inverter control',
    'Charge, then export',
  ]);
  await expect(windows).not.toContainText(/Predbat state|FrzChg|HoldChg|&nearr;|&searr;|freeze-export/);
});
