# Configuration

Most installs need no configuration file at all. Start Joule, open **Setup** and use **Connect my Predbat**; the checklist then walks through the meters, the AI provider and the optional extras. Setup saves what you choose in `settings.json` in Joule's data volume and restarts Joule in a few seconds to apply it.

Everything can also be set with environment variables, either in a `.env` file next to `compose.yaml` or under `environment:` in your Compose file, and changes take effect when the container restarts (`docker compose up -d`). **An environment variable always wins**: Setup shows that value read-only and says which variable sets it. A blank line such as `Predbat__BaseUrl=` counts as unset. [`.env.example`](../.env.example) has the handful most installs might want; [`.env.full.example`](../.env.full.example) lists every option with its default.

Variable names use double underscores, for example `App__Demo`. When running from source you can export the same variables in your shell before `./scripts/dev.sh`.

### What Setup can save

| Setup step | Saved as | Notes |
| --- | --- | --- |
| Connect my Predbat | `App__Demo`, `Predbat__BaseUrl`, `App__AccessKey` | **Find Predbat** tries `http://predbat:5052`, `http://host.docker.internal:5052`, `http://homeassistant.local:5052` and a few more; **Test** asks the address for Predbat's state and names what answered. |
| Map your meters | `HomeAssistant__Entities__*` | **Find my meters** reads the sensors in Predbat's `apps.yaml` (`load_today`, `pv_today`, `import_today`, `export_today`, `soc_percent`, `car_charging_energy`, `metric_octopus_import`/`export`), through Predbat's MCP or a mounted copy, and matches the rest by name and unit. You confirm each one. |
| MCP | `Predbat__McpToken` | Predbat's `mcp_secret`. |
| AI provider | `Ai__ApiKey` | Or sign in with ChatGPT under AI checks. |
| Allow changes | `Predbat__WritesEnabled` | |

Secrets saved this way (the access key, MCP secret and API key) are kept in `settings.json`, readable only by Joule's user, like the ChatGPT credentials in `auth/`; they are never sent back to the browser. If you'd rather keep secrets out of the data volume, set them as environment variables instead.

A demo whose `App__Demo=true` comes from the environment can't be switched from Setup, so a public demo can't be taken over by its visitors. Before you go live, anyone who can open Joule can use Setup; the default `compose.yaml` only publishes Joule on `127.0.0.1` for that reason. Once live, every change needs the access key.

## Settings

### App

| Variable | Default | What it does |
| --- | --- | --- |
| `App__Demo` | (demo until Setup switches) | Unset: the demo house until Setup's "Connect my Predbat" switches to live. `true` pins the demo; `false` always watches your real Predbat. Demo and live data are stored separately. |
| `App__AuthMode` | `AccessKey` | `AccessKey`: the dashboard asks for a key. `None`: no sign-in, only for use behind your own sign-in proxy. See [security](security.md). |
| `App__AccessKey` | (none) | Required for live `AccessKey` mode; Setup makes one when you go live. At least 16 characters; `openssl rand -base64 24` makes a good one. |
| `App__SessionDays` | `30` | How long a browser stays signed in after entering the key. `0` asks every browser session. |
| `App__SecureCookie` | `auto` | Marks the sign-in cookie `Secure` (HTTPS only). `auto` does so when Joule itself serves HTTPS. Set `true` when a proxy in front of Joule terminates HTTPS, so the cookie is never sent over plain HTTP. |
| `App__FrameAncestors` | (nobody) | Sites allowed to show Joule inside a frame, for example `https://homeassistant.example.com` for a Home Assistant panel. |
| `App__DataDirectory` | `./data` | Where the database and credentials live when running from source. The container always uses `/data`. |

If a setting is wrong, Joule stops straight away with a single line saying what to fix, for example `Joule cannot start: App__AccessKey is too short.`, and exit code 2. Setup checks every value before saving it, so it can't save a setting that stops Joule starting; if `settings.json` itself is damaged, Joule says so and you can fix or move the file aside.

### Predbat

