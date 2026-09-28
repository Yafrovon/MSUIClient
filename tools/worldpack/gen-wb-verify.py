# World Pack Verifier, tiers 1+2 as ONE client run: generate the World Builder script (MSUI_WB_SCRIPT)
# that publishes (optional) and then checks every map of a pack against the real client collision -
# spawns, portal arrivals and every terrain hole (the probe CharacterController drop + walk-off pass).
# Derived from the pack's docs, so a new zone needs no edits (shared_docs/WORLD_BUILDER.md §9).
#
#   python tools/worldpack/gen-wb-verify.py OUT.txt --pack <id|key> [--web URL] [--maps 0,1] [--publish] [--fix] [--changed]
#
#   --publish   preflight (refuses on C0) -> publish with mangosd restart -> wait -> tier-1 report -> download
#               (patch-7 + pack collision) before the collision pass
#   --fix       tier-2 fix mode: floor snaps and open-ground moves posted back as ONE op
#   --changed   only targets that changed since their last clean pass (worldpack-verified.json)
#
# Run it (no account login happens in a script run):
#   MSUI_SETTINGS_PATH=<creator settings> MSUI_WB_SCRIPT=OUT.txt MSUIClient.exe
# Results: "[verify-client] SUMMARY map N: ... error(s)" per map in the console log; hole hand-off for
# tier 3 in <client bin>/worldpack-holes.json (tools/worldpack/gen-live.py --holes).
import json, os, sys, urllib.request

args = sys.argv[1:]
path = args[0] if args and not args[0].startswith("--") else "wb-verify.txt"
def opt(name, default):
    return args[args.index(name) + 1] if name in args else default
WEB = opt("--web", os.environ.get("MSUI_WEBAPP", "http://192.168.0.2:5000")).rstrip("/")
TILE = 533.33333
MAP_FILTER = {int(m) for m in opt("--maps", "").split(",") if m} if "--maps" in args else None

def get(route):
    return json.load(urllib.request.urlopen(f"{WEB}/WorldPacks/{route}", timeout=60))

packs = get("Packs")["packs"]
want = opt("--pack", None)
if want is None:
    raise SystemExit("--pack <id|key> is required; packs: " + ", ".join(f"{p['id']}={p['packKey']}" for p in packs))
pack = next((p for p in packs if str(p["id"]) == want or p["packKey"] == want), None)
if pack is None:
    raise SystemExit(f"no pack {want}")
docs = get(f"Docs?packId={pack['id']}")["docs"]

# Maps to visit: every pack map (its stamped tiles) plus any stock map the pack spawns on.
tiles, spawn_maps = {}, {}
for d in docs:
    b = d["body"]
    if not b:
        continue
    if d["kind"] == "tile":
        tiles.setdefault(int(b["map"]), []).append((int(b["col"]), int(b["row"])))
    elif d["kind"] in ("dbrow:creature", "dbrow:gameobject") and "map" in b:
        spawn_maps.setdefault(int(float(b["map"])), []).append((float(b["position_x"]), float(b["position_y"]), float(b["position_z"])))

out = [f"pack {pack['packKey']}", "hour 13"]
if "--publish" in args:
    out += ["preflight", "publish 1", "wait-build", "verify", "download"]
first = True
visit_maps = (set(tiles) | set(spawn_maps))
if MAP_FILTER is not None:
    visit_maps &= MAP_FILTER
for m in sorted(visit_maps):
    if m in tiles:   # the middle of the stamped block, high enough to clear anything
        cols = [c for c, _ in tiles[m]]; rows = [r for _, r in tiles[m]]
        x = (32 - (min(rows) + max(rows) + 1) / 2) * TILE
        y = (32 - (min(cols) + max(cols) + 1) / 2) * TILE
        z = 200.0
    else:            # a stock map: the first spawn of the pack there
        x, y, z = spawn_maps[m][0]; z += 30
    out += [f"travel {m} {x:.1f} {y:.1f} {z:.1f}", "wait 10", "docs"]
    if first and "--publish" not in args:
        out.append("verify")   # load the last report: tier 2 takes its terrain holes (G12) from it
    first = False
    out.append("verify-spawns" + (" fix" if "--fix" in args else "") + (" changed" if "--changed" in args else ""))
out.append("quit")
with open(path, "w", encoding="utf-8", newline="\n") as f:
    f.write("\n".join(out) + "\n")
print(f"{path}: {len(out)} lines, pack {pack['id']} ({pack['packKey']}), maps {sorted(visit_maps)}")
