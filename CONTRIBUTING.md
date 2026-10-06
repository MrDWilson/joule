# Contributing to Joule

Thanks for helping. Bug reports, documentation fixes and pull requests are all welcome.

## Before you start

- **Questions and ideas:** open an issue with the feature template so the approach can be agreed before you write a lot of code.
- **Bugs:** use the bug template. Demo mode (`App__Demo=true`) reproduces most dashboard problems without sharing your data.
- **Security problems:** please follow [SECURITY.md](SECURITY.md) instead of opening a public issue.
- **Predbat itself:** if the problem is in Predbat's plan or settings rather than in Joule, the [Predbat project](https://github.com/springfall2008/batpred) is the right place. Joule is independent of Predbat.

Never paste access keys, API keys, ChatGPT credentials, Home Assistant tokens, MCP secrets, meter numbers (MPANs, serials) or private hostnames into issues, logs or screenshots.

## Development setup

You need Node.js 22 and the .NET 10 SDK.

```sh
cd web && npm ci && cd ..
./scripts/dev.sh
```

The dashboard runs at <http://127.0.0.1:5173> against a demo API on port 5080. [docs/development.md](docs/development.md) covers the layout, the API and releases.

| Path | What lives there |
| --- | --- |
| `src/Joule.Api` | ASP.NET Core API, DuckDB storage, Predbat/Home Assistant/AI integrations, demo house |
| `tests/Joule.Tests` | xUnit tests for the API |
| `web/` | React + TypeScript dashboard (Vite), unit tests (`src/**/*.test.ts`) and Playwright specs (`e2e/`) |
| `scripts/` | dev server, test runners, screenshot and knowledge-index helpers |
| `docs/` | user documentation and screenshots |

## Checks

Run these before opening a pull request; CI runs the same:

```sh
./scripts/test.sh                       # .NET tests, then the dashboard type check and build
cd web && npm run test:unit && npm run lint && npm run format:check
RUN_E2E=1 ./scripts/test.sh             # adds the Playwright browser tests (npx playwright install chromium once)
```

`./scripts/e2e.sh web/e2e/some.spec.ts` runs a single browser spec against a fresh demo.

## Pull requests

- Keep each pull request to one change, with tests for new behaviour and bug fixes.
- Write user-facing text in plain British English, the way the rest of the dashboard reads.
- Update the docs and [CHANGELOG.md](CHANGELOG.md) (under an "Unreleased" heading) when behaviour or configuration changes.
- Keep API changes additive: add fields to `/api/state?view=header`, never rename or remove them (see [development](docs/development.md#api)).
- Joule's safety rules are not negotiable in a pull request: nothing writes to Predbat without `Predbat__WritesEnabled=true` and an approval, Predbat MCP write tools are never exposed to the AI, and secrets are masked before they are stored or sent anywhere.
- If you change the look of a page, regenerate the screenshots with `node scripts/docs-screenshots.mjs`.

Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/) (`fix:`, `feat:`, `docs:`, `test:`, `refactor:`, `chore:`).

By contributing you agree that your work is released under the [MIT licence](LICENSE) and that you follow the [code of conduct](CODE_OF_CONDUCT.md).
