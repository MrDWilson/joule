## What and why

<!-- What does this change, and what problem does it solve? Link the issue: Fixes #123 -->

## How it was tested

<!-- Tests added or updated, and anything you checked by hand (demo mode, phone width, ...). -->

## Checklist

- [ ] `./scripts/test.sh` passes (.NET tests, dashboard build, unit tests, lint, formatting)
- [ ] Browser tests pass for the pages I touched (`./scripts/e2e.sh web/e2e/<spec>.spec.ts`)
- [ ] Docs and `CHANGELOG.md` updated if behaviour or configuration changed
- [ ] Screenshots regenerated (`node scripts/docs-screenshots.mjs`) if the look of a page changed
- [ ] No secrets, meter numbers (MPANs, serials) or private hostnames in code, fixtures, logs or screenshots
- [ ] Nothing writes to Predbat without `Predbat__WritesEnabled=true` and an approval
