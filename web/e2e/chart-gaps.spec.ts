import { test, expect, type Page } from '@playwright/test';
import { openPage } from './support/navigation';
import { fetchFresh, fulfillRewritten } from './support/routes';
import { londonDate } from './support/clock';

// A route handler still reading a fetched response when the test ends would fail it as the context closes.
test.afterEach(async ({ page }) => page.unrouteAll({ behavior: 'ignoreErrors' }));

// The energy timeline: one calm chart for what happened and what Predbat plans, the same encoding everywhere.
test.beforeEach(async ({ request }) => {
  const response = await request.post('/api/telemetry/collect', { headers: { 'X-Joule-Request': '1' }, data: {} });
  expect(response.ok(), await response.text()).toBeTruthy();
});

const today = (page: Page) => page.locator('figure.tl-today');
const plan = (page: Page) => page.locator('figure.tl-plan');

test('on a phone the price range is in its chip, the ranges are short, and reading a slot never moves the chart', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/#/plan');
  const chart = plan(page);
  await expect(chart.locator('svg.tl-svg')).toBeVisible();
  // No price key beside the Now pill: the chip carries the range.
  await expect(chart.locator('.tl-price-key')).toHaveCount(0);
  await expect(chart.locator('[data-chip="price"]')).toHaveText(/^Price [\d.]+p–[\d.]+p$/);
  // Short labels, full names for assistive tech.
  const ranges = chart.getByRole('group', { name: 'Time range' });
  await expect(ranges.getByRole('button', { name: 'Past 24 h' })).toHaveText('−24 h');
  // The plan's colours are named under the ribbon.
  await expect(chart.locator('.tl-action-key text').first()).toBeVisible();
  const plot = chart.locator('.tl-plot');
  const before = (await plot.boundingBox())!.y;
  const surface = (await chart.locator('.tl-surface').boundingBox())!;
  await page.mouse.click(surface.x + surface.width * 0.3, surface.y + surface.height / 2);
  await expect(chart.locator('.tl-readout strong')).toHaveText(/^\d\d:\d\d–\d\d:\d\d · /);
  expect((await plot.boundingBox())!.y).toBe(before);
  // The table groups rows under their day and never says 24:00.
  await chart.getByText('Show as table').click();
  await expect(chart.locator('.chart-table .tl-day-row').first()).toHaveText(/^[A-Z][a-z]{2} \d{1,2} [A-Z][a-z]{2}$/);
  await expect(chart.locator('.chart-table')).not.toContainText('24:00');
});

test('compare is labelled, and an all-future range turns it and forecast accuracy off', async ({ page }) => {
  await page.goto('/#/plan');
  const chart = plan(page);
  await expect(chart.getByText('Compare home use with', { exact: true })).toBeVisible();
  await chart.getByRole('group', { name: 'Time range' }).getByRole('button', { name: 'Whole plan' }).click();
  await expect(chart.getByRole('switch', { name: 'Show forecast accuracy' })).toBeEnabled();
  const next = chart.getByRole('group', { name: 'Time range' }).getByRole('button', { name: /^Next (12|48) h$/ });
  const usable = next.and(page.locator(':not([disabled])'));
  if (await usable.count()) {
    await usable.first().click();
    await expect(chart.getByRole('switch', { name: 'Show forecast accuracy' })).toBeDisabled();
    await expect(chart.getByRole('group', { name: 'Compare home use with' }).getByRole('button', { name: 'Yesterday' })).toBeDisabled();
  } else {
    // A plan too short for either: both are offered disabled, saying where the plan ends.
    await expect(next.first()).toHaveAttribute('title', /^Plan runs to [A-Z][a-z]{2} \d\d:\d\d$/);
  }
});
const series = (figure: ReturnType<typeof today>) =>
  figure.evaluate((el) => [...new Set([...el.querySelectorAll('path.tl-series[data-series]')].map((p) => p.getAttribute('data-series')))]);

/**
 * Twelve hours of history ending at the current plan's start: one missing slot (bridged), four missing slots of home use
 * (washed and named), and solar unknown overnight where Predbat forecast none (counted as zero, no wash).
 */
