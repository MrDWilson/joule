import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { openPage as open } from './support/navigation';

// Every page at desktop, tablet landscape, tablet portrait and phone widths must lay out cleanly:
// nothing runs off the right edge, no text spills out of its box and no two controls overlap.
test.use({ timezoneId: 'Europe/London' });
test.describe.configure({ timeout: 120_000 });

const viewports = [
  { name: 'desktop', width: 1440, height: 900 },
  { name: 'tablet landscape', width: 1024, height: 768 },
  { name: 'tablet portrait', width: 768, height: 1024 },
  { name: 'phone', width: 390, height: 844 },
] as const;
const pages = ['Today', 'Plan', 'Energy', 'Files', 'Checks', 'Suggestions', 'Trials', 'Setup', 'Settings', 'Changes', 'AI checks', 'Sensors', 'About'];
const escape = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

/** Runs in the browser. Returns one readable line per layout defect found inside `rootSelector`. */
function layoutDefects(rootSelector: string): string[] {
  const vw = document.documentElement.clientWidth;
  const root = document.querySelector(rootSelector) ?? document.body;
  const out: string[] = [];
  const describe = (el: Element) => {
    const cls = typeof el.className === 'string' && el.className ? `.${el.className.trim().split(/\s+/).join('.')}` : '';
    const text = (el.textContent || '').replace(/\s+/g, ' ').trim().slice(0, 50);
    return `<${el.tagName.toLowerCase()}${cls}> "${text}"`;
  };
  const visible = (el: Element) => {
    const r = el.getBoundingClientRect();
    if (r.width < 1 || r.height < 1) return false;
    // Content of a closed <details> still has a layout box in current Chrome (it is hidden with content-visibility),
    // so check rendered visibility as well as the element's own styles.
    if (!el.checkVisibility()) return false;
    const cs = getComputedStyle(el);
    return cs.visibility !== 'hidden' && cs.display !== 'none' && Number(cs.opacity) > 0;
  };
  // A scrolling or clipping ancestor that itself sits inside the viewport contains the overflow.
  const contained = (el: Element) => {
    for (let a = el.parentElement; a && a !== document.body; a = a.parentElement) {
      const cs = getComputedStyle(a);
      if (['auto', 'scroll', 'hidden', 'clip'].includes(cs.overflowX) && a.getBoundingClientRect().right <= vw + 1) return true;
    }
    return false;
  };
  if (rootSelector === 'body' && document.documentElement.scrollWidth > vw)
    out.push(`document scrolls horizontally: scrollWidth ${document.documentElement.scrollWidth} > ${vw}`);
  const all = Array.from(root.querySelectorAll('*')).filter(el => !el.closest('svg') || el.tagName === 'svg');
  for (const el of all) {
    if (!visible(el)) continue;
    const r = el.getBoundingClientRect();
    if (r.right > vw + 1 && !contained(el)) out.push(`beyond right edge (${Math.round(r.right)} > ${vw}): ${describe(el)}`);
    if (r.left < -1 && !contained(el)) out.push(`beyond left edge (${Math.round(r.left)}): ${describe(el)}`);
    const cs = getComputedStyle(el);
    if (el.children.length === 0 && (el.textContent || '').trim() && !['INPUT', 'SELECT', 'TEXTAREA', 'OPTION'].includes(el.tagName)
      && cs.overflowX === 'visible' && cs.display !== 'inline' && el.scrollWidth > el.clientWidth + 2)
      out.push(`text overflows its box (${el.scrollWidth} > ${el.clientWidth}): ${describe(el)}`);
  }
  // The part of an element its scrolling or clipping ancestors let through: rows scrolled out of a table's scroll box
  // can't overlap anything outside it.
  const clipped = (el: Element) => {
    let { left, top, right, bottom } = el.getBoundingClientRect();
    for (let a = el.parentElement; a && a !== document.body; a = a.parentElement) {
      const cs = getComputedStyle(a);
      if (cs.overflowX === 'visible' && cs.overflowY === 'visible') continue;
      const c = a.getBoundingClientRect();
      left = Math.max(left, c.left); top = Math.max(top, c.top); right = Math.min(right, c.right); bottom = Math.min(bottom, c.bottom);
    }
    return new DOMRect(left, top, Math.max(0, right - left), Math.max(0, bottom - top));
  };
  // Interactive controls in the same layer (page, fixed drawer, dialog) must not overlap one another.
  const layer = (el: Element) => { for (let a: Element | null = el; a; a = a.parentElement) if (getComputedStyle(a).position === 'fixed') return a; return null; };
  const stickyBand = (el: Element) => { for (let a: Element | null = el; a; a = a.parentElement) { const p = getComputedStyle(a).position; if (p === 'sticky') return a; if (p === 'fixed') return null; } return null; };
  const controls = Array.from(root.querySelectorAll('button, a[href], input, select, textarea, summary, [role="switch"], [role="tab"]'))
    .filter(el => visible(el) && !el.closest('svg') && !(el instanceof HTMLInputElement && el.type === 'hidden'))
    .map(el => ({ el, r: clipped(el), layer: layer(el) }))
    .filter(c => c.r.width > 1 && c.r.height > 1)
    .sort((a, b) => a.r.top - b.r.top);
  for (let i = 0; i < controls.length; i++)
    for (let j = i + 1; j < controls.length && controls[j].r.top < controls[i].r.bottom; j++) {
      const a = controls[i], b = controls[j];
      if (a.layer !== b.layer || a.el.contains(b.el) || b.el.contains(a.el)) continue;
      // A bottom sheet's sticky header and footer sit over its scrolling body by design: what scrolls beneath them is
      // reached by scrolling, so only controls in the same sticky band (or both outside one) can collide.
      if (stickyBand(a.el) !== stickyBand(b.el)) continue;
      const x = Math.min(a.r.right, b.r.right) - Math.max(a.r.left, b.r.left), y = Math.min(a.r.bottom, b.r.bottom) - Math.max(a.r.top, b.r.top);
      if (x > 1 && y > 1) out.push(`controls overlap: ${describe(a.el)} and ${describe(b.el)}`);
    }
  // Cards keep their controls inside their own border (a row of actions must wrap, not spill out).
  for (const card of Array.from(root.querySelectorAll('.recommendation, .config-change, .investigation-next-step, .modal, .panel'))) {
    if (!visible(card)) continue;
    const c = card.getBoundingClientRect();
    for (const control of controls) {
      if (!card.contains(control.el)) continue;
      let scrolls = false;
      for (let a = control.el.parentElement; a && a !== card; a = a.parentElement)
        if (['auto', 'scroll', 'hidden', 'clip'].includes(getComputedStyle(a).overflowX)) scrolls = true;
      if (scrolls) continue;
      const r = control.r;
      if (r.right > c.right + 1 || r.left < c.left - 1) out.push(`control outside its card: ${describe(control.el)} in ${describe(card).slice(0, 60)}`);
    }
  }
  return Array.from(new Set(out)).slice(0, 30);
}

