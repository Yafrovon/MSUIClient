#!/usr/bin/env bash
# Regenerates the raid map: shared_docs/RAID_MAP.md + shared_docs/raid-map/{mc,onyxia}.{svg,json}.
# Runs tools/raidmap/raid_map.py on the VMaNGOS box (READ-ONLY: SELECTs and file reads), then unpacks its JSON
# bundle into this repo. Never hand-edit the outputs; change raid_map.py and run this again.
#   bash tools/raidmap/regen.sh            (RAIDMAP_HOST overrides the ssh target)
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd)"
host="${RAIDMAP_HOST:-wowvmangos@192.168.0.2}"
tmp="$(mktemp)"
trap 'rm -f "$tmp"' EXIT
ssh -o BatchMode=yes "$host" 'python3 - --bundle' < "$here/raid_map.py" > "$tmp"
py=python3
if ! python3 -c "" >/dev/null 2>&1; then py=python; fi
"$py" "$here/raid_map.py" --unpack "$tmp" --root "$root"