async function mockHistory(page: Page) {
  // The plan starts at the half-hour in progress, and the history is the twelve hours before it.
  const end = Math.floor(Date.now() / 1800000) * 1800000;
  await page.route('**/api/state*', async (route) => {
    const response = await fetchFresh(route);
    const body = await response.json();
    const shift = end - Date.parse(body.plan.slots[0].time);
    body.plan.slots = body.plan.slots.map((s: { time: string }) => ({ ...s, time: new Date(Date.parse(s.time) + shift).toISOString(), loadActual: null, pvActual: null, socActual: null }));
    await fulfillRewritten(route, response, body);
  });
  const missingLoad = new Set([5, 12, 13, 14, 15]);
  const slots = Array.from({ length: 24 }, (_, i) => {
    const time = end - (24 - i) * 1800000;
    const night = i < 10;
    return {
      time: new Date(time).toISOString(), durationMinutes: 30, loadForecast: .4, pvForecast: night ? 0 : .5, socForecast: 50,
      socActual: 50 + i, loadActual: missingLoad.has(i) ? null : .3 + i / 100, pvActual: night ? null : .4,
      loadActualMethod: missingLoad.has(i) ? null : 'measured', pvActualMethod: night ? null : 'measured',
      importRate: 20, exportRate: 15, action: 'demand', cost: .1,
    };
  });
  await page.route('**/api/plans/timeline?*', (r) => r.fulfill({ json: { slots } }));
}

test('Today keeps to four legend chips and four series, compares with nothing by default, and never hatches the night', async ({ page }) => {
  await mockHistory(page);
  await page.goto('/');
  const chart = today(page);
  await expect(chart.locator('svg.tl-svg')).toBeVisible();
  // Calm by default: at most four chips, at most four things drawn, no earlier-period overlay. (This history has no grid
  // readings; with them Today adds a Grid lane and chip, five at most: money.spec.ts.)
  expect(await chart.locator('.chart-chips .chart-chip').count()).toBeLessThanOrEqual(4);
  expect((await series(chart)).length).toBeLessThanOrEqual(4);
  await expect(chart.getByRole('group', { name: 'Compare home use with' }).getByRole('button', { name: 'Nothing' })).toHaveAttribute('aria-pressed', 'true');
  await expect(chart).toHaveAttribute('data-compare', 'off');
  // Dashes mean forecast only: every dashed series path is a forecast.
  const dashed = await chart.locator('path.tl-series').evaluateAll((paths) => paths.filter((p) => (p as SVGPathElement).style.strokeDasharray).map((p) => p.getAttribute('class')));
  expect(dashed.every((c) => c?.includes('tl-forecast'))).toBe(true);
  // One long gap (home use, four slots) is washed and named; the short one is bridged; the night's solar is not a gap.
  await expect(chart).toHaveAttribute('data-gap-bands', '1');
  await expect.poll(async () => Number(await chart.getAttribute('data-bridges'))).toBeGreaterThanOrEqual(1);
  await expect(chart.locator('.tl-wash')).toHaveAttribute('data-series', 'home');
  await expect(chart).not.toContainText('Short gap');
  // Hovering the wash names the meter and the hours, and says the night's solar counted as zero elsewhere.
  await chart.locator('.tl-surface').scrollIntoViewIfNeeded();
  const wash = await chart.locator('.tl-wash rect').first().boundingBox();
  await page.mouse.move(wash!.x + wash!.width / 2, wash!.y + wash!.height / 2);
  const tip = chart.locator('.chart-tip');
  await expect(tip).toBeVisible();
  await expect(tip).toContainText(/Home meter offline \d\d:\d\d–\d\d:\d\d/);
  await expect(tip).toContainText('no reading');
  const night = await chart.locator('.tl-surface').boundingBox();
  await page.mouse.move(night!.x + 4, night!.y + night!.height / 2);
  await expect(tip).toContainText('Solar sensor asleep overnight; counted as 0');
  // The chart is a named figure with a one-sentence caption and a table view.
  await expect(chart.locator('figcaption')).toContainText(/Last \d+ h: home used/);
  await chart.getByText('Show as table').click();
  await expect(chart.getByRole('region', { name: /Timeline, slot by slot/ })).toBeVisible();
});

