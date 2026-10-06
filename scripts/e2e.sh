#!/usr/bin/env bash
set -euo pipefail
project_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$project_dir"
if command -v dotnet >/dev/null 2>&1; then dotnet_bin="$(command -v dotnet)"
elif [[ -x "$project_dir/.tools/dotnet/dotnet" ]]; then dotnet_bin="$project_dir/.tools/dotnet/dotnet"
else printf '%s\n' 'Install the .NET 10 SDK first.' >&2; exit 1; fi
export DOTNET_CLI_HOME="$project_dir/.tools/dotnet-home" NUGET_PACKAGES="$project_dir/.cache/nuget"
# E2E_WEB_PORT / E2E_API_PORT let parallel checkouts run the suite side by side.
web_port="${E2E_WEB_PORT:-5177}" api_port="${E2E_API_PORT:-5087}"
export E2E_BASE_URL="http://127.0.0.1:$web_port" JOULE_API_URL="http://127.0.0.1:$api_port" E2E_MANAGED=1
mkdir -p "$project_dir/.cache"
run_dir="$(mktemp -d "$project_dir/.cache/e2e.XXXXXX")"
api_pid=''
cleanup() {
  if [[ -n "$api_pid" ]]; then kill "$api_pid" 2>/dev/null || true; wait "$api_pid" 2>/dev/null || true; fi
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
"$dotnet_bin" build src/Joule.Api --no-restore -p:UseSharedCompilation=false
# E2E_BUILD_WEB=1 serves the production build from the API too, so production-headers.spec.ts can check the
# Content-Security-Policy, caching and compression the dev server does not apply.
if [[ "${E2E_BUILD_WEB:-0}" == 1 ]]; then
  (cd web && npm run build)
  rm -rf src/Joule.Api/wwwroot && cp -R web/dist src/Joule.Api/wwwroot
fi
# Each spec receives a fresh disposable demo. Seed recommendations cannot be
# accidentally consumed by an earlier spec, or mutate the user's normal preview.
specs=("$@")
if [[ ${#specs[@]} == 0 ]]; then specs=(web/e2e/*.spec.ts); fi
for spec in "${specs[@]}"; do
  name="$(basename "$spec" .spec.ts)"
  auth_mode=AccessKey access_key='' app_demo=true
  if [[ "$name" == auth ]]; then auth_mode=None; fi
  # session.spec.ts signs in with this key to check the remembered-sign-in cookie.
  if [[ "$name" == session ]]; then access_key='e2e-session-key-0123456789'; fi
  # first-run.spec.ts is the out-of-the-box container: demo by default, not pinned by App__Demo, so Setup can go live.
  if [[ "$name" == first-run ]]; then app_demo=''; fi
  App__Demo="$app_demo" App__AuthMode="$auth_mode" App__AccessKey="$access_key" App__DataDirectory="$run_dir/$name" ASPNETCORE_URLS="$JOULE_API_URL" \
    "$dotnet_bin" src/Joule.Api/bin/Debug/net10.0/Joule.Api.dll --contentRoot "$project_dir/src/Joule.Api" >"$run_dir/$name.log" 2>&1 &
  api_pid=$!
  ready=0
  for attempt in {1..100}; do
    if ! kill -0 "$api_pid" 2>/dev/null; then cat "$run_dir/$name.log" >&2; exit 1; fi
    if grep -q 'Now listening on:' "$run_dir/$name.log" && curl --silent --fail "$JOULE_API_URL/api/health" >/dev/null; then ready=1; break; fi
    sleep .2
  done
  if [[ "$ready" != 1 ]]; then cat "$run_dir/$name.log" >&2; exit 1; fi
  (cd web && npx playwright test "e2e/$name.spec.ts")
  cleanup; api_pid=''
done
printf 'Browser fixtures and logs: %s\n' "$run_dir"
