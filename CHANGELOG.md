# Changelog

All notable changes to Joule are listed here. Versions follow [semantic versioning](https://semver.org/).

## Unreleased

### Features

- **Grid import, front and centre:** Today has a Grid tile (bought since midnight, what it cost and the average price per kWh, what was sold and earned, a bar per half-hour and yesterday by the same time). Today's timeline has a Grid lane (bought above the line, sold below), and the Energy page has a "Grid each day" chart.
- **Standing charge:** Joule reads the Octopus Energy integration's standing charge sensor on the same meter as your import rate by itself, or you can map any sensor (`HomeAssistant__Entities__StandingCharge`) or type a figure in pence per day in Setup › Sensors. Each day keeps its own rate. Net cost on Today, the Energy page, reports and AI reviews includes it as its own line ("Standing charge £0.21 · £0.54/day"); a switch in Setup leaves it out of the headline.
- **API:** the energy summary gains `standingChargeGbp`, `standingChargePencePerDay`, `standingChargeSource`, `standingChargeAssumed`, `standingChargeIncluded` and `netCostWithStandingChargeGbp` (`netCostGbp` stays energy only, so trials compare like with like). Plan slots gain `gridImportActual`/`gridExportActual` and history slots `gridImport`/`gridExport`. New `GET`/`POST /api/telemetry/standing-charge`.
- **Meters found from Predbat:** Joule reads Predbat's `apps.yaml` (through MCP, Predbat's web interface or a mounted copy) and its entity list, and maps every Home Assistant meter it is sure of by itself, rechecking every few hours. Setup shows "Found automatically from Predbat" with **Change**, asks only when there is a real choice (one sensor per inverter, close name matches), and **Not mapped** keeps a meter unmapped for good. `HomeAssistant__Entities__*` settings are now optional overrides and always win. A `metric_standing_charge` given as a number in `apps.yaml` is used as the standing charge when there is no sensor and no figure of your own.
- **Notifications on your phone:** Setup › Notifications sends what needs you, new problems, checks that keep not finishing, things offline and an optional daily summary through ntfy, Pushover, Home Assistant (the companion app), Telegram, Discord or Slack, or your own JSON webhook. Each channel has its own events, quiet hours and a Test button; messages are limited per hour, combined when several are due, retried when a send fails and listed in a delivery log. Links open the item in Joule when `App__PublicUrl` is set.
- **Joule makes apps.yaml edits itself:** when Predbat's config folder is mounted read-write and edits are switched on (Setup › Files or `ConfigFiles__AllowEdits=true`), an `apps.yaml` suggestion has **Review and apply**. Joule shows the exact diff, keeps a copy of the file first, writes it in place (owner, permissions and ACLs kept), then watches Predbat restart and puts the previous file back by itself if Predbat logs a new error about the edit. **Restore previous version** stays available while Joule's edit is in the file, even after a later check has verified it. Without the mount nothing changes: copy the snippet and mark it applied as before.

### Changed

- **Joule names on disk.** The database is now `joule.duckdb` (it was `predbat.duckdb`) and the demo folder marker is `.joule-demo` (it was `.predbat-ai-demo`). The first start renames the old files in place, write-ahead log included, and logs one line saying so. Nothing is deleted: if the rename can't happen, Joule keeps using the old file under its old name and tries again at the next start. Going back to 1.0.0 afterwards would start with an empty database, so take a backup first if you might.
- **Dependencies.** Node.js 24 (the current LTS) builds the dashboard; Vite 8, Vitest 5, ESLint 10, TypeScript 6, lucide-react 1, DuckDB.NET 1.5.6. This clears every open Dependabot alert (tinypool, vitest and @vitest/mocker, all development-only).
- **Predbat MCP sign-in:** Joule exchanges the MCP secret for an access token at Predbat's `/oauth/token` and renews it before it expires, so Predbat no longer logs "MCP: Token … failed: Not enough segments" or "Authenticated via legacy bearer token" every few minutes. Older Predbat versions without the token endpoint still get the secret as before.
- **Closing things:** every suggestion, to-do, file edit and finding now has **Not needed** and **Dismiss** (with an optional reason) beside its main action, on Today, Insights, Suggestions and the check itself, with an **Undo** toast and a **Show closed** link to bring things back. Closing the last open item from a check closes the check too. When you reply "nah, not needed" and the AI agrees, the item closes and the reply says so; a question or "I'll do it later" keeps it open. Closed items are not raised again for 30 days.
- **Joule's own traffic:** Predbat log lines caused by Joule's own sign-in (such as "Not enough segments" or "legacy bearer token") are no longer reported as problems, and open findings about them are closed once on upgrade.

### Fixes

