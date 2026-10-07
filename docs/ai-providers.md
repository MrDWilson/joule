# AI providers

Joule's reviews and replies need an AI model. Choose the provider, model, review schedule, daily run limit and token prices in Joule's AI settings. There is no automatic fallback from one provider to another: switching to paid API use is always your choice.

| Provider | Needs | Cost |
| --- | --- | --- |
| **Demo** | Nothing | Free. Scripted results, demo mode only. |
| **API** | `Ai__ApiKey` | Billed per token by the provider. Joule estimates the cost from recorded token counts and the prices you enter; the estimate is not an invoice. |
| **ChatGPT plan** | A one-time sign-in on your own computer | Uses your ChatGPT plan's allowance. Joule records token counts but cannot see your remaining allowance or a dollar cost. |

## Token use and costs

A review is a conversation: the model reads a summary, then asks for the evidence it needs (plans, measurements, logs, documentation) until it can answer. Most tokens are therefore input tokens, and repeated context is often served from the provider's prompt cache. The cost of a review depends mostly on the model and how much evidence it asks for, so it varies from house to house and question to question.

- Every run records its input, cached-input and output tokens in the AI usage card, with an estimated cost when you have entered prices.
- The daily run limit (12 by default, 1 to 96) covers scheduled reviews and the questions you ask. When it is used up, reviews wait until midnight in your household's timezone.
- Scheduled reviews are off until you turn them on, and the interval can be 15 minutes to a day.
- Start with a smaller model and a low limit, watch the usage card for a few days, then adjust.

## API key

