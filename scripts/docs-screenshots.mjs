#!/usr/bin/env node
// Regenerates the README/docs screenshots from a throwaway demo: desktop (1440 px) and phone (390 px) for each page.
//
//   node scripts/docs-screenshots.mjs [--no-build] [--port 5090] [--out docs/screenshots] [--allow-outage]
//
// Builds the dashboard into the API's wwwroot, starts the demo API with a fresh data directory, captures every page
// with Playwright (PLAYWRIGHT_CHANNEL=chrome uses installed Chrome), and stops the API again.
import { spawn, spawnSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { cpSync, mkdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const args = process.argv.slice(2);
const option = (name, fallback) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : fallback; };
const port = Number(option('--port', process.env.SCREENSHOT_PORT || '5090'));
const out = resolve(root, option('--out', 'docs/screenshots'));
const build = !args.includes('--no-build');

// The demo house's meters drop out for 20 minutes every night across 03:00-03:30 house time (Europe/London;
// DemoTelemetry.InOutage), and shots taken then show "No reading" tiles. Its only long outage is a single 40 minutes three
// days before the history is seeded, which today's pages never show. Refuse to run in or just after the nightly dropout
// unless asked to.
const london = new Intl.DateTimeFormat('en-GB', { timeZone: 'Europe/London', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }).formatToParts(new Date());
const houseMinute = Number(london.find(p => p.type === 'hour').value) * 60 + Number(london.find(p => p.type === 'minute').value);
const inOutage = houseMinute >= 3 * 60 && houseMinute < 3 * 60 + 45;
if (inOutage && !args.includes('--allow-outage')) {
  console.error('The demo house is in its nightly meter dropout (03:00-03:45 Europe/London), so screenshots would show missing readings. Run again later, or pass --allow-outage.');
  process.exit(1);
}

// Pages: a hash route for the routed shell, and navigation labels to click on builds without routing. The first
// label that exists is used, so the script works before and after the navigation redesign.
const pages = [
  { name: 'today', hash: '#/today', nav: ['Today', 'Overview'] },
  { name: 'plan', hash: '#/plan', nav: ['Plan'] },
  { name: 'insights', hash: '#/insights', nav: ['Insights', 'Investigations'] },
  { name: 'energy', hash: '#/energy', nav: ['Energy', 'Data'] },
  { name: 'setup', hash: '#/setup', nav: ['Setup', 'Settings'] },
];
const viewports = [
  { suffix: 'desktop', width: 1440, height: 900 },
  { suffix: 'phone', width: 390, height: 844, isMobile: true, hasTouch: true, deviceScaleFactor: 3 },
];

function run(command, commandArgs, cwd = root) {
  const result = spawnSync(command, commandArgs, { cwd, stdio: 'inherit', env: process.env });
  if (result.status !== 0) throw new Error(`${command} ${commandArgs.join(' ')} failed`);
}
const dotnetOnPath = spawnSync('dotnet', ['--version']).status === 0;
const dotnet = process.env.DOTNET || (dotnetOnPath ? 'dotnet' : join(root, '.tools/dotnet/dotnet'));
const env = { ...process.env, DOTNET_CLI_HOME: process.env.DOTNET_CLI_HOME || join(root, '.tools/dotnet-home'), NUGET_PACKAGES: process.env.NUGET_PACKAGES || join(root, '.cache/nuget') };

if (build) {
  run('npm', ['run', 'build'], join(root, 'web'));
  // Copy over the previous build (nothing is deleted; old hashed assets are simply unused).
  cpSync(join(root, 'web/dist'), join(root, 'src/Joule.Api/wwwroot'), { recursive: true, force: true });
  const built = spawnSync(dotnet, ['build', 'src/Joule.Api', '-p:UseSharedCompilation=false', '-v', 'quiet', '-nologo'], { cwd: root, stdio: 'inherit', env });
  if (built.status !== 0) throw new Error('dotnet build failed');
}

// A fresh demo in its own directory each run, so nothing earlier has to be deleted.
const data = join(root, '.cache', `docs-screenshots-${Date.now()}`);
mkdirSync(data, { recursive: true });
const base = `http://127.0.0.1:${port}`;
const api = spawn(dotnet, [join(root, 'src/Joule.Api/bin/Debug/net10.0/Joule.Api.dll'), '--contentRoot', join(root, 'src/Joule.Api')], {
  // Run inside the data folder so Setup shows its settings file as plain "settings.json", not a path on this machine.
  cwd: data, stdio: ['ignore', 'pipe', 'inherit'],
  // App__Demo unset: the out-of-the-box demo, so Setup shows its "Connect my Predbat" form.
  env: { ...env, App__Demo: '', App__AuthMode: 'AccessKey', App__AccessKey: '', App__DataDirectory: data, ASPNETCORE_URLS: base },
});
api.stdout.resume();
const stop = () => { if (api.exitCode === null) api.kill('SIGTERM'); };
process.on('exit', stop); process.on('SIGINT', () => { stop(); process.exit(130); });

try {
  for (let i = 0; ; i++) {
    try { if ((await fetch(`${base}/api/health`)).ok) break; } catch { /* starting */ }
    if (i > 80 || api.exitCode !== null) throw new Error('The demo API did not start.');
    await new Promise(r => setTimeout(r, 250));
  }
  // The demo seeds its measured history in the background; wait for it so charts and tiles are populated.
  for (let i = 0; ; i++) {
    const status = await (await fetch(`${base}/api/telemetry/status`)).json().catch(() => null);
    const readings = Object.values(status?.latestReadings ?? {});
    if (status?.lastCollection && readings.some(r => Date.now() - Date.parse(r.time) < 30 * 60_000)) break;
    if (i > 120) throw new Error('The demo did not seed its measured history.');
    await new Promise(r => setTimeout(r, 500));
  }
  const shell = await (await fetch(`${base}/`)).text();
  if (!shell.includes('id="root"')) throw new Error('The API is not serving the dashboard. Run without --no-build.');

  const require = createRequire(join(root, 'web/package.json'));
  const { chromium } = require('@playwright/test');
  const browser = await chromium.launch({ channel: process.env.PLAYWRIGHT_CHANNEL || undefined });
  mkdirSync(out, { recursive: true });
  for (const viewport of viewports) {
    const context = await browser.newContext({ viewport: { width: viewport.width, height: viewport.height }, isMobile: viewport.isMobile, hasTouch: viewport.hasTouch, deviceScaleFactor: viewport.deviceScaleFactor ?? 1, colorScheme: 'dark', reducedMotion: 'reduce' });
    // Skip the first-run welcome so every shot shows the page itself.
    await context.addInitScript(() => localStorage.setItem('joule.firstRunSeen', '1'));
    const page = await context.newPage();
    for (const target of pages) {
      await page.goto(`${base}/${target.hash}`);
      await page.waitForLoadState('networkidle');
      // Builds without hash routing ignore the hash: click the page's navigation entry, opening the phone menu first
      // if it is collapsed. On routed builds this is a harmless second navigation to the same page.
      const clickNav = async () => {
        for (const label of target.nav) {
          const entry = page.getByRole('button', { name: label, exact: true }).or(page.getByRole('link', { name: label, exact: true })).first();
          if (await entry.isVisible().catch(() => false)) { await entry.click(); return true; }
        }
        return false;
      };
      if (!(await clickNav())) {
        const menu = page.getByRole('button', { name: /menu|open navigation/i }).first();
        if (await menu.isVisible().catch(() => false)) { await menu.click(); await clickNav(); }
      }
      await page.waitForLoadState('networkidle');
      // Park the pointer on empty header space so the last navigation entry clicked isn't shown hovered.
      if (!viewport.hasTouch) await page.mouse.move(Math.round(viewport.width * 0.45), 20);
      await page.waitForTimeout(600); // let charts finish their entry animation
      const file = join(out, `${target.name}-${viewport.suffix}.png`);
      await page.screenshot({ path: file });
      console.log(`saved ${file.replace(root + '/', '')}`);
    }
    await context.close();
  }
  await browser.close();
} finally {
  stop();
}