async function openPage(page: Page, name: string) {
  await expect(page.locator('.page-heading h1')).toBeAttached();
  await open(page, name);
  await expect(page.getByRole('dialog')).toHaveCount(0);
  // Charts measure their container after layout; give them a moment to settle.
  await page.waitForTimeout(400);
}

function watchErrors(page: Page) {
  const errors: string[] = [];
  page.on('pageerror', e => errors.push(`page error: ${e.message}`));
  page.on('console', m => { if (m.type() === 'error') errors.push(`console error: ${m.text()}`); });
  return errors;
}

// Marks fixture writes as dashboard requests, which the API requires when it runs without an access key.
const headers = { 'X-Joule-Request': '1' };

async function ready(request: APIRequestContext) {
  const post = async (path: string, data: unknown) => { const r = await request.post(`/api${path}`, { data, headers }); expect(r.ok(), await r.text()).toBeTruthy(); };
  let s = await (await request.get('/api/state')).json();
  if (s.state.pendingFileReload) {
    await post('/collect', {});
    s = await (await request.get('/api/state')).json();
    await post('/files/reload-acknowledge', { revision: s.state.revision, notes: 'Responsive layout fixture runtime checked.' });
  }
  await post('/mode', { mode: 'Recommend' });
}

for (const viewport of viewports) {
  test(`every page lays out cleanly at ${viewport.name} width (${viewport.width}px)`, async ({ page }) => {
    const errors = watchErrors(page);
    await page.setViewportSize({ width: viewport.width, height: viewport.height });
    await page.goto('/');
    await expect(page.getByRole('heading', { level: 1, name: 'Today', exact: true })).toBeAttached();
    for (const name of pages) {
      await openPage(page, name);
      expect(await page.evaluate(layoutDefects, 'body'), `${name} at ${viewport.width}px`).toEqual([]);
      // Disclosures hide long evidence, entity ids and tables; they must fit once opened too.
      const closed = await page.locator('main details:not([open]) > summary').count();
      if (closed) {
        await page.evaluate(() => document.querySelectorAll('main details').forEach(d => { (d as HTMLDetailsElement).open = true; }));
        await page.waitForTimeout(150);
        expect(await page.evaluate(layoutDefects, 'body'), `${name} at ${viewport.width}px with details expanded`).toEqual([]);
      }
    }
    expect(errors).toEqual([]);
  });
}