Paste the key in Setup (**Choose an AI provider → Add an API key**) or set `Ai__ApiKey` (and `Ai__ApiBaseUrl` for an endpoint other than OpenAI's, as long as it speaks the chat-completions API). Restart, open the AI settings, choose **API**, type a model your endpoint supports, and enter its input and output prices per million tokens so Joule can estimate costs. Without prices the cost shows as unknown. Energy figures are in GBP and AI costs in USD; Joule never adds them together into a "net saving".

## ChatGPT plan

Joule uses OpenAI's [Sign in with ChatGPT open-source flow](https://developers.openai.com/siwc/token-sharing-open-source). This is a preview: not every account can use it, and having a ChatGPT subscription does not guarantee access. Requests go to the Responses API with `store: false`.

The sign-in must finish at exactly `http://127.0.0.1:5080/auth/callback` on the computer whose browser you sign in with. That is an OpenAI requirement, and it means a Joule running in Docker or on another server cannot complete the sign-in itself. Instead:

1. **Start the server's Joule once** so it creates its host ID (`/data/auth/chatgpt-host.json`), then leave it running.
2. **On your own computer**, in a checkout of this repository (Node.js 24 and the .NET 10 SDK needed), run:

   ```sh
   ./scripts/chatgpt-signin.sh
   ```

   It builds the dashboard, starts a private local Joule on <http://127.0.0.1:5080> and opens it. Go to the AI settings, choose **ChatGPT**, select **Continue with ChatGPT** and approve. Then press Ctrl+C. The script prints where it saved `chatgpt-credentials.json`.
3. **Copy that one file to the server** over SSH, for example `scp …/chatgpt-credentials.json server:joule-chatgpt-credentials.json`.
4. **On the server, in your Compose folder**, install it with the right owner and permissions, keeping the server's own host ID:

   ```sh
   docker compose stop joule
   docker compose run --rm --no-deps --user root --entrypoint sh \
     -v "$HOME/joule-chatgpt-credentials.json:/run/chatgpt-import.json:ro" \
     joule -c 'set -eu; test -f /data/auth/chatgpt-host.json; install -o app -g app -m 600 /run/chatgpt-import.json /data/auth/chatgpt-credentials.json'
   rm "$HOME/joule-chatgpt-credentials.json"
   docker compose up -d joule
   ```

5. Open the AI settings on the server's Joule, choose **ChatGPT** and load the available models.

Do not use the copied session from your computer afterwards, and do not press Disconnect there: the server now owns the session and refreshes it, and disconnecting the original may revoke it for both. If a native Joule on the same computer is your only install, simply open <http://127.0.0.1:5080> and sign in directly. OpenAI's [self-hosted VM guide](https://developers.openai.com/siwc/token-sharing-open-source/self-hosted-vms) describes the same procedure.

ChatGPT plan limits can be shared with other apps. Manage access and usage in [ChatGPT Settings → Usage](https://chatgpt.com/#settings/Usage). See OpenAI's notes on [accounts and usage](https://developers.openai.com/siwc/token-sharing-open-source/profiles-and-sessions) and [preview limitations](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations).

### Stored credentials

Under the data directory's `auth/` folder (`/data/auth` in Docker):

- `chatgpt-host.json`: this install's host ID, created at startup. Keep it when moving credentials.
- `chatgpt-credentials.json`: the access, refresh and ID tokens.
- `chatgpt-registration.json`: registration details kept after a disconnect so you can sign in again.

Files are written atomically and readable only by their owner (`0600`). This storage works on Linux and macOS. Never commit these files, paste tokens into issues, or share sign-in URLs. Disconnect in Joule tries to revoke the session with OpenAI and deletes the local tokens; if it cannot confirm revocation, remove the app in ChatGPT's settings.

## What the AI sees

For each review the AI gets a summary of Predbat's current settings, then asks for what it needs: plans, measured energy, earlier reviews, your saved notes about the house, Predbat's documentation and (with MCP) Predbat's logs. Every request and answer is saved with the review so you can see exactly what it looked at. Secrets and credential-like values are masked first.

The AI never changes anything directly. A suggested setting change must cite Predbat's documentation (Joule checks the citation) and goes through your approval like any other change.

## Predbat MCP

MCP lets the AI read Predbat's live logs, status, plan, settings, masked `apps.yaml` and Home Assistant entity history during a review. Joule only offers a fixed list of read tools; Predbat's write tools are never exposed, and `apps.yaml` is always read masked.

1. In Predbat's `apps.yaml`, enable the MCP server and choose a secret, then restart Predbat:

   ```yaml
   mcp_enable: true
   mcp_secret: "choose-a-long-random-secret"
   ```

2. Give Joule the same secret in Setup (**Let the AI read Predbat's logs**), or with `Predbat__McpToken`. The address defaults to Predbat's host on port 8199 at `/mcp`; set `Predbat__McpUrl` if yours differs. Keep port 8199 on your private Docker network.
3. In Joule's AI settings, use **Check MCP connection**. It lists the read tools your Predbat offers.

Predbat's `search_entities`, `get_entity_state` and `get_entity_history` tools also need `switch.predbat_ai_ha_state_enable` turned on in Home Assistant. That switch lets Predbat read Home Assistant for the AI; it does not allow changes.

If MCP is off or fails, reviews still run from Joule's stored history and record the gap. The MCP token only authenticates Joule to Predbat; it does not add sign-in to Joule.

### Compatibility notes

Joule's MCP client targets the JSON-over-HTTP MCP server in Predbat v9.3.x (protocol `2024-11-05`, POST to `/mcp`). That server answers the `notifications/initialized` message with an ordinary JSON response, has no sessions or streaming, and wraps tool results as JSON text, where failures may appear as `success: false` without `isError`. Joule's adapter tolerates all of this, limits response sizes and time, refuses redirects, and only calls these read tools:

`get_plan`, `get_status`, `get_apps` (always `masked: true`), `get_apps_config`, `get_state`, `get_log`, `get_config`, `get_entities`, `search_entities`, `get_entity_state`, `get_entity_history`.

Predbat's MCP server accepts its secret as a bearer token and does not enforce read-only scopes per tool, so this allow-list is enforced on Joule's side. Tool availability comes from your installed Predbat, not from the newest documentation. See Predbat's [MCP documentation](https://github.com/springfall2008/batpred/blob/main/docs/components.md#mcp-server-mcp).
