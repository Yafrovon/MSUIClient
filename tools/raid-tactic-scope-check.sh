#!/usr/bin/env bash
# Raid tactic containment (shared_docs/RAID_TACTIC_SCOPE_LAW.md), Core half. Run from this repo:
#   bash tools/raid-tactic-scope-check.sh                 # over ssh against 192.168.0.2:~/vmangos
#   bash tools/raid-tactic-scope-check.sh --local ~/vmangos   # on the box itself
# Owner, 2026-11-15: no "lossy" generic logic for a raid fight - a tactic made for one raid's pack or boss is kept to
# that raid (and those creatures) and leaves every other raid, dungeon and boss as it was.
# Checks src/game/SuperUiContent/SuiBots/SuiTacticScope.h (the registry) against the bot and autopilot sources:
#   1. every registered tactic names at least one raid map (no global tactics) and a unique key;
#   2. every enum id has exactly one row, every gate names a registered id, every id is gated somewhere;
#   3. every dated comment from 2026-11-15 on in SuiBots/*.cpp|h and SuiWorld/CRPG/SuiAutopilot.cpp sits beside a
#      SuiTactic::On / OnAnyAttacker gate (15 lines before to 40 after), unless it carries
#      "[TACTICS-SCOPE-EXEMPT: why]" within 3 lines (diagnostics, telemetry, crash fixes - never behaviour).
# Exit 1 with every violation listed; prints PASS otherwise.
set -u
if [ "${1:-}" = "--local" ]; then
  TREE="${2:-$HOME/vmangos}"
  py() { python3 - "$TREE"; }
else
  py() { ssh 192.168.0.2 'python3 - ~/vmangos'; }
fi
py <<'PYEOF'
import re, sys, glob, os
tree = os.path.expanduser(sys.argv[1])
base = os.path.join(tree, 'src/game/SuperUiContent')
hdr_path = os.path.join(base, 'SuiBots/SuiTacticScope.h')
errors = []
if not os.path.exists(hdr_path):
    print('RAID_TACTIC_SCOPE FAIL: no registry at ' + hdr_path); sys.exit(1)
hdr = open(hdr_path).read()

m = re.search(r'enum Id : uint8\s*\{(.*?)\bCount\b', hdr, re.S)
ids = re.findall(r'^\s*(\w+)\s*,', m.group(1), re.M) if m else []
if not ids:
    errors.append('registry: no enum Id found')
rows = re.findall(r'\{\s*(\w+)\s*,\s*"([^"]+)"\s*,\s*\{([^}]*)\}\s*,\s*\{([^}]*)\}\s*,', hdr)
seen, keys = {}, set()
for rid, key, maps, entries in rows:
    seen[rid] = seen.get(rid, 0) + 1
    if key in keys:
        errors.append('registry: key "%s" used twice' % key)
    keys.add(key)
    if not maps.strip() or all(t.strip() in ('', '0') for t in maps.split(',')):
        errors.append('registry: %s ("%s") names no raid map - global tactics are not allowed' % (rid, key))
for i in ids:
    if seen.get(i, 0) != 1:
        errors.append('registry: enum %s has %d rows (need exactly 1)' % (i, seen.get(i, 0)))

files = sorted(set(glob.glob(os.path.join(base, 'SuiBots/*.cpp')) + glob.glob(os.path.join(base, 'SuiBots/*.h')) +
                   [os.path.join(base, 'SuiWorld/CRPG/SuiAutopilot.cpp')]))
files = [f for f in files if not f.endswith('SuiTacticScope.h')]
gate_re = re.compile(r'SuiTactic::(?:On|OnAnyAttacker)\s*\(')
id_re = re.compile(r'SuiTactic::(\w+)')
date_re = re.compile(r'\b(20\d\d)-(\d\d)-(\d\d)\b')
used = set()
for f in files:
    lines = open(f, errors='replace').read().split('\n')
    gates = []
    for n, line in enumerate(lines):
        if gate_re.search(line):
            gates.append(n)
            for t in id_re.findall(line):
                if t in ('On', 'OnAnyAttacker'):
                    continue
                if t not in ids:
                    errors.append('%s:%d gates unknown tactic SuiTactic::%s' % (os.path.relpath(f, tree), n + 1, t))
                used.add(t)
    for n, line in enumerate(lines):
        c = line.find('//')
        if c < 0:
            continue
        for y, mo, d in date_re.findall(line[c:]):
            if (int(y), int(mo), int(d)) < (2026, 11, 15):
                continue
            if any('[TACTICS-SCOPE-EXEMPT' in lines[k] for k in range(max(0, n - 3), min(len(lines), n + 4))):
                continue
            if not any(n - 15 <= g <= n + 40 for g in gates):
                errors.append('%s:%d dated %s-%s-%s change without a SuiTactic::On gate beside it: %s' %
                              (os.path.relpath(f, tree), n + 1, y, mo, d, line.strip()[:110]))
            break
for i in ids:
    if i not in used:
        errors.append('registry: %s is registered but gated nowhere' % i)
if errors:
    for e in errors:
        print('RAID_TACTIC_SCOPE FAIL: ' + e)
    sys.exit(1)
print('RAID_TACTIC_SCOPE PASS: %d tactics, all scoped to a raid; %d files checked' % (len(ids), len(files)))
PYEOF
