"""Generate a map-only live smoke protocol from captured or candidate pack docs.

Offline: no network, login, publication or character change occurs while generating.
The protocol uses GM safety/positioning but never changes level/gear/spec, revives,
resets quests, changes groups, fights, or enters a dungeon. The Greymane approach
is walked continuously from its first point through its last point.

  python tools/worldpack/gen-map-smoke.py OUT.txt --docs EXPECTED-docs.json \
    --state EXPECTED-state.json --report OFFLINE-PROBE/report.json --map 0

Regenerate after candidate positions change. --dock-point x,y,z may be repeated
for collision-verified deck points (the model origin is not assumed walkable).
--dock-route 'x,y,z|x,y,z|...' walks continuously from shore onto verified deck points.
Coast support-at output is observation, not a terrain assertion; G17 and the
client collision verifier remain required. wait-grounded and walk-to judge real
body physics. worldmap-hover supplies pointer input to the ordinary map frame.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
import re
from worldmap_samples import samples


def load(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def inside(point, polygon):
    x, y = point
    result = False
    for a, b in zip(polygon, polygon[1:] + polygon[:1]):
        if (a[1] > y) != (b[1] > y) and x < (b[0] - a[0]) * (y - a[1]) / (b[1] - a[1]) + a[0]:
            result = not result
    return result


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("output", type=Path)
    ap.add_argument("--docs", type=Path, required=True)
    ap.add_argument("--state", type=Path, required=True)
    ap.add_argument("--report", type=Path)
    ap.add_argument("--map", type=int, default=0)
    ap.add_argument("--gate", default="greymane-pass")
    ap.add_argument("--pack-id", type=int, default=3)
    ap.add_argument("--dock-point", action="append", default=[])
    ap.add_argument("--dock-route", action="append", default=[])
    args = ap.parse_args()
    docs_blob, state = load(args.docs), load(args.state)
    docs = docs_blob.get("docs", []) if isinstance(docs_blob, dict) else docs_blob
    docs = [d for d in docs if int(d.get("packId", args.pack_id)) == args.pack_id and d.get("body")]
    regions = [d for d in docs if d["kind"] == "worldmap" and int(d["body"]["map"]) == args.map]
    if not regions:
        raise ValueError("map smoke needs a worldmap polygon in the input documents")
    paths = [d for d in docs if d["kind"] == "path" and d["docKey"] == args.gate and int(d["body"]["map"]) == args.map]
    if len(paths) != 1:
        raise ValueError("map smoke needs exactly one requested gate path")
    names = {int(d["docKey"]): d["body"]["fields"].get("11", d["docKey"]) for d in docs if d["kind"] == "dbc:AreaTable"}
    templates = {int(d["body"]["entry"]): d["body"] for d in docs if d["kind"] == "dbrow:creature_template"}
    spawns = [d["body"] for d in docs if d["kind"] == "dbrow:creature" and int(d["body"].get("map", -1)) == args.map]
    report = load(args.report) if args.report else {}
    measured = {int(p["id"]): p for p in report.get("spawns", []) if p.get("kind") == "creature" and int(p["map"]) == args.map}
    out = [f"# INPUT docs sha256={hashlib.sha256(args.docs.read_bytes()).hexdigest()}",
           f"# INPUT state sha256={hashlib.sha256(args.state.read_bytes()).hexdigest()}",
           "# SETUP: GM visibility protection and travel only; no character, party, quest or dungeon reset",
           "wait 12", "pose assert-dead false", "gm .gm on", "wait 2", "camera 0 10 18"]

    def label(text):
        return re.sub(r"[^a-z0-9]+", "-", str(text).lower()).strip("-")

    def support(x, y, z):
        out.append(f"support-at {x:.3f} {y:.3f} {z:.3f}")

    def travel(x, y, z, title, minimum=None):
        out.extend([f"# --- {title}", f"gm .go xyz {x:.3f} {y:.3f} {z + 2:.3f} {args.map}",
                    "wait 8", "wait-grounded 20", f"assert-grounded {(z - 8 if minimum is None else minimum):.3f}"])
        support(x, y, z)

    def walk(a, b, title):
        distance = math.hypot(b[0] - a[0], b[1] - a[1])
        out.extend([f"# {title}", f"walk-to {b[0]:.3f} {b[1]:.3f} {max(20, distance / 4 + 20):.0f} 3",
                    "wait-grounded 8", f"assert-grounded {b[2] - 8:.3f}"])
        support(*b)

    path = paths[0]["body"]
    points = path.get("approach", []) + path["points"]
    travel(*points[0], "north of Greymane gate; one setup teleport before the uninterrupted walk")
    out.extend(["dump map-gate-north", "mark"])
    for index, (a, b) in enumerate(zip(points, points[1:]), 1):
        walk(a, b, f"gate leg {index}; no teleports between path points")
        if index in (2, len(points) - 1):
            out.append(f"dump map-gate-leg-{index}")
    out.append(f"assert-new displayZone={int(regions[0]['body']['area'])}")

    paints = {}
    for d in docs:
        if d["kind"] == "tile" and int(d["body"]["map"]) == args.map:
            for p in d["body"].get("areaPaint", []):
                paints[int(p["areaId"])] = p
    checkpoints = []
    for area, p in sorted(paints.items()):
        name = names.get(area, str(area))
        if "wall" in name.lower() or "gate" in name.lower():
            continue  # The fixed gate was already walked; an old paint circle is not a second entrance.
        nearby = [s for s in spawns if math.hypot(float(s["position_x"]) - p["x"], float(s["position_y"]) - p["y"]) <= p["radius"]
                  and int(templates.get(int(s.get("id", 0)), {}).get("npc_flags", 0))]
        if not nearby:
            continue
        anchor = min(nearby, key=lambda s: math.hypot(float(s["position_x"]) - p["x"], float(s["position_y"]) - p["y"]))
        x, y, z = (float(anchor[k]) for k in ("position_x", "position_y", "position_z"))
        grounded = measured.get(int(anchor["guid"]))
        if grounded and grounded.get("ground") is not None and abs(float(grounded["ground"]) - z) > 3:
            out.append(f"# WARNING {name}: candidate NPC Z is {z - grounded['ground']:.2f} yd from offline terrain; tier 2 must settle placement")
        travel(x, y, z, f"{name}: {templates[int(anchor['id'])].get('name', anchor['id'])}")
        out.extend([f"face {math.pi:.4f}", f"dump map-{label(name)}"])
        # A bounded walk between nearby authored service points exercises actual local terrain/collision.
        partners = [s for s in nearby if 8 <= math.hypot(float(s["position_x"]) - x, float(s["position_y"]) - y) <= 35
                    and abs(float(s["position_z"]) - z) <= 6]
        if partners:
            partner = min(partners, key=lambda s: math.hypot(float(s["position_x"]) - x, float(s["position_y"]) - y))
            destination = [float(partner[k]) for k in ("position_x", "position_y", "position_z")]
            walk((x, y, z), destination, f"short service-to-service walk in {name}")
        checkpoints.append((x, y, z))

    # Docks' origins are diagnostics only; a deck assertion requires supplied collision-verified surface coordinates.
    docks = [p for p in state.get("placements", []) if int(p.get("packId", -1)) == args.pack_id
             and int(p["mapId"]) == args.map and not p.get("deleted") and "dock" in p["modelPath"].lower()]
    if docks and checkpoints:
        near_dock = min(checkpoints, key=lambda p: math.hypot(p[0] - float(docks[0]["posX"]), p[1] - float(docks[0]["posY"])))
        travel(*near_dock, "harbor floor; stream the dock tiles before inspecting their surfaces")
    for p in docks:
        out.append(f"# dock placement {p['id']} origin: support observation only, not assumed walkable deck")
        support(float(p["posX"]), float(p["posY"]), float(p["posZ"]) + 3)
    for index, route in enumerate(args.dock_route):
        route_points = [tuple(map(float, point.split(","))) for point in route.split("|")]
        if len(route_points) < 2 or any(len(point) != 3 for point in route_points):
            raise ValueError("dock route requires at least two x,y,z points")
        travel(*route_points[0], f"dock route {index + 1}: shore approach", minimum=route_points[0][2] - 2)
        for leg, (a, b) in enumerate(zip(route_points, route_points[1:]), 1):
            walk(a, b, f"dock route {index + 1} leg {leg}; shore to deck without teleport")
            out.append(f"assert-grounded {b[2] - 1:.3f}")
        out.extend(["face 1.5708", f"dump map-dock-walk-{index + 1}"])
    for index, point in enumerate(args.dock_point):
        x, y, z = map(float, point.split(","))
        travel(x, y, z, f"collision-verified dock deck {index + 1}", minimum=z - 2)
        out.extend(["face 1.5708", f"dump map-dock-deck-{index + 1}"])

    for region_doc in regions:
        region = region_doc["body"]
        polygon = region["polygon"]
        centre = tuple(sum(p[axis] for p in polygon) / len(polygon) for axis in (0, 1))
        level = float(region.get("terrain", {}).get("seaLevel", 0))
        # West, east and south: move 90 yd toward the polygon interior, then let the actual body settle.
        vertices = [max(polygon, key=lambda p: p[1]), min(polygon[2:], key=lambda p: p[1]), min(polygon, key=lambda p: p[0])]
        for edge, vertex in zip(("west", "east", "south"), vertices):
            dx, dy = centre[0] - vertex[0], centre[1] - vertex[1]
            length = math.hypot(dx, dy)
            x, y = vertex[0] + 90 * dx / length, vertex[1] + 90 * dy / length
            if not inside((x, y), polygon):
                raise ValueError(f"{edge} coast observation is not inside the polygon")
            travel(x, y, level + 120, f"{edge} coast: settle onto actual reshaped terrain", minimum=level + 0.2)
            out.extend([f"face {math.atan2(vertex[1] - y, vertex[0] - x):.5f}", f"dump map-coast-{edge}"])
            # Sea floor and liquid read at a point just outside the same coast; printed observations need review.
            sx, sy = vertex[0] - 90 * dx / length, vertex[1] - 90 * dy / length
            support(sx, sy, level + 2)

    probes = samples(regions)
    out.append("# --- mounted map ownership/name/highlight; independent authored + stock expectations")
    for m, x, y, expected, source in probes:
        out.extend([f"# {source}", f"worldmap-probe {m} {x:.3f} {y:.3f} {expected}"])
    out.extend([f"worldmap-continent {args.map}", "wait 2", "dump map-continent-painted"])
    for region_doc in regions:
        region = region_doc["body"]
        inside_points = [p for p in probes if p[3] == int(region["area"])]
        p = inside_points[len(inside_points) // 2]
        out.extend([f"worldmap-hover {p[0]} {p[1]:.3f} {p[2]:.3f}", "wait 2", f"dump map-hover-{region['area']}"])
    empty = next((p for p in probes if p[3] == 0), None)
    if empty:
        out.extend([f"worldmap-hover {empty[0]} {empty[1]:.3f} {empty[2]:.3f}", "wait 2", "dump map-hover-unowned-sea"])
    out.extend(["worldmap-hover off", "panel quest", "wait 1"])
    if checkpoints:
        travel(*checkpoints[0], "finish on a known town floor")
    out.extend(["worldmap-hover off", "gm .gm off", "wait 2", "pose assert-dead false"])
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text("\n".join(out) + "\n", encoding="utf-8")
    print(f"{args.output}: {len(out)} lines, map {args.map}, {len(points) - 1} uninterrupted gate legs, "
          f"{len(checkpoints)} settlements, {len(probes)} map probes, {len(args.dock_point)} verified deck points, "
          f"{len(args.dock_route)} shore-to-deck routes")


if __name__ == "__main__":
    main()
