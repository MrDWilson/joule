import { test, expect, type APIResponse } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

// The slim state header and paged history endpoints that UI pages build on. The schema is the same file the
// backend contract test validates against (docs/api/state-header.schema.json), mirrored by StateHeader in src/types.ts.
const schema = JSON.parse(readFileSync(fileURLToPath(new URL('../../docs/api/state-header.schema.json', import.meta.url)), 'utf8'));

type Schema = Record<string, any>;
function validate(rule: Schema, value: unknown, at: string, errors: string[]): void {
  if (rule.$ref) {
    const target = (rule.$ref as string).replace(/^#\//, '').split('/').reduce((node: Schema, key) => node[key], schema);
    return validate(target, value, at, errors);
  }
  if (rule.oneOf) {
    const matches = (rule.oneOf as Schema[]).filter(option => { const e: string[] = []; validate(option, value, at, e); return e.length === 0; }).length;
    if (matches !== 1) errors.push(`${at}: matched ${matches} of oneOf`);
    return;
  }
  if (rule.type) {
    const allowed: string[] = Array.isArray(rule.type) ? rule.type : [rule.type];
    const actual = value === null ? 'null' : Array.isArray(value) ? 'array' : typeof value === 'number' ? (Number.isInteger(value) ? 'integer' : 'number') : typeof value;
    if (!allowed.includes(actual) && !(actual === 'integer' && allowed.includes('number'))) { errors.push(`${at}: ${actual} is not ${allowed.join('|')}`); return; }
  }
  if (value === null) return;
  if ('const' in rule && rule.const !== value) errors.push(`${at}: expected ${rule.const}`);
  if (rule.enum && !rule.enum.includes(value)) errors.push(`${at}: ${String(value)} not in enum`);
  if (Array.isArray(value)) {
    if (rule.maxItems !== undefined && value.length > rule.maxItems) errors.push(`${at}: more than ${rule.maxItems} items`);
    if (rule.items) value.forEach((item, i) => validate(rule.items, item, `${at}[${i}]`, errors));
  } else if (typeof value === 'object') {
    const record = value as Record<string, unknown>;
    for (const name of rule.required ?? []) if (!(name in record)) errors.push(`${at}.${name}: missing`);
    for (const [name, child] of Object.entries(rule.properties ?? {})) if (name in record) validate(child as Schema, record[name], `${at}.${name}`, errors);
  }
}

async function json(response: APIResponse) { expect(response.ok(), `${response.url()} ${response.status()}`).toBeTruthy(); return response.json(); }

test('the slim state header matches the published contract and is small', async ({ request }) => {
  const response = await request.get('/api/state?view=header');
  const header = await json(response);
  const errors: string[] = [];
  validate(schema, header, '$', errors);
  expect(errors).toEqual([]);
  expect(header.state).toBeUndefined();
  expect((await response.body()).length).toBeLessThan(120_000);
  expect(header.latestInvestigations.length).toBeGreaterThan(0);
  expect(header.latestInvestigations[0].headline.length).toBeGreaterThan(0);
});

test('an unchanged header answers 304 to its ETag', async ({ request }) => {
  const first = await request.get('/api/state?view=header');
  const etag = first.headers()['etag'];
  expect(etag).toMatch(/^W\/"[0-9a-f]{32}"$/);
  expect(first.headers()['cache-control']).toContain('no-cache');
  const again = await request.get('/api/state?view=header', { headers: { 'If-None-Match': etag } });
  expect(again.status()).toBe(304);
});

test('history pages through investigations, activities and usage', async ({ request }) => {
  const page = await json(await request.get('/api/investigations?limit=2'));
  expect(page.items).toHaveLength(2);
  expect(page.total).toBeGreaterThanOrEqual(3);
  expect(page.nextCursor).toBeTruthy();
  const next = await json(await request.get(`/api/investigations?limit=2&cursor=${encodeURIComponent(page.nextCursor)}`));
  expect(next.items.map((x: { id: string }) => x.id)).not.toContain(page.items[0].id);
  expect(Date.parse(next.items[0].at)).toBeLessThanOrEqual(Date.parse(page.items[1].at));
  const detail = await json(await request.get(`/api/investigations/${page.items[0].id}`));
  expect(detail.id).toBe(page.items[0].id);
  const activities = await json(await request.get('/api/activities?limit=5'));
  expect(Array.isArray(activities.items)).toBeTruthy();
  const usage = await json(await request.get('/api/usage?days=7'));
  expect(usage.timeZone).toBeTruthy();
  expect(Array.isArray(usage.daily)).toBeTruthy();
  const settings = await json(await request.get('/api/settings'));
  expect(settings.settings.length).toBeGreaterThan(0);
  expect(Array.isArray(settings.settings[0].options)).toBeTruthy();
});

test('unknown API paths answer JSON 404 and health is public with a version', async ({ request }) => {
  const missing = await request.get('/api/does-not-exist');
  expect(missing.status()).toBe(404);
  expect((await missing.json()).error).toBeTruthy();
  const health = await json(await request.get('/api/health'));
  expect(health.status).toBe('ok');
  expect(health.version).toMatch(/^\d+\.\d+\.\d+/);
});
