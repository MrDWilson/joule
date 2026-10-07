#!/usr/bin/env bash
# Sign in to ChatGPT for Joule on this computer.
#
# OpenAI's open-source sign-in flow only returns to http://127.0.0.1:5080/auth/callback on the browser's own computer,
# so a Joule in Docker or on a server cannot finish it. This script builds the dashboard, serves it from a private,
# local Joule on 127.0.0.1:5080 and opens it in your browser. Sign in under the AI settings, press Ctrl+C, then copy
# the credentials file it prints to your server (see docs/ai-providers.md).
#
# Usage: ./scripts/chatgpt-signin.sh [data-directory]   (default: ./data/chatgpt-signin; JOULE_NO_BROWSER=1 skips opening it)
set -euo pipefail
project_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$project_dir"
if command -v dotnet >/dev/null 2>&1; then dotnet_bin="$(command -v dotnet)"
elif [[ -x "$project_dir/.tools/dotnet/dotnet" ]]; then dotnet_bin="$project_dir/.tools/dotnet/dotnet"
else printf '%s\n' 'Install the .NET 10 SDK first: https://dotnet.microsoft.com/download' >&2; exit 1; fi
command -v npm >/dev/null 2>&1 || { printf '%s\n' 'Install Node.js 24 with npm first.' >&2; exit 1; }
if command -v lsof >/dev/null 2>&1 && lsof -nP -iTCP:5080 -sTCP:LISTEN >/dev/null 2>&1; then
  printf '%s\n' 'Port 5080 is in use. Stop whatever is listening there (another Joule?) and try again; the ChatGPT callback must use 5080.' >&2
  exit 1
fi
data_dir="$(mkdir -p "${1:-$project_dir/data/chatgpt-signin}" && cd "${1:-$project_dir/data/chatgpt-signin}" && pwd)"
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$project_dir/.tools/dotnet-home}" NUGET_PACKAGES="${NUGET_PACKAGES:-$project_dir/.cache/nuget}"
mkdir -p "$DOTNET_CLI_HOME" "$NUGET_PACKAGES"

printf '%s\n' 'Building Joule…'
(cd web && { [[ -d node_modules ]] || npm ci; } && npm run build >/dev/null)
rm -rf src/Joule.Api/wwwroot && cp -R web/dist src/Joule.Api/wwwroot
"$dotnet_bin" build src/Joule.Api/Joule.Api.csproj -p:UseSharedCompilation=false -v quiet -nologo

# Demo data keeps this instance self-contained; only the auth/ folder matters. Loopback only, so no app sign-in.
App__Demo=true App__AuthMode=None App__AccessKey='' App__DataDirectory="$data_dir" ASPNETCORE_URLS=http://127.0.0.1:5080 \
  "$dotnet_bin" src/Joule.Api/bin/Debug/net10.0/Joule.Api.dll --contentRoot "$project_dir/src/Joule.Api" &
api_pid=$!
finish() {
  trap - EXIT INT TERM
  kill "$api_pid" 2>/dev/null || true; wait "$api_pid" 2>/dev/null || true
  credentials="$data_dir/auth/chatgpt-credentials.json"
  printf '\n'
  if [[ -f "$credentials" ]]; then
    printf 'Signed in. Your ChatGPT credentials are in:\n  %s\n\n' "$credentials"
    printf 'Copy that file to your server, then install it as described in docs/ai-providers.md, for example:\n'
    printf '  scp "%s" your-server:joule-chatgpt-credentials.json\n' "$credentials"
    printf 'Afterwards, do not use or disconnect this local copy: the server owns the session from then on.\n'
  else
    printf 'No ChatGPT credentials were saved. Run this again and choose "Continue with ChatGPT" in the AI settings.\n'
  fi
}
trap finish EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
for _ in $(seq 1 60); do
  if curl --silent --fail http://127.0.0.1:5080/api/health >/dev/null 2>&1; then break; fi
  kill -0 "$api_pid" 2>/dev/null || { printf '%s\n' 'Joule stopped while starting. See the output above.' >&2; exit 1; }
  sleep .5
done
url=http://127.0.0.1:5080
printf '\nJoule is running at %s\n' "$url"
printf 'Open the AI settings, choose ChatGPT, select "Continue with ChatGPT" and approve. Then press Ctrl+C here.\n'
if [[ -n "${JOULE_NO_BROWSER:-}" ]]; then :
elif command -v open >/dev/null 2>&1; then open "$url"
elif command -v xdg-open >/dev/null 2>&1; then xdg-open "$url" >/dev/null 2>&1 || true
fi
wait "$api_pid"
