#!/usr/bin/env sh
# Copies the canonical Predbat state glossary into the web app. The backend test
# PlanGlossaryTests.WebMirrorIsByteIdentical fails until this has been run after an edit.
set -eu
root="$(cd "$(dirname "$0")/.." && pwd)"
cp "$root/src/Joule.Api/Knowledge/predbat-states.json" "$root/web/src/lib/predbat-states.json"
printf '%s\n' "Copied predbat-states.json to web/src/lib/."
