# Security

Joule can read your energy data, your Predbat settings and logs, and (if you allow it) change Predbat settings. Treat access to it like access to Predbat itself.

## Sign-in

### Access key (default)

With `App__AuthMode=AccessKey`, the dashboard asks for the key in `App__AccessKey` (at least 16 characters; `openssl rand -base64 24` makes one). After a correct key the browser gets a sign-in cookie, so new tabs and a phone home-screen shortcut do not ask again. The cookie is `HttpOnly` (page scripts cannot read it), `SameSite=Strict` (other sites cannot send it), and lasts `App__SessionDays` (30 by default). It is signed with a value derived from your key, so changing `App__AccessKey` signs every browser out. API clients can send the key in an `X-Access-Key` header instead.

After five different wrong keys from one address within ten minutes, Joule makes each further new guess wait, doubling up to five minutes. The response is `429` with a `Retry-After` header. A correct key clears the count. Repeating a key that already failed is not counted as a new guess, so a tab still polling with a typo, or with a key from before you changed `App__AccessKey`, gets a plain `401` and does not lock anyone else out.

The cookie is marked `Secure` when Joule itself serves HTTPS. Behind a proxy that terminates HTTPS, set `App__SecureCookie=true` so the browser never sends it over plain HTTP.

Demo mode works without a key. If you set one, it is enforced there too.

### Behind your own sign-in proxy

If a reverse proxy already handles sign-in (oauth2-proxy, Authelia, Authentik, Cloudflare Access and so on), you can turn Joule's own sign-in off with `App__AuthMode=None`. Joule then trusts every request that reaches it: there is no login, key check, identity header or token validation. Make sure the proxy covers every path, including `/api/*`, and that nothing else can reach port 5080. Joule logs a warning at startup when `None` mode listens on all interfaces, as it does in the container.

Any `App__AccessKey` is ignored in this mode. Route the proxy to `http://joule:5080` (the service name) on a shared Docker network; no host port needs publishing.

### Proxy requirements

- Use HTTPS between the browser and the proxy.
- Pass the browser's `Sec-Fetch-Site` header through unchanged. If a client does not send it, keep the public `Host` header so Joule's fallback `Origin` check works. Rewriting `Host` is fine when `Sec-Fetch-Site` is present.
- If the proxy compresses responses itself, that is fine; Joule already compresses its own.
- When the proxy's sign-in expires, background requests are redirected to the login page and fail. Reload the page to sign in again. For oauth2-proxy, make sure the redirect URL (`rd`) uses `https://`.

## Request integrity

Every `/api` request from another site is refused: Joule checks the browser's `Sec-Fetch-Site` header, falling back to `Origin` against `Host`. Requests that change something (anything other than `GET`/`HEAD`) and rely on ambient credentials (the sign-in cookie, or `None` mode) must also carry `X-Joule-Request: 1`. A web page on another site cannot add that header without a CORS preflight, which Joule never grants. The older `X-PredbatAI-Request: 1` header is still accepted. Clients that send `X-Access-Key` do not need it.

## Browser protections

Every response carries:

- `Content-Security-Policy`: scripts, styles, images, fonts and API calls only from Joule itself (inline styles are allowed for the charts), no plugins, and `frame-ancestors 'none'`.
- `X-Frame-Options: DENY`, so another site cannot frame the approval buttons and trick clicks. To show Joule in a Home Assistant panel, set `App__FrameAncestors=https://your-home-assistant`; the frame header is then replaced by that allow-list.
- `Permissions-Policy` turning off camera, microphone, location, payment, USB and similar features.
- `Referrer-Policy: no-referrer`, `X-Content-Type-Options: nosniff`, and same-origin opener and resource policies.

API responses are never stored by shared caches. The polled state endpoints use `Cache-Control: private, no-cache` with an `ETag`, so the browser revalidates every time and gets a tiny `304` when nothing changed.

## What Joule can change

- Nothing in Predbat until `Predbat__WritesEnabled=true` **and** you approve a change (or allow automatic changes for a specific low-risk setting in Auto mode).
- Before writing, Joule checks the current value is still the one you approved. After writing, it reads the value back. If it cannot confirm the result, it blocks further changes until you review and reconcile.
- The AI cannot grant permissions, change the mode or bypass these checks. Its suggestions are validated by the server, and settings without supporting Predbat documentation are rejected.
- Joule never runs Predbat MCP write tools. Only an allow-list of read tools is offered to the AI.
- Configuration file edits are never written by the AI. You get a diff to apply yourself; file restores are a separate, hash-checked action.

## What Joule stores and sends

All data stays in the `/data` volume: plans, settings history, measurements, reviews and AI credentials (owner-only `0600` files). Secrets and credential-looking values are masked before evidence is stored or sent to an AI provider, and in what the dashboard shows. The ChatGPT account email is shown masked. Logs and Home Assistant state can still contain personal household details, so only connect an AI provider you are happy to share them with. See [what leaves your network](../README.md#what-leaves-your-network).

## Reporting a problem

Please report security issues privately, as described in [SECURITY.md](../SECURITY.md), rather than in a public issue.
