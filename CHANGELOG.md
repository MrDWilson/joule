# Changelog

All notable changes to Joule are listed here. Versions follow [semantic versioning](https://semver.org/).

## Unreleased

### Changed

- **Closing things:** every suggestion, to-do, file edit and finding now has **Not needed** and **Dismiss** (with an optional reason) beside its main action, on Today, Insights, Suggestions and the check itself, with an **Undo** toast and a **Show closed** link to bring things back. Closing the last open item from a check closes the check too. When you reply "nah, not needed" and the AI agrees, the item closes and the reply says so; a question or "I'll do it later" keeps it open. Closed items are not raised again for 30 days.
- **Joule's own traffic:** Predbat log lines caused by Joule's own sign-in (such as "Not enough segments" or "legacy bearer token") are no longer reported as problems, and open findings about them are closed once on upgrade.

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