test('dialogs and the navigation drawer fit a phone screen', async ({ page, request }) => {
  await ready(request);
  const errors = watchErrors(page);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/');
  await expect(page.getByRole('heading', { level: 1, name: 'Today', exact: true })).toBeAttached();
  const dialog = page.getByRole('dialog');
  const fits = async (label: string) => {
    await expect(dialog).toBeVisible();
    await page.waitForTimeout(250);
    const box = (await dialog.boundingBox())!;
    expect(box.x, `${label} left edge`).toBeGreaterThanOrEqual(0);
    expect(box.y, `${label} top edge`).toBeGreaterThanOrEqual(0);
    expect(box.x + box.width, `${label} right edge`).toBeLessThanOrEqual(390);
    expect(box.y + box.height, `${label} bottom edge`).toBeLessThanOrEqual(844);
    expect(await page.evaluate(layoutDefects, '[role="dialog"]'), label).toEqual([]);
  };
  const close = async () => { await page.keyboard.press('Escape'); await expect(dialog).toHaveCount(0); };

  await openPage(page, 'Suggestions');
  await page.getByRole('button', { name: 'Review', exact: true }).first().click();
  await fits('recommendation review');
  await close();

  await openPage(page, 'Settings');
  await page.getByRole('button', { name: /^House load scaling\b/ }).click();
  await fits('setting sheet');
  await page.getByRole('switch', { name: 'Allow automatic changes to House load scaling', exact: true }).click();
  await fits('automatic permission bounds');
  // Closing the limits goes back to the setting's sheet.
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog', { name: 'House load scaling' })).toBeVisible();
  await close();

  await openPage(page, 'Files');
  await page.getByRole('button', { name: 'Save a copy now', exact: true }).click();
  await fits('file capture');
  await close();

  // A second revision makes the first one restorable.
  const s = await (await request.get('/api/state')).json();
  const before = s.state.settings.find((x: { key: string }) => x.key === 'pv_scaling').value;
  const changed = await request.post('/api/settings/pv_scaling', { data: { revision: s.state.revision, value: before === '0.97' ? '0.96' : '0.97' }, headers });
  expect(changed.ok(), await changed.text()).toBeTruthy();
  await page.reload();
  await openPage(page, 'Changes');
  await page.getByRole('button', { name: /^More actions for/ }).last().click();
  await page.getByRole('button', { name: 'Restore settings to this point' }).click();
  await fits('restore snapshot');
  await close();
  expect(errors).toEqual([]);
});

test('reply forms, reply threads and reply outcomes fit at desktop and phone widths', async ({ page }) => {
  const errors = watchErrors(page);
  for (const viewport of [viewports[0], viewports[3]]) {
    await page.setViewportSize({ width: viewport.width, height: viewport.height });
    await page.goto('/');
    await openPage(page, 'Suggestions');
    const proposal = page.locator('article.recommendation').first();
    await proposal.getByRole('button', { name: /^Reply( again)?$/ }).click();
    // On a phone the composer is a bottom sheet; on a desktop it opens in the card.
    const composer = viewport.width === 1440 ? proposal : page.getByRole('dialog', { name: /^Reply about/ });
    await expect(composer.getByRole('textbox', { name: 'Your reply' })).toBeVisible();
    expect(await page.evaluate(layoutDefects, 'body'), `reply form at ${viewport.width}px`).toEqual([]);
    if (viewport.width === 1440) {
      // The demo answers a question, so the item stays open with its thread.
      await proposal.getByRole('textbox', { name: 'Your reply' }).fill('Why is the evening forecast too high when we often cook late?');
      await proposal.getByRole('button', { name: 'Send reply', exact: true }).click();
      await expect(proposal.getByRole('list', { name: 'Replies' })).toContainText('Answer');
      await expect(proposal.getByRole('button', { name: 'Reply again', exact: true })).toBeVisible();
      expect(await page.evaluate(layoutDefects, 'body'), `reply thread at ${viewport.width}px`).toEqual([]);
    } else {
      await composer.getByRole('button', { name: 'Close reply', exact: true }).click();
      await expect(composer).toHaveCount(0);
      // A reasoned reply is accepted: the configuration file change is dismissed and the outcome opens in a dialog.
      const change = page.locator('article.config-change').first();
      await change.getByRole('button', { name: /^Reply/ }).click();
      const sheet = page.getByRole('dialog', { name: /^Reply about/ });
      await sheet.getByRole('textbox', { name: 'Your reply' }).fill('Leave apps.yaml as it is because export is metered by another sensor.');
      const sheetBox = (await sheet.boundingBox())!;
      expect(sheetBox.x + sheetBox.width).toBeLessThanOrEqual(viewport.width);
      expect(sheetBox.y + sheetBox.height).toBeLessThanOrEqual(viewport.height + 1);
      await sheet.getByRole('button', { name: 'Send reply', exact: true }).click();
      const dialog = page.getByRole('dialog', { name: 'The AI agreed' });
      await expect(dialog).toBeVisible();
      await page.waitForTimeout(250);
      const box = (await dialog.boundingBox())!;
      expect(box.x).toBeGreaterThanOrEqual(0);
      expect(box.x + box.width).toBeLessThanOrEqual(viewport.width);
      expect(box.y + box.height).toBeLessThanOrEqual(viewport.height);
      expect(await page.evaluate(layoutDefects, '[role="dialog"]'), `reply outcome at ${viewport.width}px`).toEqual([]);
      await dialog.getByRole('button', { name: 'Close', exact: true }).last().click();
      await expect(dialog).toHaveCount(0);
    }
  }
  expect(errors).toEqual([]);
});