test('comparing with yesterday draws one thin grey line for home use and remembers the choice', async ({ page }) => {
  const requests: string[] = [];
  page.on('request', (r) => { if (r.url().includes('/api/telemetry/history')) requests.push(r.url()); });
  await page.goto('/');
  const chart = today(page);
  await expect(chart.locator('svg.tl-svg')).toBeVisible();
  const compare = chart.getByRole('group', { name: 'Compare home use with' });
  await compare.getByRole('button', { name: 'Yesterday' }).click();
  await expect(chart).toHaveAttribute('data-compare', 'day');
  await expect(chart.getByText('Dashed grey line: home use yesterday, same times of day')).toBeVisible();
  const earlier = chart.locator('path.tl-earlier');
  await expect(earlier).toHaveCount(1);
  // A neutral dashed reference line, drawn by the stylesheet (no series colour of its own).
  expect(await earlier.evaluate((p) => getComputedStyle(p).strokeDasharray)).not.toBe('none');
  expect(Number(await earlier.evaluate((p) => getComputedStyle(p).opacity))).toBeCloseTo(.8, 2);
  // Half-hour history, fetched up to the end of the slot in progress (yesterday is wholly in the past).
  const url = new URL(requests.at(-1)!);
  expect(url.searchParams.get('slotMinutes')).toBe('30');
  expect(Date.parse(url.searchParams.get('to')!)).toBeGreaterThan(Date.now() - 86400000);
  await page.reload();
  await expect(today(page)).toHaveAttribute('data-compare', 'day');
  await today(page).getByRole('group', { name: 'Compare home use with' }).getByRole('button', { name: 'Nothing' }).click();
  await page.reload();
  await expect(today(page).getByRole('group', { name: 'Compare home use with' }).getByRole('button', { name: 'Nothing' })).toHaveAttribute('aria-pressed', 'true');
  await expect(today(page)).toHaveAttribute('data-compare', 'off');
});

test('a failed comparison request is reported without hiding the chart', async ({ page }) => {
  await page.route('**/api/telemetry/history?*', (r) => r.fulfill({ status: 503, json: { error: 'History outage' } }));
  await page.goto('/');
  await today(page).getByRole('group', { name: 'Compare home use with' }).getByRole('button', { name: 'Yesterday' }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'Comparison unavailable: History outage' })).toBeVisible();
  await expect(today(page).locator('svg.tl-svg')).toBeVisible();
});

test('the Plan timeline shows the whole plan with at most five chips, and the accuracy overlay on request', async ({ page }) => {
  await page.goto('/');
  await openPage(page, 'Plan');
  const chart = plan(page);
  await expect(chart.locator('svg.tl-svg')).toBeVisible();
  expect(await chart.locator('.chart-chips .chart-chip').count()).toBeLessThanOrEqual(5);
  expect((await series(chart)).length).toBeLessThanOrEqual(5);
  const state = await (await page.request.get('/api/state')).json();
  const last = state.plan.slots.at(-1);
  await expect(chart).toHaveAttribute('data-end', new Date(Date.parse(last.time) + last.durationMinutes * 60000).toISOString());
  await expect(chart.locator('[data-series="home-accuracy"]')).toHaveCount(0);
  await chart.getByRole('switch', { name: 'Show forecast accuracy' }).click();
  await expect(chart.locator('[data-series="home-accuracy"]')).toHaveCount(1);
  // The legend chips hide and show a series, and say so.
  const solar = chart.getByRole('button', { name: 'Solar' });
  await solar.click();
  await expect(solar).toHaveAttribute('aria-pressed', 'false');
  expect(await series(chart)).not.toContain('solar');
  await solar.click();
  // The range control narrows the window.
  await chart.getByRole('group', { name: 'Time range' }).getByRole('button', { name: 'Past 24 h' }).click();
  const end = Date.parse((await chart.getAttribute('data-end'))!);
  expect(end - Date.now()).toBeLessThanOrEqual(60000);
});

test('the action ribbon tags free sessions and the plan starts mid-half-hour without a hole', async ({ page }) => {
  await page.route('**/api/state*', async (route) => {
    const response = await fetchFresh(route);
    const body = await response.json();
    const start = Math.floor(Date.now() / 1800000) * 1800000;
    // A plan made five minutes before the half-hour, then a free session two hours later.
    body.plan.slots = body.plan.slots.map((s: Record<string, unknown>, i: number) => ({
      ...s,
      time: new Date(i === 0 ? start + 25 * 60000 : start + i * 1800000).toISOString(),
      durationMinutes: i === 0 ? 5 : 30,
      importRate: i === 5 ? 0 : s.importRate,
    }));
    await fulfillRewritten(route, response, body);
  });
  await page.goto('/');
  const chart = today(page);
  await expect(chart.locator('svg.tl-svg')).toBeVisible();
  await expect(chart.locator('.tl-block[data-tags~="free"]').first()).toBeAttached();
  // The half-hour in progress is one forecast step, not a 5-minute sliver beside a hole.
  const forecast = await chart.locator('path.tl-forecast[data-series="home"]').getAttribute('d');
  expect(forecast?.match(/M/g)?.length).toBe(1);
});

