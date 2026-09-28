"""Build the concrete Gilneas coastline migration from captured WorldPacks API data.

By default this is entirely offline. It writes an audited API plan, expected docs
and state, and a shoreline clearance report for inspection/build probes. Heights
remain candidates until the rebuilt terrain/collision verifier snaps them.

  python tools/worldpack/fit-gilneas.py scratch/worldbuilder/map-fit-plan.json \
    --docs scratch/worldbuilder/docs-before-mapfix.json \
    --state scratch/worldbuilder/state-before-mapfix.json

Only --apply sends the generated plan to the existing audited APIs. It first
checks that live pack docs, placements and sculpt still match the input snapshot.
It never publishes, restarts the server, writes SQL, or retries failed mutations.
Every response is journalled. A partially applied plan must be inspected; simply
rerunning it is refused by the original-state check (Relocate is not idempotent).
To apply a generated plan after offline probe corrections, without regenerating
or overwriting those corrections:
  python tools/worldpack/fit-gilneas.py PLAN.json --apply-existing

The mission-specific choices live here explicitly: one ADT west/two north, the
fixed Greymane gate, rehomed coastal settlement and wolf camp, and source tiles.
The reusable projection/geometry lives in worldmap-outline.py.
"""

import argparse
from copy import deepcopy
import hashlib
import json
import math
from pathlib import Path
import runpy
import struct
import urllib.request

REPO = Path(__file__).resolve().parents[2]
OUTLINE = REPO / "tools/worldpack/examples/gilneas/worldmap-outline.json"
GROUNDING = REPO / "tools/worldpack/examples/gilneas/map-fit-grounding.json"
# Exact float used by RelocateSpec in the web app, not a hand-rounded tile size.
TILE = struct.unpack("<f", struct.pack("<f", 533.33333))[0]
DX, DY = 2 * TILE, TILE
PACK = 3
DOCK_SPAWNS = set(range(1500103, 1500108))
WOLF_SPAWNS = set(range(1500108, 1500114)) | {1500149, 1500150}
DOCK_SHIFT = (-320, 100)
WOLF_SHIFT = (-600, -280)
GEOMETRY = runpy.run_path(str(REPO / "tools/worldpack/worldmap-outline.py"))


