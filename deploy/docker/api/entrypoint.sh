#!/usr/bin/env bash
set -Eeuo pipefail

readonly seed_root="/opt/hsjm/wwwroot.seed"
readonly web_seed_root="/opt/hsjm/web.seed"
readonly runtime_root="/app/wwwroot"

mkdir -p \
  "$runtime_root" \
  /app/logs \
  /app/Uploads \
  /app/ParserTemplates \
  /app/GeneratedParsers

if [[ -d "$seed_root" ]]; then
  cp -a --update=none "$seed_root"/. "$runtime_root"/
fi

# Frontend files are immutable build artifacts. Refresh them on every start so
# image upgrades cannot be hidden by an existing wwwroot volume. Runtime Skills
# remain protected because they live only in seed_root and use no-clobber copy.
if [[ -d "$web_seed_root" ]]; then
  cp -a "$web_seed_root"/. "$runtime_root"/
fi

exec dotnet /app/HSJM.AI.dll