test('on a phone the reading sits in a fixed strip above the plot, and the chart fits', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/');
  const chart = today(page);
  await expect(chart.locator('svg.tl-svg')).toBeVisible();
  const strip = chart.locator('.tl-readout');
  await expect(strip).toContainText('Touch the chart to read a half-hour');
  await chart.locator('.tl-surface').scrollIntoViewIfNeeded();
  const surface = (await chart.locator('.tl-surface').boundingBox())!;
  await page.mouse.click(surface.x + surface.width * .7, surface.y + surface.height / 2);
  await expect(strip).toContainText(/\d\d:\d\d–\d\d:\d\d/);
  await expect(chart.locator('.chart-tip-floating')).toHaveCount(0);
  // About twelve hours either side of now by default.
  const end = Date.parse((await chart.getAttribute('data-end'))!);
  expect(end - Date.now()).toBeLessThanOrEqual(12 * 3600000 + 60000);
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390);
  // No text in the chart is smaller than 12px.
  const sizes = await chart.locator('svg text').evaluateAll((els) => els.map((el) => parseFloat(getComputedStyle(el).fontSize)));
  expect(Math.min(...sizes)).toBeGreaterThanOrEqual(12);
});

test('the keyboard reads the timeline slot by slot', async ({ page }) => {
  await page.goto('/');
  const slider = today(page).getByRole('slider', { name: /Read the timeline slot by slot/ });
  await slider.focus();
  await page.keyboard.press('ArrowRight');
  await expect(slider).toHaveAttribute('aria-valuetext', /\d\d:\d\d–\d\d:\d\d · .+\. (Solar|Home) [\d.]+ kWh · /);
  await expect(today(page).locator('.chart-tip')).toBeVisible();
});

const summary = (from: string, load: number, loadCoverage: number, pv: number, pvCoverage: number, cost: number) => ({
  from, to: new Date().toISOString(), importCostGbp: cost, exportCreditGbp: 0, observedNetCostGbp: cost, costCoverageFraction: 1, costObservedSeconds: 3600, sources: ['HomeAssistant'], limitations: [],
  metrics: { load: { energyKwh: load, observedSeconds: 3600, coverageFraction: loadCoverage, missingIntervals: 0 }, pv: { energyKwh: pv, observedSeconds: 3600, coverageFraction: pvCoverage, missingIntervals: 0 } },
});

test('Overview cards compare today so far with the same time yesterday', async ({ page }) => {
  const requested: { from: string; to: string }[] = [];
  await page.route('**/api/telemetry/summary?*', (route) => {
    const url = new URL(route.request().url()), from = url.searchParams.get('from')!, to = url.searchParams.get('to')!;
    requested.push({ from, to });
    // Yesterday's midnight is always more than a day ago; today's never is.
    const yesterday = Date.parse(from) < Date.now() - 86400000;
    return route.fulfill({ json: yesterday ? summary(from, 8, 1, 1, .5, 1.5) : summary(from, 10, 1, 3, 1, 1.2) });
  });
  await page.goto('/');
  const load = page.getByRole('article', { name: 'Home use', exact: true });
  await expect(load.locator('.stat-footnote')).toHaveText('Yesterday by now: 8.0 kWh');
  const cost = page.getByRole('article', { name: 'Net cost today', exact: true });
  await expect(cost.locator('.stat-footnote')).toHaveText('Yesterday by now: paid £1.50');
  // Yesterday's solar was only half measured, so it is not compared at all (no hedging line either).
  const solar = page.getByRole('article', { name: 'Solar', exact: true });
  await expect(solar).not.toContainText('Yesterday');
  await expect(solar).not.toContainText('not compared');
  // The earlier window runs from yesterday's midnight to the same local clock time.
  await expect.poll(() => new Set(requested.map((r) => r.from)).size).toBeGreaterThanOrEqual(2);
  // Only the 'so far' pair (today until now, and yesterday until the same time); Today also asks for last night's
  // cheap window and the whole of yesterday.
  const soFar = requested.filter((r) => [0, 86400000].some((back) => Math.abs(Date.now() - back - Date.parse(r.to)) < 3600000));
  const distinct = [...new Map(soFar.map((r) => [r.from, r])).values()];
  const [now, earlier] = distinct.sort((a, b) => Date.parse(b.from) - Date.parse(a.from));
  expect(Date.parse(earlier.to) - Date.parse(earlier.from)).toBeCloseTo(Date.parse(now.to) - Date.parse(now.from), -4);
  expect(Date.parse(now.from) - Date.parse(earlier.from)).toBeGreaterThanOrEqual(23 * 3600000);
});

