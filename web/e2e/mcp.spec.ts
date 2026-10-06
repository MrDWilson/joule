import { test, expect } from '@playwright/test';
import { openPage } from './support/navigation';

const unchecked = { configured: true, connected: false, checkedAt: null, tools: [], error: null };
const connected = { ...unchecked, connected: true, checkedAt: '2026-10-02T08:00:00Z', tools: [{ name: 'get_log', description: 'Inspect filtered Predbat logs.' }] };
async function open(page: import('@playwright/test').Page) {
  await page.goto('/');
  await openPage(page, 'AI checks');
  return page.getByRole('region', { name: "Predbat's live tools" });
}

test("Predbat's live tools distinguish set-up, checked, working and unreachable", async ({ page }) => {
  // Explicit protocol-status UI fixture; backend protocol tests cover actual MCP calls.
  await page.route('**/api/mcp/status', route => route.fulfill({ json: unchecked }));
  await page.route('**/api/mcp/discover', route => route.fulfill({ json: connected }));
  const panel = await open(page);
  await expect(panel.getByText('Not checked yet')).toBeVisible();
  await panel.getByRole('button', { name: 'Check now' }).click();
  await expect(panel.getByText('Working · 1 tool')).toBeVisible();
  await panel.getByText('Tools a check can use').click();
  await expect(panel.getByText('get_log', { exact: true })).toBeVisible();
  await page.unroute('**/api/mcp/discover');
  await page.route('**/api/mcp/discover', route => route.fulfill({ json: { ...unchecked, checkedAt: '2026-10-02T08:01:00Z', error: 'MCP could not authenticate. Check the server token.' } }));
  await panel.getByRole('button', { name: 'Check now' }).click();
  await expect(panel.getByText('Not reachable')).toBeVisible();
  await expect(panel.getByText('MCP could not authenticate. Check the server token.')).toBeVisible();
  await expect(panel.getByText('get_log', { exact: true })).toHaveCount(0);
  await expect(panel.getByRole('button', { name: 'Check now' })).toBeEnabled();
  await page.unroute('**/api/mcp/discover');
  await page.route('**/api/mcp/discover', route => route.fulfill({ status: 503, json: { error: 'Discovery service unavailable' } }));
  await panel.getByRole('button', { name: 'Check now' }).click();
  await expect(panel.getByRole('alert')).toContainText('Discovery service unavailable');
  await expect(panel.getByText(/^Working/)).toHaveCount(0);
});

test('reachable tools with nothing a check can read report that limitation', async ({ page }) => {
  await page.route('**/api/mcp/status', route => route.fulfill({ json: { ...connected, tools: [] } }));
  const panel = await open(page);
  await expect(panel.getByText(/^Working/)).toBeVisible();
  await expect(panel).toContainText('offered no tools a check can read');
  await expect(panel.getByText('Tools a check can use')).toHaveCount(0);
});

test('tools that are not set up give the setup lines without pretending to work', async ({ page }) => {
  await page.route('**/api/mcp/status', route => route.fulfill({ json: { ...unchecked, configured: false } }));
  const panel = await open(page);
  await expect(panel.getByText('Not set up', { exact: true })).toBeVisible();
  await expect(panel.getByRole('button', { name: 'Check now' })).toBeDisabled();
  await expect(panel.getByText('Predbat__McpToken', { exact: true })).toBeVisible();
  await expect(panel).toContainText("Demo checks use sample data and don't call these tools");
});

test('a status that fails to load can be tried again', async ({ page }) => {
  await page.route('**/api/mcp/status', route => route.fulfill({ status: 503, json: { error: 'Temporary status failure' } }));
  const panel = await open(page);
  await expect(panel.getByRole('alert')).toContainText('Temporary status failure');
  await page.unroute('**/api/mcp/status');
  await page.route('**/api/mcp/status', route => route.fulfill({ json: connected }));
  await panel.getByRole('button', { name: 'Try again' }).click();
  await expect(panel.getByText('Working · 1 tool')).toBeVisible();
});