| Variable | Default | What it does |
| --- | --- | --- |
| `Predbat__BaseUrl` | (none) | Predbat's web interface, normally port 5052. Joule reads the plan and settings every five minutes. |
| `Predbat__AccessToken` | (none) | Only if your Predbat web interface needs a bearer token. |
| `Predbat__Prefix` | `predbat` | The entity prefix Predbat uses in Home Assistant. |
| `Predbat__WritesEnabled` | `false` | Allows Joule to change Predbat settings. Every change still needs your approval, or an explicit Auto-mode permission for that setting. |
| `Predbat__McpToken` | (none) | Predbat's `mcp_secret`. Lets the AI read Predbat's logs and entity history during a review. See [AI providers](ai-providers.md#predbat-mcp). |
| `Predbat__McpUrl` | Predbat host, port 8199, `/mcp` | Only if Predbat's MCP server is somewhere else. |
| `Predbat__McpTimeoutSeconds` | `20` | Time limit for each MCP call, 1 to 60 seconds. |
| `Predbat__DocumentationRef` | `v9.3.3` | The Predbat documentation release the AI cites. |

### AI

| Variable | Default | What it does |
| --- | --- | --- |
| `Ai__ApiKey` | (none) | An OpenAI-compatible API key. Choose the model and token prices in Joule's AI settings. |
| `Ai__ApiBaseUrl` | `https://api.openai.com/v1` | Another chat-completions-compatible endpoint. |

ChatGPT plan sign-in has no variables; see [AI providers](ai-providers.md#chatgpt-plan).

### Home Assistant

These settings map your energy meters so Joule can measure what really happened. [Measurements](measurements.md) explains which sensors to pick.

| Variable | Default | What it does |
| --- | --- | --- |
| `HomeAssistant__Entities__Load`, `Pv`, `GridImport`, `GridExport`, `BatteryCharge`, `BatteryDischarge`, `Ev` | (none) | Cumulative energy sensors (kWh, Wh or MWh), not power sensors (W). |
| `HomeAssistant__Entities__Soc` | (none) | Battery state of charge (%). |
| `HomeAssistant__Entities__ImportTariff`, `ExportTariff` | (none) | Current import and export rates. |
| `HomeAssistant__Entities__StandingCharge` | (found automatically) | Optional: the daily standing charge (GBP or p per day). With the Octopus Energy integration Joule finds it on its own from the import rate sensor (`sensor.octopus_energy_electricity_<serial>_<mpan>_current_standing_charge`). Without a sensor, enter the figure in Setup › Sensors. |
| `HomeAssistant__Entities__IntelligentSlots`, `AlternativeForecast` | (none) | Optional: Octopus Intelligent dispatch slots, and a second load forecast to compare. |
| `HomeAssistant__BaseUrl`, `HomeAssistant__AccessToken` | (none) | Optional. Without them, mapped sensors are read through Predbat's copy of Home Assistant state. With them, Joule reads Home Assistant directly and falls back to Predbat. |
| `HomeAssistant__TimeZone` | `Europe/London` | Your household's timezone. "Today", daily reports and AI budgets use it. |
| `HomeAssistant__PollMinutes` | `5` | How often meters are read. |
| `HomeAssistant__MaxGapMinutes` | `10` | Readings older than this count as stale. |
| `HomeAssistant__Units__<Name>` | (from the sensor) | Unit override for a sensor that does not declare one, for example `HomeAssistant__Units__Ev=kWh`. |

### Container

| Variable | Default | What it does |
| --- | --- | --- |
| `TZ` | `Europe/London` | Timezone for log timestamps. |
| `JOULE_IMAGE` | `ghcr.io/mrdwilson/joule:latest` | Which image `compose.yaml` runs. Pin a release such as `ghcr.io/mrdwilson/joule:1.0.0` if you prefer. |

## Networking

Joule's container has to reach Predbat (and Home Assistant, if you set `HomeAssistant__BaseUrl`).

- **Predbat in Docker on the same machine:** put both containers on one Docker network and use the container name, for example `http://predbat:5052`. The [README example](../README.md#alongside-an-existing-predbat-compose) joins an existing network.
- **Predbat or Home Assistant on the Docker host itself** (for example Home Assistant with `network_mode: host`): use `http://host.docker.internal:5052` (or `:8123` for Home Assistant). The default `compose.yaml` already maps that name; in your own Compose file, add:

  ```yaml
      extra_hosts:
        - "host.docker.internal:host-gateway"
  ```

- **Predbat as a Home Assistant add-on:** Joule can't run as an add-on, so run it with Docker on any machine on your network and use Home Assistant's IP address with Predbat's port, for example `http://192.168.1.20:5052`. In Home Assistant, open Predbat's add-on page, then **Configuration → Network**, and make sure the web interface port (5052) is set; set 8199 too if you use MCP. `homeassistant.local` usually doesn't resolve inside Docker, so prefer the IP address.
- **Another machine:** use its IP address, for example `http://192.168.1.20:5052`.

Inside a container, `127.0.0.1` means the container itself, never your server.

The default `compose.yaml` publishes port 5080 on `127.0.0.1` only, so Joule cannot be reached from other machines until you put a reverse proxy in front. That proxy should use HTTPS and pass the browser's `Sec-Fetch-*` headers through unchanged. It may rewrite `Host`. See [security](security.md).

## Health checks

`GET /api/health` needs no key and returns `{"status":"ok","version":"1.0.0","build":"…","started":"…"}` (`started` changes on every restart). The image has a Docker `HEALTHCHECK` that calls it, so `docker ps` shows `healthy`, and uptime monitors can poll the same URL.

## Configuration file snapshots

Joule can keep exact-byte versions of your Predbat configuration files (such as `apps.yaml`), show what changed, and restore an earlier version after a hash check. Mount the directory and list the files you want:

```yaml
services:
  joule:
    environment:
      ConfigFiles__Root: /predbat-config
      ConfigFiles__AllowedFiles__0: apps.yaml
    volumes:
      - /absolute/path/to/predbat/config:/predbat-config
```

The container runs as the `app` user, which needs read access (and write access if you want restores). Secrets files and symbolic links are never read. Leave `ConfigFiles__Root` empty to turn this off. A restore writes a new version; it does not reload Predbat, so Joule pauses automatic changes until you reload Predbat and confirm in Files.

## Data and backups

Everything lives in the `/data` volume: `demo/` and `live/` databases (DuckDB), `settings.json` from Setup, and `auth/` for AI provider credentials. Joule keeps the newest few hundred reviews and the last 500 activity entries in its working state and moves older history into the database, so the dashboard stays quick as history grows.

To back up, stop the container and copy the whole volume, including any DuckDB companion files. Backups contain your access key and AI credentials: encrypt them and keep them private. Restore with the container stopped and keep the files owned by the `app` user.

```sh
docker compose stop joule
docker run --rm -v joule_joule-data:/data -v "$PWD":/backup alpine tar czf /backup/joule-data.tgz -C /data .
docker compose start joule
```

(Compose adds your project name, usually the folder name, in front of the volume name; `docker volume ls` shows the real one.)