test('the Data page draws diverging daily bars for a week and the half-hour profile for a single day', async ({ page }) => {
  await page.goto('/');
  await openPage(page, 'Energy');
  const period = page.getByRole('group', { name: 'Period' });
  // A single day: the half-hour profile, not one lonely bar group. Yesterday, because today has no complete reading in the
  // first minutes after midnight.
  await period.getByRole('button', { name: 'Yesterday', exact: true }).click();
  await expect(page.locator('figure.tl-day')).toBeVisible();
  await expect(page.locator('figure.chart-daily-energy')).toHaveCount(0);
  // Today so far is compared with the same time yesterday.
  await period.getByRole('button', { name: 'Today', exact: true }).click();
  await expect(page.locator('.energy-period-note')).toContainText('Compared with this time yesterday');
  const change = page.locator('.energy-chip .energy-change').first();
  await expect(change).toBeVisible();
  expect(await change.evaluate((el) => parseFloat(getComputedStyle(el).fontSize))).toBeGreaterThanOrEqual(12);
  // A week (the default): one diverging bar per day, thin bars, energy in above zero and out below.
  await period.getByRole('button', { name: '7 days', exact: true }).click();
  const daily = page.locator('figure.chart-daily-energy');
  await expect(daily).toBeVisible();
  await expect(daily).toHaveAttribute('data-days', '7');
  const bars = await daily.locator('path.daily-bar').evaluateAll((els) => els.map((el) => el.getBoundingClientRect()).filter((r) => r.width > 0));
  expect(bars.length).toBeGreaterThan(7);
  for (const bar of bars) expect(bar.width).toBeLessThanOrEqual(56.5);
  await expect(daily.locator('[data-metric="pv"]').first()).toBeAttached();
  await expect(daily.locator('[data-metric="home"]').first()).toBeAttached();
  // Dates read "Sun 4 Oct", in the table too.
  await daily.getByText('Show as table').click();
  await expect(daily.getByRole('cell').first()).toHaveText(/^[A-Z][a-z]{2} \d{1,2} [A-Z][a-z]{2}$/);
  await expect(page.locator('figure.chart-daily-cost')).toBeVisible();
});

test('daily net cost is paid minus earned with each side priced, and home use never loses the car twice', async ({ page }) => {
  // Live 2 Oct: both meters 99.97 % priced, but the stricter matched-period figure covered only 37 % of the day, and the
  // chart said "n/a". And an install whose load meter excludes the car must keep its whole load as home use.
  await page.route('**/api/telemetry/daily?*', async (route) => {
    const response = await fetchFresh(route);
    const days = await response.json();
    const rewritten = days.map((d: { metrics: Record<string, unknown> }) => ({
      ...d,
      metrics: {
        ...d.metrics,
        load: { energyKwh: 12, observedSeconds: 86400, coverageFraction: 1, missingIntervals: 0 },
        ev: { energyKwh: 30, observedSeconds: 86400, coverageFraction: 1, missingIntervals: 0 },
      },
      home: null,
      loadIncludesEv: false,
      importCostGbp: 2.2,
      exportCreditGbp: 0.08,
      observedNetCostGbp: 9.99,
      costCoverageFraction: 0.37,
      netCostGbp: 2.12,
      importCostCoverage: 1,
      exportCostCoverage: 1,
      // The energy figures only: the standing charge on top has its own checks (money.spec.ts).
      standingChargeGbp: null,
    }));
    await fulfillRewritten(route, response, rewritten);
  });
  await page.goto('/');
  await openPage(page, 'Energy');
  // Four days to today, by the London calendar (the UTC date is a day behind in the hour after London midnight).
  const from = londonDate(Date.now() - 3 * 86400000);
  await page.getByRole('group', { name: 'Period' }).getByRole('button', { name: 'Custom', exact: true }).click();
  await page.getByLabel('Energy from date').fill(from);
  const cost = page.locator('figure.chart-daily-cost');
  await expect(cost).toBeVisible();
  await expect(cost.locator('text.daily-na')).toHaveCount(0);
  await expect(cost).toContainText('£8.48');
  const daily = page.locator('figure.chart-daily-energy');
  await daily.getByText('Show as table').click();
  const row = daily.getByRole('row').nth(1);
  await expect(row.getByRole('cell').nth(1)).toHaveText('12.00');
  await expect(row).toContainText('£2.12');
  await expect(row).not.toContainText('priced');
  await expect(row).not.toContainText('£9.99');
  // The default week may still be loading behind the custom period; let it go rather than fail on a request in flight.
  await page.unrouteAll({ behavior: 'ignoreErrors' });
});
