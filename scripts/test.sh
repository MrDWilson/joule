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
command -v npm >/dev/null 2>&1 || { printf '%s\n' 'Install Node.js 22 with npm.' >&2; exit 1; }
export DOTNET_CLI_HOME="$project_dir/.tools/dotnet-home"
export NUGET_PACKAGES="$project_dir/.cache/nuget"
mkdir -p "$DOTNET_CLI_HOME" "$NUGET_PACKAGES"
"$dotnet_bin" test tests/Joule.Tests/Joule.Tests.csproj -p:UseSharedCompilation=false
(cd web && npm ci && npm run build && npm run test:unit && npm run lint && npm run format:check)
if [[ "${RUN_E2E:-0}" == 1 ]]; then
  ./scripts/e2e.sh
fi
