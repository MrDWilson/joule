#!/usr/bin/env bash
set -euo pipefail
project_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$project_dir"
if command -v dotnet >/dev/null 2>&1; then
  dotnet_bin="$(command -v dotnet)"
elif [[ -x "$project_dir/.tools/dotnet/dotnet" ]]; then
  dotnet_bin="$project_dir/.tools/dotnet/dotnet"
else
  printf '%s\n' 'Install the .NET 10 SDK or provide .tools/dotnet/dotnet.' >&2
  exit 1
fi
command -v npm >/dev/null 2>&1 || { printf '%s\n' 'Install Node.js 24 with npm.' >&2; exit 1; }
export DOTNET_CLI_HOME="$project_dir/.tools/dotnet-home"
export NUGET_PACKAGES="$project_dir/.cache/nuget"
# No App__Demo default: Joule starts in demo mode on its own, and Setup can then switch it to live.
export App__DataDirectory="${App__DataDirectory:-$project_dir/data}"
export ASPNETCORE_URLS=http://127.0.0.1:5080
mkdir -p "$DOTNET_CLI_HOME" "$NUGET_PACKAGES"
"$dotnet_bin" build src/Joule.Api/Joule.Api.csproj -p:UseSharedCompilation=false
(cd web && npm ci)
api_pid=''
web_pid=''
cleanup() {
  trap - EXIT INT TERM
  [[ -z "$web_pid" ]] || kill "$web_pid" 2>/dev/null || true
  [[ -z "$api_pid" ]] || kill "$api_pid" 2>/dev/null || true
  [[ -z "$web_pid" ]] || wait "$web_pid" 2>/dev/null || true
  [[ -z "$api_pid" ]] || wait "$api_pid" 2>/dev/null || true
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
"$dotnet_bin" src/Joule.Api/bin/Debug/net10.0/Joule.Api.dll --contentRoot "$project_dir/src/Joule.Api" &
api_pid=$!
(cd web && exec node node_modules/vite/bin/vite.js --host 127.0.0.1 --port 5173 --strictPort) &
web_pid=$!
printf '%s\n' 'Joule: http://127.0.0.1:5173 (API on port 5080). To sign in to ChatGPT, use ./scripts/chatgpt-signin.sh instead. Press Ctrl+C to stop.'
while kill -0 "$api_pid" 2>/dev/null && kill -0 "$web_pid" 2>/dev/null; do sleep 1; done
printf '%s\n' 'A development server stopped. Check its output above.' >&2
exit 1