test('the energy chart comparison toggle and "now" marker fit at every width', async ({ page }) => {
  const errors = watchErrors(page);
  for (const viewport of viewports) {
    await page.setViewportSize({ width: viewport.width, height: viewport.height });
    await page.goto('/');
    await expect(page.locator('.page-heading h1')).toBeAttached();
    const panel = page.locator('section.panel', { has: page.getByRole('heading', { name: 'Today and the plan ahead', exact: true }) });
    const compare = panel.getByRole('group', { name: 'Compare home use with' });
    await compare.getByRole('button', { name: 'Yesterday', exact: true }).click();
    await expect(compare.getByRole('button', { name: 'Yesterday', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await expect(panel.locator('figure.tl-today')).toHaveAttribute('data-compare', 'day');
    expect(await page.evaluate(layoutDefects, 'body'), `comparison at ${viewport.width}px`).toEqual([]);
    // The "Now" pill must not sit on top of the y-axis tick labels.
    const collisions = await panel.evaluate(el => {
      const marker = Array.from(el.querySelectorAll('.tl-now text')).map(t => t.getBoundingClientRect());
      const ticks = Array.from(el.querySelectorAll('text.tl-tick')).map(t => t.getBoundingClientRect());
      if (!marker.length || !ticks.length) return -1;
      return marker.flatMap(m => ticks.filter(t => Math.min(m.right, t.right) - Math.max(m.left, t.left) > 0 && Math.min(m.bottom, t.bottom) - Math.max(m.top, t.top) > 0)).length;
    });
    expect(collisions, `"now" label collides with y-axis ticks at ${viewport.width}px`).toBe(0);
    await compare.getByRole('button', { name: 'Nothing', exact: true }).click();
  }
  expect(errors).toEqual([]);
});

// Real Home Assistant entity ids (Octopus Energy especially) are very long; they must wrap inside their own column.
const longIds = {
  import_tariff: 'sensor.octopus_energy_electricity_00a0000000_1000000000000_current_rate',
  export_tariff: 'sensor.octopus_energy_electricity_00a0000000_1000000000001_export_current_rate',
  intelligent_slots: 'binary_sensor.octopus_energy_00000000_0000_4000_8000_000000000000_intelligent_dispatching',
  ev: 'sensor.hypervolt_session_energy_total_increasing',
};

test('long sensor entity ids break only at dots and underscores, inside their row, at every width', async ({ page }) => {
  const errors = watchErrors(page);
  const status = await (await page.request.get('/api/telemetry/status')).json();
  const at = new Date().toISOString();
  status.demo = false;
  for (const [key, entityId] of Object.entries(longIds)) {
    status.entityMappings[key] = entityId;
    status.latestReadings[key] = { value: key === 'intelligent_slots' ? null : 17.2, unit: key.endsWith('tariff') ? 'p/kWh' : 'kWh', time: at, status: 'observed', entityId, source: 'HomeAssistant', rawState: key === 'intelligent_slots' ? 'off' : '0.172', rawUnit: 'GBP/kWh' };
  }
  await page.route('**/api/telemetry/status', route => route.fulfill({ json: status }));
  for (const viewport of viewports) {
    await page.setViewportSize({ width: viewport.width, height: viewport.height });
    await page.goto('/');
    await openPage(page, 'Energy');
    const sensors = page.locator('.sensor-health');
    await sensors.getByRole('button', { name: /^Show all/ }).click();
    // Open every row so each entity id is laid out.
    for (const summary of await sensors.locator('.sensor-row > summary').all()) await summary.click();
    await expect(sensors.locator('.sensor-detail code').filter({ hasText: longIds.intelligent_slots })).toBeVisible();
    // Measured at the top of the page: the sticky header over scrolled content is not a defect.
    await page.evaluate(() => window.scrollTo(0, 0));
    expect(await page.evaluate(layoutDefects, 'body'), `Energy sensors at ${viewport.width}px`).toEqual([]);
    const spills = await sensors.evaluate(el => Array.from(el.querySelectorAll('.sensor-detail dd code')).flatMap(code => {
      const box = (code.closest('dd') as HTMLElement).getBoundingClientRect();
      const text = code.getBoundingClientRect();
      return text.right > box.right + 1 || text.left < box.left - 1 ? [`${code.textContent} (${Math.round(text.right)} > ${Math.round(box.right)})`] : [];
    }));
    expect(spills, `entity ids spill at ${viewport.width}px`).toEqual([]);
    // Lines break at "." or "_" only: every rendered line ends with one of them, or is the end of the id.
    const breaks = await sensors.evaluate(el => Array.from(el.querySelectorAll('.sensor-detail dd code')).flatMap(code => {
      const range = document.createRange();
      const node = code as HTMLElement;
      const text = node.textContent ?? '';
      const lines: string[] = [];
      let top = -1, line = '';
      const walker = document.createTreeWalker(node, NodeFilter.SHOW_TEXT);
      for (let t = walker.nextNode(); t; t = walker.nextNode())
        for (let i = 0; i < (t.textContent ?? '').length; i++) {
          range.setStart(t, i); range.setEnd(t, i + 1);
          const r = range.getBoundingClientRect();
          if (top >= 0 && r.top > top + 2) { lines.push(line); line = ''; }
          top = r.top; line += t.textContent![i];
        }
      lines.push(line);
      return lines.slice(0, -1).filter(l => !/[._]$/.test(l)).map(l => `${text}: "${l}"`);
    }));
    expect(breaks, `entity ids broken mid-word at ${viewport.width}px`).toEqual([]);
  }
  expect(errors).toEqual([]);
});

test('the brand fits the sidebar, the phone header and the connection screen', async ({ page }) => {
  const errors = watchErrors(page);
  for (const viewport of viewports) {
    await page.setViewportSize({ width: viewport.width, height: viewport.height });
    await page.goto('/');
    await expect(page.locator('.page-heading h1')).toBeAttached();
    // Desktop shows the lockup in the sidebar; phones and tablets show the mark in the header.
    const phone = viewport.width <= 900;
    const container = page.locator(phone ? '.topbar' : '.sidebar');
    const brand = container.locator(phone ? '.topbar-mark' : '.brand');
    await expect(brand).toBeVisible();
    const [b, c] = [(await brand.boundingBox())!, (await container.boundingBox())!];
    expect(b.x, `brand left at ${viewport.width}px`).toBeGreaterThanOrEqual(c.x);
    expect(b.x + b.width, `brand right at ${viewport.width}px`).toBeLessThanOrEqual(c.x + c.width);
    expect(await page.evaluate(layoutDefects, 'body'), `navigation at ${viewport.width}px`).toEqual([]);
  }
  // A missing access key shows the connection screen with the brand mark.
  await page.route('**/api/state', route => route.fulfill({ status: 401, json: { error: 'Enter your application access key.', authMode: 'AccessKey' } }));
  for (const viewport of [viewports[0], viewports[3]]) {
    await page.setViewportSize({ width: viewport.width, height: viewport.height });
    await page.goto('/');
    const mark = page.locator('.connection-screen .connection-mark');
    await expect(mark).toBeVisible();
    const box = (await mark.boundingBox())!;
    expect(box.x).toBeGreaterThanOrEqual(0);
    expect(box.x + box.width).toBeLessThanOrEqual(viewport.width);
    expect(await page.evaluate(layoutDefects, 'body'), `connection screen at ${viewport.width}px`).toEqual([]);
  }
  await page.unrouteAll({ behavior: 'wait' });
  // The 401 is the expected response the connection screen renders from.
  expect(errors.filter(e => !/401|Unauthorized/i.test(e))).toEqual([]);
});
