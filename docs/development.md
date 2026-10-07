# Development

Joule is an ASP.NET Core 10 API with DuckDB storage (`src/Joule.Api`) and a React, TypeScript and Recharts dashboard built with Vite (`web/`).

## Run from source

Install Node.js 24 (the current LTS; 22.12 or later also works) and the .NET 10 SDK, then:

```sh
./scripts/dev.sh
```

The dashboard is at <http://127.0.0.1:5173> and the API at <http://127.0.0.1:5080>, in demo mode with data under `./data`. Use Setup's **Connect my Predbat** to point it at a real Predbat (saved in `./data/settings.json`), or export settings such as `App__Demo=false` first. To sign in to ChatGPT from source, use `./scripts/chatgpt-signin.sh`, which serves the built dashboard on port 5080 where the OAuth callback lands.

## Tests

```sh
./scripts/test.sh                 # .NET tests, then the dashboard type check, build, unit tests, lint and formatting
RUN_E2E=1 ./scripts/test.sh       # also the Playwright browser tests
```

Browser tests need a browser once: `cd web && npx playwright install chromium`. `./scripts/e2e.sh [web/e2e/x.spec.ts …]` runs specs, each against a fresh demo API. Set `E2E_WEB_PORT` and `E2E_API_PORT` to run several checkouts side by side, `PLAYWRIGHT_CHANNEL=chrome` to use installed Chrome, and `E2E_BUILD_WEB=1` to serve the production build from the API too, which `production-headers.spec.ts` needs to check the Content-Security-Policy, caching and compression. Fixture data and logs stay under `.cache/e2e.*`.

## API

The dashboard polls `GET /api/state?view=header`, a small summary with an `ETag`; an unchanged poll costs a `304`. Larger data is paged: `/api/investigations?cursor=&limit=`, `/api/investigations/{id}`, `/api/activities?since=` or `?before=`, `/api/usage?days=`, `/api/settings`, `/api/plans`. `GET /api/state` without `view` still returns the full legacy payload for older pages. The header's shape is published as TypeScript in `web/src/types.ts` and as JSON Schema in [`docs/api/state-header.schema.json`](api/state-header.schema.json); `StateHeaderContractTests` and `web/e2e/api-contract.spec.ts` keep them in step with the server. Add fields; never rename or remove one.

The live state keeps the newest 100 investigations plus everything from the last 8 days (and anything still open or linked to a proposal), the newest 500 activities and 35 days of AI usage. Older items move to `archived_*` tables in DuckDB; the paged endpoints above read both. Code that searches the in-memory state does not see the archive: replying to an old, closed investigation returns `404`, and a custom report period longer than 8 days lists only the investigations still in memory.

Clients that change something send `X-Joule-Request: 1` (the older `X-PredbatAI-Request: 1` still works) and, in access-key mode, `X-Access-Key`. Unknown `/api` paths return `404` JSON.

## Screenshots

```sh
node scripts/docs-screenshots.mjs
```

builds the dashboard, starts a throwaway demo on port 5090, and saves desktop (1440 px) and phone (390 px) screenshots of each page to `docs/screenshots/`. Regenerate them for each release. The demo house has scripted meter outages (UTC 03:00–03:30 and 10:00–11:30), so the script refuses to run then unless you pass `--allow-outage`.

## Releases

CI (`.github/workflows/ci.yml`) runs the .NET tests, the dashboard build, the browser tests and an image build for both architectures (not pushed) on every pull request and push to `master`. Pushing a tag such as `v1.0.0` runs `.github/workflows/release.yml`: the tests again, then a multi-architecture image (`linux/amd64`, `linux/arm64`) pushed to `ghcr.io/<owner>/joule` as `1.0.0`, `1.0`, `1` and `latest`, and a GitHub release with generated notes. The version shown in `/api/health` and the dashboard comes from the tag.