- **The notifications bell can be cleared.** Its count was worked out afresh on every page load with nothing remembered on the server, so "Needs you" always counted as new. The bell is now an inbox kept by Joule: open an item to mark it read, dismiss it, or mark all as read and clear all, and it stays that way across browsers and restarts. Anything you handle elsewhere drops out of the count at once.
- **Battery ring:** the short tick beside the ring (it marked the reserve) and the round cap at 12 o'clock are gone. The ring shows only the level; "Reserve 4%" is written under it and turns amber when the battery is close to it.

### Upgrading

- **Old `predbat-ai-data` volume?** Your data keeps working where it is. To move it into a volume with Joule's name, see [Moving to the joule-data volume](docs/configuration.md#moving-to-the-joule-data-volume).

## 1.0.0 (2026-10-07)

The first public release, under the Joule name.

### Features

- **Today:** what the battery is doing now and next, how today compares with yesterday, and anything that needs you.
- **Plan:** every half-hour Predbat planned beside what your meters measured (house load, solar, grid, battery, EV), with Predbat's own reasons in plain English, session prices, and earlier plans to browse.
- **Insights:** AI reviews that read Predbat's plan, settings and logs plus your measured data, explain what happened and why, and suggest exact changes with Predbat's documentation cited. Ask your own questions, run reviews on a schedule, and reply to a suggestion to have it reconsidered.
- **Changes you approve:** Monitor, Recommend and Auto modes; approve once or allow future changes within limits; one-click undo; a full history of every setting change, including changes made outside Joule. File edits (such as `apps.yaml`) come as a reviewable diff you apply yourself.
- **Trials:** each change is judged on equal before-and-after periods for forecast accuracy and measured net cost, with checks for anything that would muddy the comparison.
- **Energy:** measured daily and weekly figures from your Home Assistant meters, with gaps, resets and coverage shown honestly, and daily and weekly reports.
- **Household notes:** tell Joule once about your heat pump, EV or rules, and every review takes them into account.
- **Configuration file snapshots:** exact-byte versions of your Predbat files with diffs and hash-checked restores.
- **AI providers:** any OpenAI-compatible API (with token and cost tracking against prices you set), or your ChatGPT plan via Sign in with ChatGPT. Optional Predbat MCP access gives the AI Predbat's logs and entity history through an allow-list of read-only tools.
- **Demo mode:** a scripted house and scripted AI, so you can explore every page without connecting anything. It is what a fresh `docker compose up` shows.
- **Setup:** connect your Predbat from the browser (find it, test it, go live with a generated access key), then a checklist that finds your Home Assistant meters from Predbat's own `apps.yaml`, adds the MCP secret or an AI key, and allows changes. Choices are saved in Joule's data volume and applied with an automatic restart; environment variables still work and always win.
- **Security:** access-key sign-in with a remembered `HttpOnly`, `SameSite=Strict` cookie, slowed-down guessing, same-origin and request-header checks on every API call, strict browser security headers, and secrets masked before anything is stored or sent.
- **Operations:** a multi-architecture image (`linux/amd64`, `linux/arm64`), `GET /api/health` with a Docker health check, compressed and cacheable API responses, and a small polled state header with paged history.

### Upgrading from predbat-ai

Joule was developed privately as "predbat-ai". If you ran that:

- **Your data is kept.** The data directory layout and database file names are unchanged. A compose file whose service is still called `predbat-ai` keeps working; only the image changed. If your proxy pointed at `http://predbat-ai:5080` and you rename the service to `joule`, point it at `http://joule:5080`.
- **The example `compose.yaml` names its volume `joule-data`.** If you used the old example file (volume `predbat-ai-data`), keep your data by declaring the old volume in your own compose file: `volumes: { joule-data: { external: true, name: <project>_predbat-ai-data } }` (`docker volume ls` shows the exact name).
- **The image no longer sets `App__Demo=true`.** Joule still starts in demo mode when `App__Demo` is unset; leaving it unset lets Setup switch to live. Installs that set `App__Demo=false` are unaffected.
- **The dashboard sends `X-Joule-Request: 1`.** Joule still accepts the old `X-PredbatAI-Request: 1` header, so scripts and open browser tabs keep working.
- **An access key typed before the upgrade is still read** from the browser session, and new ones are saved under the Joule name.
- **Environment variables are unchanged** (`App__*`, `Predbat__*`, `HomeAssistant__*`, `Ai__*`, `ConfigFiles__*`).
- **Access keys must be at least 16 characters.** An install in `App__AuthMode=AccessKey` with a shorter `App__AccessKey` stops at startup with one line explaining the fix and exit code 2. Make a new key with `openssl rand -base64 24`.
- **Joule can no longer be shown inside a frame by default.** If you show it in a Home Assistant panel or another page, set `App__FrameAncestors` to that site, for example `App__FrameAncestors=https://homeassistant.example.com`.
- **Behind an HTTPS proxy, set `App__SecureCookie=true`** so the remembered sign-in cookie is only ever sent over HTTPS.
- Repeated wrong access keys are slowed down (`429` with `Retry-After`) after five different wrong keys in ten minutes.
- Old investigations, activities and AI usage move from the live state to an archive in the database.