def load(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def numeric(value):
    if isinstance(value, dict):
        return numeric(value.get("f", 0))
    return float(value or 0)


def shift(body, x_key, y_key, dx=DX, dy=DY):
    body[x_key] = round(numeric(body.get(x_key)) + dx, 3)
    body[y_key] = round(numeric(body.get(y_key)) + dy, 3)


def relocate_doc(doc):
    """Mirror WorldPackRelocation.Transform for the fixed map0->map0 move."""
    result = deepcopy(doc)
    kind, body = result["kind"], result["body"]
    if kind == "tile" and body["map"] == 0:
        body["col"] -= 1
        body["row"] -= 2
        for key in ("areaPaint", "healHoles"):
            for point in body.get(key, []):
                shift(point, "x", "y")
        result["docKey"] = f"0:{body['col']}:{body['row']}"
    elif kind in ("dbrow:creature", "dbrow:gameobject") and numeric(body.get("map")) == 0:
        shift(body, "position_x", "position_y")
    elif kind == "dbrow:map_template" and numeric(body.get("ghost_entrance_map")) == 0:
        shift(body, "ghost_entrance_x", "ghost_entrance_y")
    elif kind == "dbrow:areatrigger_template" and numeric(body.get("map_id")) == 0:
        shift(body, "x", "y")
    elif kind == "dbrow:areatrigger_teleport" and numeric(body.get("target_map")) == 0:
        shift(body, "target_position_x", "target_position_y")
    elif kind in ("dbc:AreaTrigger", "dbc:WorldSafeLocs") and numeric(body.get("fields", {}).get("1")) == 0:
        fields = body["fields"]
        fields["2"] = {"f": numeric(fields.get("2")) + DX}
        fields["3"] = {"f": numeric(fields.get("3")) + DY}
    elif kind == "dbc:Light" and numeric(body.get("fields", {}).get("1")) == 0:
        return None  # ToStockMap=true drops redundant pack lighting.
    # Path docs are deliberately not moved by the current Relocate API.
    return result


def document(kind, key, body):
    return {"packId": PACK, "kind": kind, "docKey": key, "body": body}


def canonical_hash(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def original_fingerprints(docs, state):
    pack_docs = sorted((d for d in docs if d["packId"] == PACK), key=lambda d: (d["kind"], d["docKey"]))
    pack_placements = sorted(({k: v for k, v in p.items() if k != "published"}
                              for p in state["placements"] if p["packId"] == PACK), key=lambda p: p["id"])
    # State returns the enabled-pack sum. The captured Gilneas sculpt is only
    # 28,36; 32,48 belongs to the unrelated Northshire test and must not move.
    sculpt = [s for s in state["sculpt"] if (s["col"], s["row"]) == (28, 36)]
    return {"docs": canonical_hash(pack_docs), "placements": canonical_hash(pack_placements),
            "gilneasSculpt28_36": canonical_hash(sculpt)}


def coast_distance(point, polygon):
    distances = []
    for a, b in zip(polygon, polygon[1:] + polygon[:1]):
        direction = [b[k] - a[k] for k in (0, 1)]
        u = max(0, min(1, sum((point[k] - a[k]) * direction[k] for k in (0, 1)) /
                       sum(v * v for v in direction)))
        distances.append(math.hypot(*(point[k] - a[k] - u * direction[k] for k in (0, 1))))
    distance = min(distances)
    return distance if GEOMETRY["inside"](point, polygon) else -distance


def fit_report(docs, placements, polygon, maritime=()):
    points = []
    for d in docs:
        b = d["body"]
        if d["kind"] == "dbrow:creature" and b.get("map") == 0:
            points.append(("creature", int(d["docKey"]), b["position_x"], b["position_y"], b.get("wander_distance", 0)))
    for p in placements:
        if p["packId"] == PACK and not p["deleted"] and p["mapId"] == 0:
            points.append(("placement", p["id"], p["posX"], p["posY"], 0))
    out = []
    for kind, ident, x, y, wander in points:
        clearance = coast_distance((x, y), polygon)
        out.append({"kind": kind, "id": ident, "x": x, "y": y,
                    "inside": clearance >= 0, "clearance": round(clearance, 3), "wander": wander,
                    "withinCoastBlend180": clearance < 180 + wander,
                    "maritimeOutsideZone": kind == "placement" and ident in maritime,
                    "fixedGateOutsideZone": kind == "placement" and ident == 14})
    return out


def make_plan(docs_snapshot, state_snapshot, outline, path_body=None, data_dir=None, grounding=None):
    docs = docs_snapshot["docs"]
    grounding = load(GROUNDING) if grounding is None else grounding
    assert all(d["packId"] == PACK for d in docs), "Docs snapshot must contain only Gilneas pack3"
    assert any(p["id"] == PACK and p["packKey"] == "gilneas" for p in state_snapshot["packs"])
    original = {(d["kind"], d["docKey"]): d for d in docs}
    source = GEOMETRY["Source"](data_dir or REPO / "GameData/Data")
    assert ("tile", "0:28:39") in original and ("worldmap", "0:7001") not in original, "Expected pre-migration snapshot"
    relocated = [r for d in docs if (r := relocate_doc(d)) is not None]
    final = {(d["kind"], d["docKey"]): deepcopy(d) for d in relocated}
    fixed_gate = deepcopy(original[("tile", "0:29:33")])
    template = deepcopy(final[("tile", "0:27:34")]["body"])
    area_paint = deepcopy(template["areaPaint"])
    for paint in area_paint:
        if paint["areaId"] == 7004:
            shift(paint, "x", "y", *DOCK_SHIFT)
    # Entirely rebuild map0's tile set after Relocate. Old moved gateway, row33
    # and west26 sources are removed; identity apron tiles replace west26.
    final = {key: d for key, d in final.items() if not (d["kind"] == "tile" and d["body"]["map"] == 0)}
    relocated_by_key = {(d["kind"], d["docKey"]): d for d in relocated}
    tiles, skipped_apron = [], []
    for row in range(34, 39):
        for col in range(26, 32):
            key = f"0:{col}:{row}"
            if col in range(27, 30) and row in range(34, 38):
                body = deepcopy(relocated_by_key[("tile", key)]["body"])
                body["areaPaint"] = deepcopy(area_paint)
            elif col == 30 and row in range(34, 38):
                body = {"map": 0, "col": col, "row": row, "sourceMap": "Azeroth",
                        "sourceCol": 31, "sourceRow": row - 4, "keepDoodads": True,
                        "keepWmos": False, "areaId": 7001, "areaPaint": deepcopy(area_paint), "stitch": 150}
            else:
                # Vanilla WDT has no ADTs in some open-sea cells. They already
                # are sea: do not invent an identity stamp from a missing file.
                try:
                    source.read(rf"World\Maps\Azeroth\Azeroth_{col}_{row}.adt")
                except ValueError:
                    skipped_apron.append([col, row])
                    continue
                # The coast processor only clears identity source objects in
                # its changed footprint. Keep the stock world outside it.
                body = {"map": 0, "col": col, "row": row, "sourceMap": "Azeroth",
                        "sourceCol": col, "sourceRow": row, "keepDoodads": True,
                        "keepWmos": True, "areaId": 7001, "areaPaint": deepcopy(area_paint)}
            final[("tile", key)] = document("tile", key, body)
            tiles.append([col, row])
    final[("tile", "0:29:33")] = fixed_gate
    worldmap = deepcopy(outline)
    worldmap["terrain"] = {"tiles": tiles, "joinNorth": -1066.66667, "joinWidth": 80,
                           "coastWidth": 180, "seaDepth": 515, "seaLevel": 0, "minimumLand": 6}
    final[("worldmap", "0:7001")] = document("worldmap", "0:7001", worldmap)
    if path_body is None:
        path_body = deepcopy(original[("path", "greymane-pass")]["body"])
        path_body["points"] = [[-1000, 1500, 52], [-1133, 1500, 55], [-1250, 1560, 50],
                               [-1375, 1610, 55], [-1510, 1660, 54]]
        path_body["width"], path_body["falloff"] = 24, 45
    final[("path", "greymane-pass")] = document("path", "greymane-pass", path_body)
    for (kind, key), d in final.items():
        if kind == "dbrow:creature" and int(key) in DOCK_SPAWNS | WOLF_SPAWNS:
            shift(d["body"], "position_x", "position_y", *(DOCK_SHIFT if int(key) in DOCK_SPAWNS else WOLF_SHIFT))
    for key, pose in grounding.get("spawnZ", {}).items():
        body = final[("dbrow:creature", key)]["body"]
        if abs(body["position_x"] - pose["x"]) > 0.01 or abs(body["position_y"] - pose["y"]) > 0.01:
            raise ValueError(f"Ground probe for spawn {key} is stale: horizontal position changed")
        body["position_x"] = pose.get("targetX", body["position_x"])
        body["position_y"] = pose.get("targetY", body["position_y"])
        body["position_z"] = pose["z"]
    expected_docs = sorted(final.values(), key=lambda d: (d["kind"], d["docKey"]))
    source_checks = []
    for d in expected_docs:
        if d["kind"] != "tile":
            continue
        b = d["body"]
        source_path = rf"World\Maps\{b['sourceMap']}\{b['sourceMap']}_{b['sourceCol']}_{b['sourceRow']}.adt"
        try:
            _, source_metadata = source.read(source_path)
        except ValueError as error:
            raise ValueError(f"Required tile {d['docKey']} has no stock source: {source_path}") from error
        source_checks.append({"tile": d["docKey"], **source_metadata})
    items = []
    for key in sorted(set(relocated_by_key) | set(final)):
        if final.get(key) != relocated_by_key.get(key):
            items.append({"kind": key[0], "key": key[1], "body": final[key]["body"] if key in final else None})
    operator = "codex-gilneas-map-fit"
    requests = [{"route": "Relocate", "body": {"packId": PACK, "fromMap": 0, "toMap": 0,
                  "dCol": -1, "dRow": -2, "operator": operator}},
                {"route": "Content", "body": {"packId": PACK,
                  "label": "fit Gilneas to painted peninsula; keep Greymane gate; coast/hover data; rehome coastal camps",
                  "operator": operator, "items": items}}]
    expected_state = deepcopy(state_snapshot)
    relocated_placements = deepcopy(state_snapshot["placements"])
    for p in relocated_placements:
        if p["packId"] == PACK and not p["deleted"] and p["mapId"] == 0:
            p["posX"] += DX
            p["posY"] += DY
    for p in expected_state["placements"]:
        if p["packId"] != PACK or p["deleted"] or p["mapId"] != 0:
            continue
        if p["id"] != 14:
            p["posX"] = round(p["posX"] + DX + DOCK_SHIFT[0], 3)
            p["posY"] = round(p["posY"] + DY + DOCK_SHIFT[1], 3)
        correction = grounding.get("placementAdjustments", {}).get(str(p["id"]), {})
        p["posX"] += correction.get("dx", 0)
        p["posY"] += correction.get("dy", 0)
        p["posZ"] = correction.get("z", p["posZ"])
        for field in ("rotX", "rotY", "rotZ", "scale"):
            if field in correction:
                p[field] = correction[field]
        body = {k: p[k] for k in ("packId", "mapId", "kind", "modelPath", "posX", "posY", "posZ",
                                   "rotX", "rotY", "rotZ", "scale", "doodadSet")}
        body.update(placementId=p["id"], operator=operator)
        requests.append({"route": "Move", "body": body})
    for s in expected_state["sculpt"]:
        if (s["col"], s["row"]) == (28, 36):
            s["col"], s["row"] = 27, 34
    expected_state["note"] = "Offline candidate after audited plan; publishedSculpt/lastBuild retain pre-publish facts; heights not yet collision-verified."
    baseline_fit = fit_report(relocated, relocated_placements, outline["polygon"])
    final_fit = fit_report(expected_docs, expected_state["placements"], outline["polygon"], grounding.get("maritimePlacements", []))
    outside = [r for r in final_fit if not r["inside"] and not r["fixedGateOutsideZone"] and not r["maritimeOutsideZone"]]
    assert not outside, f"Proposed non-gate content is outside shoreline: {outside}"
    # Dungeon geometry, NPCs and patrols do not move. Its map0 return/ghost
    # arrivals do move, intentionally, with the overworld fortress entrance.
    for d in docs:
        b = d["body"]
        if (d["kind"] == "tile" and b["map"] == 801) or (d["kind"] == "dbrow:creature" and b["map"] == 801) or d["kind"] == "dbrow:creature_movement":
            assert final[(d["kind"], d["docKey"])] == d
    assert final[("tile", "0:29:33")] == original[("tile", "0:29:33")]
    report = {"translated": baseline_fit, "proposed": final_fit,
              "heightStatus": "Selected outdoor spawn and stable heights corrected from offline ground probe; intentional WMO floor heights preserved; client collision check remains required",
              "grounding": grounding,
              "dockDeltaAfterTranslation": DOCK_SHIFT, "wolfDeltaAfterTranslation": WOLF_SHIFT}
    plan = {"schema": 1, "packId": PACK, "mapId": 0,
            "description": "Gilneas painted-peninsula migration, staged through undoable WorldPacks APIs",
            "translation": {"dCol": -1, "dRow": -2, "worldX": DX, "worldY": DY},
            "skippedMissingSeaApron": skipped_apron, "stockTileSourceChecks": source_checks,
            "preconditions": original_fingerprints(docs, state_snapshot),
            "status": "candidate; ground heights and entrance path require offline build review before publish",
            "requests": requests}
    return plan, {"success": True, "docs": expected_docs}, expected_state, report


def call(web, route, body=None):
    request = urllib.request.Request(f"{web.rstrip('/')}/WorldPacks/{route}",
        data=json.dumps(body).encode() if body is not None else None,
        headers={"Content-Type": "application/json"}, method="POST" if body is not None else "GET")
    with urllib.request.urlopen(request, timeout=90) as response:
        value = json.load(response)
    if not value.get("success"):
        raise RuntimeError(f"{route}: {value.get('error', value)}")
    return value


def apply(plan, web, journal_path):
    live_docs = call(web, f"Docs?packId={PACK}")["docs"]
    live_state = call(web, "State?mapId=0")
    if original_fingerprints(live_docs, live_state) != plan["preconditions"]:
        raise RuntimeError("Live pack differs from the before snapshot; refusing a repeated/stale Relocate")
    if journal_path.exists():
        raise RuntimeError(f"Journal already exists: {journal_path}; inspect it before a new attempt")
    journal = {"planSha256": canonical_hash(plan), "web": web, "responses": []}
    save(journal_path, journal)
    for i, request in enumerate(plan["requests"]):
        journal["pending"] = {"index": i, "route": request["route"]}
        save(journal_path, journal)
        response = call(web, request["route"], request["body"])
        journal["responses"].append({"index": i, "route": request["route"], "response": response})
        journal.pop("pending")
        save(journal_path, journal)
        print(f"Applied {i + 1}/{len(plan['requests'])}: {request['route']}", flush=True)
    journal["complete"] = True
    save(journal_path, journal)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("output", type=Path)
    parser.add_argument("--docs", type=Path)
    parser.add_argument("--state", type=Path)
    parser.add_argument("--outline", type=Path, default=OUTLINE)
    parser.add_argument("--grounding", type=Path, default=GROUNDING, help="Reviewed ground-probe and waterfront corrections")
    parser.add_argument("--data", type=Path, default=REPO / "GameData/Data", help="Stock client MPQ directory; patch-7 is excluded")
    parser.add_argument("--path", type=Path, help="Reviewed replacement greymane-pass body JSON")
    parser.add_argument("--apply", action="store_true", help="Send plan to audited APIs after matching live-state checks")
    parser.add_argument("--apply-existing", action="store_true", help="Apply the existing output plan exactly; do not regenerate it")
    parser.add_argument("--web", default="http://192.168.0.2:5000")
    args = parser.parse_args()
    if args.apply_existing:
        if args.apply or args.docs or args.state or args.path:
            parser.error("--apply-existing takes only the existing plan output path and optional --web")
        apply(load(args.output), args.web, args.output.with_name(args.output.stem + "-apply-journal.json"))
        return
    if not args.docs or not args.state:
        parser.error("Generating a plan requires --docs and --state snapshots")
    plan, docs, state, report = make_plan(load(args.docs), load(args.state), load(args.outline), load(args.path) if args.path else None, args.data, load(args.grounding))
    outputs = {"plan": args.output, "docs": args.output.with_name(args.output.stem + "-expected-docs.json"),
               "state": args.output.with_name(args.output.stem + "-expected-state.json"),
               "fit": args.output.with_name(args.output.stem + "-fit.json")}
    for key, value in (("plan", plan), ("docs", docs), ("state", state), ("fit", report)):
        save(outputs[key], value)
    print(json.dumps({"outputs": {k: str(v) for k, v in outputs.items()}, "requests": len(plan["requests"]),
        "contentFixes": len(plan["requests"][1]["body"]["items"]),
        "translatedOutside": sum(not r["inside"] for r in report["translated"]),
        "proposedOutside": sum(not r["inside"] and not r["fixedGateOutsideZone"] and not r["maritimeOutsideZone"] for r in report["proposed"])}, indent=2))
    if args.apply:
        apply(plan, args.web, args.output.with_name(args.output.stem + "-apply-journal.json"))


if __name__ == "__main__":
    main()
