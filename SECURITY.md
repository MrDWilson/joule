# Security policy

Joule can read your energy data, your Predbat settings and logs, and, if you allow it, change Predbat settings. Security reports are taken seriously.

## Supported versions

Security fixes go into the latest release. Please upgrade to it before reporting a problem.

| Version | Supported |
| --- | --- |
| 1.0.x | Yes |
| Older | No |

## Reporting a vulnerability

Please **do not open a public issue** for a security problem.

Report it privately through GitHub's [private vulnerability reporting](https://github.com/MrDWilson/joule/security/advisories/new) (the repository's **Security** tab, then **Report a vulnerability**). Include:

- the Joule version (shown in the dashboard footer and at `/api/health`) and how you run it (Docker image, from source);
- your `App__AuthMode` and whether Joule sits behind a reverse proxy;
- steps to reproduce, and what an attacker could achieve.

Never include real access keys, AI provider keys, ChatGPT credentials, Home Assistant tokens or Predbat MCP secrets in a report. Redact them, along with meter numbers (MPANs and serials) and private hostnames.

You should get an acknowledgement within a week. Once a fix is released, the advisory is published with credit to you unless you prefer otherwise.

## Scope

In scope: the Joule API and dashboard in this repository, the published container image, and the scripts under `scripts/`.

Out of scope: Predbat, Home Assistant, your AI provider and your reverse proxy themselves (report those to their own projects), and setups that ignore the documented requirements, such as publishing `App__AuthMode=None` to the internet without a sign-in proxy in front.

## How Joule protects you

[docs/security.md](docs/security.md) describes the sign-in modes, request checks, browser headers and exactly what Joule can change.
