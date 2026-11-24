#!/usr/bin/env python3
"""Generated raid map for Molten Core (409) and Onyxia's Lair (249).

Two modes, one file (python stdlib only):

  On the box (READ-ONLY: SELECTs through the mysql client and file reads, nothing else):
      ssh -o BatchMode=yes wowvmangos@192.168.0.2 'python3 - --bundle' < tools/raidmap/raid_map.py > bundle.json
    prints one JSON bundle {"files": {repo-relative path: content}} on stdout.

  Locally (no DB): python tools/raidmap/raid_map.py --unpack bundle.json --root <repo>
    writes the bundle's files into the repo (shared_docs/RAID_MAP.md, shared_docs/raid-map/*).

tools/raidmap/regen.sh does both. The outputs are generated: change this script, never the outputs.

What it derives (rules are also printed into RAID_MAP.md):
  * packs  = connected components of creature_groups + creature_linking (+ creature_linking_template) edges, then
             single-linkage <= PROX_LINK yd (3D) that attaches UNGROUPED non-patrolling spawns to a neighbour; proximity
             never merges two different DB groups. Id = P<lowest spawn guid>; a component holding a rank-3 creature is a
             boss, id B-<short name>.
  * patrols = every movement_type 2 spawn with a waypoint path (creature_movement by guid, else
             creature_movement_template by entry). Id = R<spawn guid>. A lone patroller is its own object (kind patrol);
             a patrolling group leader (Lucifron, Garr, Geddon) keeps its B-/P- id and its route is R<leader guid>.
  * areas  = nearest anchor (3D distance from the object's centre): the instance entrance (areatrigger_teleport target)
             and each boss (spawn row, else playbook summonAt, else the first script position naming it).
  * reach  = 28 yd for an idle elite (the autopilot's credit), 34 yd for the kinds in the wide-idle-pack-reach tactic
             row, template detection_range for non-elites. "Passes near" lists everything within reach + 10 yd.
"""
import argparse
import collections
import json
import math
import os
import re
import subprocess
import sys

# ---------------------------------------------------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------------------------------------------------
HOME = os.path.expanduser("~")
VM = os.path.join(HOME, "vmangos")
ETC = os.path.join(VM, "run", "etc")
SRC = os.path.join(VM, "src")
SCOPE_H = os.path.join(SRC, "game", "SuperUiContent", "SuiBots", "SuiTacticScope.h")
SUI_TREE = os.path.join(SRC, "game", "SuperUiContent")

RAIDS = [
    {"map": 409, "key": "mc", "name": "Molten Core",
     "scripts": os.path.join(SRC, "scripts", "eastern_kingdoms", "burning_steppes", "molten_core")},
    {"map": 249, "key": "onyxia", "name": "Onyxia's Lair",
     "scripts": os.path.join(SRC, "scripts", "kalimdor", "dustwallow_marsh", "onyxias_lair")},
]

PROX_LINK = 12.0          # single-linkage distance that attaches ungrouped spawns to a pack
ELITE_REACH = 28.0        # the autopilot's reach credit for an idle elite
WIDE_REACH = 34.0         # ... for the kinds in the wide-idle-pack-reach tactic row
WIDE_REACH_KEY = "wide-idle-pack-reach"
NEAR_MARGIN = 10.0        # "passes near" = reach + this
NEIGHBOUR = 40.0          # pack-to-pack neighbour listing (min member distance)
SPLIT_LINK = 30.0        # a DB-linked component whose members split into clusters > this apart is "linked across"
RECENT_DAYS = 7
WALK_BASE, RUN_BASE = 2.5, 7.0   # yd/s at speed 1.0
FLAG_ALWAYS_RUN = 0x40

GROUP_FLAGS = [(0x001, "formation move"), (0x002, "aggro together"), (0x004, "evade together"),
               (0x008, "respawn together"), (0x010, "respawn all on master evade"),
               (0x020, "respawn all on any evade"), (0x040, "inform leader on member died"),
               (0x080, "inform members on any died")]

GO_DRAW = [("Rune of", "rune"), ("Molten Core Circle", "circle"), ("Cache of", "chest"), ("Core Fragment", "item"),
           ("Hot Coal", "trap"), ("Onyxia Egg", "egg"), ("Lava Fissure", "fissure"), ("LavaTrap", "lavatrap")]

COLORS = {"boss": "#b03a2e", "elite": "#d35400", "pack": "#7d8c8d", "patrol": "#1f618d", "wander": "#5d6d7e",
          "playbook": "#7d3c98", "script": "#1e8449", "core": "#9a7d0a", "egg": "#8d6e63", "fissure": "#e74c3c",
          "go": "#566573", "breath": "#c0392b", "threshold": "#943126"}


# ---------------------------------------------------------------------------------------------------------------------
# Small helpers
# ---------------------------------------------------------------------------------------------------------------------
def q(sql):
    """One READ-ONLY query through the mysql client (TSV, no headers)."""
    if not sql.lstrip().lower().startswith(("select", "show")):
        raise SystemExit("raid_map.py only reads: refused " + sql[:60])
    out = subprocess.run(["mysql", "-umangos", "-pmangos", "-B", "-N", "mangos", "-e", sql],
                         stdout=subprocess.PIPE, stderr=subprocess.PIPE, universal_newlines=True)
    if out.returncode != 0:
        raise SystemExit("mysql failed: " + out.stderr.strip() + "\n" + sql)
    rows = []
    for line in out.stdout.splitlines():
        rows.append([None if c == "NULL" else c.replace("\\t", "\t").replace("\\n", "\n") for c in line.split("\t")])
    return rows


def read_text(path):
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            return f.read()
    except OSError:
        return None


def d3(a, b):
    return math.sqrt((a[0] - b[0]) ** 2 + (a[1] - b[1]) ** 2 + (a[2] - b[2]) ** 2)


def d2(a, b):
    return math.hypot(a[0] - b[0], a[1] - b[1])


def r1(v):
    return round(float(v), 1)


def fmt_xyz(p):
    return "(%.1f, %.1f, %.1f)" % (p[0], p[1], p[2])


def fmt_xy(p):
    return "(%.0f, %.0f)" % (p[0], p[1])


def fmt_secs(s):
    s = int(s)
    if s >= 86400 and s % 86400 == 0:
        return "%d d" % (s // 86400)
    if s >= 3600:
        return ("%.1f h" % (s / 3600.0)).replace(".0 h", " h")
    if s >= 60:
        return ("%.1f min" % (s / 60.0)).replace(".0 min", " min")
    return "%d s" % s


def guid_ranges(guids):
    guids = sorted(guids)
    out, start, prev = [], None, None
    for g in guids:
        if start is None:
            start = prev = g
        elif g == prev + 1:
            prev = g
        else:
            out.append(str(start) if start == prev else "%d-%d" % (start, prev))
            start = prev = g
    if start is not None:
        out.append(str(start) if start == prev else "%d-%d" % (start, prev))
    return ", ".join(out)


def decode_group_flags(flags):
    return ", ".join(n for b, n in GROUP_FLAGS if flags & b) or "none"


class UnionFind:
    def __init__(self):
        self.p = {}

    def find(self, a):
        self.p.setdefault(a, a)
        while self.p[a] != a:
            self.p[a] = self.p[self.p[a]]
            a = self.p[a]
        return a

    def union(self, a, b):
        ra, rb = self.find(a), self.find(b)
        if ra != rb:
            self.p[max(ra, rb)] = min(ra, rb)


def seg_point_dist(a, b, p):
    """3D distance from p to segment ab, and the parameter t."""
    ab = (b[0] - a[0], b[1] - a[1], b[2] - a[2])
    ap = (p[0] - a[0], p[1] - a[1], p[2] - a[2])
    L = ab[0] ** 2 + ab[1] ** 2 + ab[2] ** 2
    t = 0.0 if L == 0 else max(0.0, min(1.0, (ap[0] * ab[0] + ap[1] * ab[1] + ap[2] * ab[2]) / L))
    c = (a[0] + ab[0] * t, a[1] + ab[1] * t, a[2] + ab[2] * t)
    return d3(c, p), t


def route_dist(segments, p):
    """(distance, segment index) from p to the closest point of a list of segments."""
    best = (1e18, -1)
    for i, (a, b) in enumerate(segments):
        d, _ = seg_point_dist(a, b, p)
        if d < best[0]:
            best = (d, i)
    return best


def short_boss_name(name):
    n = name.split(" the ")[0]
    w = n.split()
    if len(w) > 1 and w[0] in ("Baron", "Lord", "High", "General", "Prince", "King", "Lady"):
        return w[1]
    return w[0]


def composition(members):
    c = collections.Counter(m["name"] for m in members)
    parts = sorted(c.items(), key=lambda kv: (kv[1], kv[0]))
    return " + ".join(("%dx %s" % (n, k)) if n > 1 else k for k, n in parts)


# ---------------------------------------------------------------------------------------------------------------------
# Source parsers (Core files, read-only)
# ---------------------------------------------------------------------------------------------------------------------
FLOAT = r"(-?\d+\.\d*)f?"
TRIPLE = re.compile(FLOAT + r"\s*,\s*" + FLOAT + r"\s*,\s*" + FLOAT)
CAPS = re.compile(r"\b((?:NPC|SPELL|OBJECT|GO|DATA|TYPE)_[A-Z0-9_]+)\b")
CALLS = re.compile(r"\b(MovePoint|NearTeleportTo|SummonCreature|SummonGameObject|Relocate|TeleportTo|MoveJump)\b")
ARRAY_DECL = re.compile(r"(\w+)\s*(?:\[[^\]]*\])+\s*=")
FUNC_DECL = re.compile(r"^\s*(?:void|bool|uint32|int|float|Creature\*|Unit\*)\s+(\w+)\s*\(")
THRESH = re.compile(r"GetPosition([XYZ])\(\)\s*([<>]=?)\s*" + FLOAT)
ENUM_VAL = re.compile(r"\b((?:NPC|GO|OBJECT)_[A-Z0-9_]+)\s*=\s*(\d+)")
FLOAT_CONST = re.compile(r"static\s+float\s+const\s+(\w+)\s*=\s*" + FLOAT)
MOVE_ROW = re.compile(r"\{\s*(\d+)\s*,\s*(\d+)\s*,\s*(SPELL_\w+)\s*,\s*" + FLOAT + r"\s*,\s*" + FLOAT + r"\s*,\s*" +
                      FLOAT + r"\s*,\s*" + FLOAT + r"\s*\}\s*,?\s*(?://\s*(.*))?")


def parse_scripts(script_dir):
    """Every literal x,y,z triple in the raid's boss/instance scripts with a label, plus threshold lines,
    named float constants, the NPC_/GO_ enum and the Onyxia-style flight table."""
    out = {"positions": [], "thresholds": [], "constants": [], "enum": {}, "flight": [], "swaps": []}
    if not os.path.isdir(script_dir):
        return out
    for fn in sorted(os.listdir(script_dir)):
        if not fn.endswith((".cpp", ".h")):
            continue
        text = read_text(os.path.join(script_dir, fn)) or ""
        array, func, case, in_block = None, None, None, False
        for ln, raw in enumerate(text.splitlines(), 1):
            # drop /* ... */ block comments (boss_onyxia.cpp ends with an SQL block)
            line, rest = "", raw
            while rest:
                if in_block:
                    k = rest.find("*/")
                    if k < 0:
                        rest = ""
                    else:
                        rest, in_block = rest[k + 2:], False
                else:
                    k = rest.find("/*")
                    if k < 0:
                        line, rest = line + rest, ""
                    else:
                        line, rest, in_block = line + rest[:k], rest[k + 2:], True
            if not line.strip():
                continue
            code = line.split("//")[0]
            comment = line.split("//", 1)[1].strip() if "//" in line else ""
            cm_case = re.search(r"\bcase\s+(NPC_\w+)\s*:", code)
            if cm_case:
                case = cm_case.group(1)
            um = re.search(r"UpdateEntry\(\s*(NPC_\w+)\s*\)", code)
            if um and case and um.group(1) != case:
                out["swaps"].append({"file": fn, "line": ln, "from": case, "to": um.group(1)})
            if re.search(r"\bbreak\s*;", code):
                case = None
            for m in ENUM_VAL.finditer(code):
                out["enum"].setdefault(m.group(1), int(m.group(2)))
            fm = FUNC_DECL.match(line)
            if fm:
                func = fm.group(1)
            am = ARRAY_DECL.search(code)
            if am and "(" not in code.split("=")[0]:
                array = am.group(1)
            for m in FLOAT_CONST.finditer(code):
                out["constants"].append({"file": fn, "line": ln, "name": m.group(1), "value": float(m.group(2))})
            mr = MOVE_ROW.search(line)
            if mr:
                out["flight"].append({"file": fn, "line": ln, "id": int(mr.group(1)), "to": int(mr.group(2)),
                                      "spell": mr.group(3), "x": float(mr.group(4)), "y": float(mr.group(5)),
                                      "z": float(mr.group(6)), "zGround": float(mr.group(7)),
                                      "comment": (mr.group(8) or "").strip()})
            for m in THRESH.finditer(code):
                out["thresholds"].append({"file": fn, "line": ln, "axis": m.group(1).lower(), "op": m.group(2),
                                          "value": float(m.group(3)), "func": func or "", "comment": comment,
                                          "code": code.strip()[:160]})
            tm = TRIPLE.search(code)
            if tm and not code.strip().startswith(("#", "*")):
                x, y, z = (float(tm.group(i)) for i in (1, 2, 3))
                if abs(x) + abs(y) < 5:          # orientation/rotation quads, not places
                    continue
                cm = CAPS.findall(code)
                call = CALLS.search(code)
                label = None
                if call and cm:
                    label = "%s %s" % (call.group(1), cm[0])
                elif cm:
                    label = cm[0]
                elif call:
                    label = call.group(1)
                elif array:
                    label = array
                else:
                    label = func or "position"
                out["positions"].append({"file": fn, "line": ln, "label": label, "array": array, "func": func,
                                         "x": x, "y": y, "z": z, "comment": comment})
            if code.strip().startswith("};"):
                array = None
    return out


def parse_scope(path):
    text = read_text(path)
    res = {"rows": [], "comments": {}, "source": path, "found": text is not None}
    if not text:
        return res
    consts = {m.group(1): int(m.group(2)) for m in re.finditer(r"\b(k\w+)\s*=\s*(\d+)", text)}
    for m in re.finditer(r"^\s*(\w+),\s*//\s*(.*)$", text, re.M):
        res["comments"][m.group(1)] = m.group(2).strip()
    body = text.split("kScopes[]", 1)[1] if "kScopes[]" in text else ""
    row = re.compile(r'\{\s*(\w+)\s*,\s*"([^"]+)"\s*,\s*\{([^}]*)\}\s*,\s*\{([^}]*)\}\s*,\s*((?:"(?:[^"\\]|\\.)*"\s*)+)\}',
                     re.S)
    for m in row.finditer(body):
        maps = [consts.get(t.strip(), int(t) if t.strip().isdigit() else 0) for t in m.group(3).split(",") if t.strip()]
        entries = [int(t) for t in re.findall(r"\d+", m.group(4))]
        origin = "".join(re.findall(r'"((?:[^"\\]|\\.)*)"', m.group(5)))
        res["rows"].append({"id": m.group(1), "key": m.group(2), "maps": [x for x in maps if x],
                            "entries": entries, "origin": origin, "what": res["comments"].get(m.group(1), "")})
    return res


def parse_core_positions(bbox):
    """Literal float pairs/triples in the SuperUI Core code that fall inside a raid's bounding box."""
    out = []
    if not os.path.isdir(SUI_TREE):
        return out
    num = re.compile(r"(-?\d+\.\d+)f\b")
    for dirpath, _, files in os.walk(SUI_TREE):
        for fn in sorted(files):
            if not fn.endswith((".cpp", ".h")):
                continue
            path = os.path.join(dirpath, fn)
            text = read_text(path) or ""
            for ln, line in enumerate(text.splitlines(), 1):
                code = line.split("//")[0]
                vals = [float(v) for v in num.findall(code)]
                if len(vals) < 2:
                    continue
                for i in range(len(vals) - 1):
                    x, y = vals[i], vals[i + 1]
                    if abs(x) + abs(y) < 20:
                        continue
                    if bbox[0] <= x <= bbox[1] and bbox[2] <= y <= bbox[3]:
                        z = vals[i + 2] if i + 2 < len(vals) else None
                        out.append({"file": os.path.relpath(path, SRC), "line": ln, "x": x, "y": y, "z": z,
                                    "code": code.strip()[:180]})
                        break
    return out


def load_playbook():
    for name in ("sui-raid-playbook.json", "sui-raid-playbook.json.dist"):
        p = os.path.join(ETC, name)
        t = read_text(p)
        if t:
            try:
                return json.loads(t), p
            except ValueError:
                pass
    return {}, None


def playbook_points(pb, entry_key, node, path, out, why_ctx):
    if isinstance(node, dict):
        why = node.get("why") or node.get(path[-1] + "Why" if path else "") or why_ctx
        for k, v in node.items():
            sub_why = node.get(k + "Why") or why
            playbook_points(pb, entry_key, v, path + [k], out, sub_why)
    elif isinstance(node, list) and len(node) == 3 and all(isinstance(v, (int, float)) for v in node):
        out.append({"entry": entry_key, "path": ".".join(path), "x": float(node[0]), "y": float(node[1]),
                    "z": float(node[2]), "why": why_ctx or ""})


def read_entry_file(name, with_value=False):
    t = read_text(os.path.join(ETC, name))
    res = {}
    if t is None:
        return None
    for line in t.split("\n"):
        p = line.split()
        if not p:
            continue
        try:
            e = int(p[0])
        except ValueError:
            continue
        res[e] = float(p[1]) if with_value and len(p) > 1 else True
    return res


def load_learned():
    return {
        "rebounders": read_entry_file("sui-autopilot-rebounders.txt"),
        "risers": read_entry_file("sui-autopilot-risers.txt"),
        "multipliers": read_entry_file("sui-autopilot-multipliers.txt"),
        "pulse": read_entry_file("sui-autopilot-pulses.txt.entries", True),
        "cone": read_entry_file("sui-autopilot-pulses.txt.cones", True),
        "burst": read_entry_file("sui-autopilot-pulses.txt.bursts", True),
        "rhythm": read_entry_file("sui-autopilot-pulses.txt.rhythm", True),
    }


def learned_for(entry, learned):
    out = []
    if (learned.get("rebounders") or {}).get(entry):
        out.append("rebounder")
    if (learned.get("risers") or {}).get(entry):
        out.append("riser")
    if (learned.get("multipliers") or {}).get(entry):
        out.append("multiplier")
    burst = (learned.get("burst") or {}).get(entry)
    if burst:
        out.append("death burst %.0f yd" % burst)
    pulse = (learned.get("pulse") or {}).get(entry)
    if pulse and not burst:
        out.append("pulse %.0f yd" % pulse)
    cone = (learned.get("cone") or {}).get(entry)
    if cone:
        out.append("cone %.0f yd" % cone)
    rh = (learned.get("rhythm") or {}).get(entry)
    if rh:
        out.append("burst rhythm %.1f s" % (rh / 1000.0))
    return out


# ---------------------------------------------------------------------------------------------------------------------
# DB load
# ---------------------------------------------------------------------------------------------------------------------
def load_map(mapid):
    cr = q("select guid,id,position_x,position_y,position_z,orientation,spawntimesecsmin,spawntimesecsmax,"
           "wander_distance,movement_type from creature where map=%d order by guid" % mapid)
    creatures = {}
    for r in cr:
        g = int(r[0])
        creatures[g] = {"guid": g, "entry": int(r[1]), "pos": (float(r[2]), float(r[3]), float(r[4])),
                        "o": float(r[5]), "respawn": (int(r[6]), int(r[7])), "wander": float(r[8]),
                        "mt": int(r[9])}
    guids = ",".join(str(g) for g in creatures) or "0"
    groups = [dict(leader=int(r[0]), member=int(r[1]), dist=float(r[2]), angle=float(r[3]), flags=int(r[4]))
              for r in q("select leader_guid,member_guid,dist,angle,flags from creature_groups where leader_guid in "
                         "(%s) or member_guid in (%s)" % (guids, guids))]
    links = [dict(guid=int(r[0]), master=int(r[1]), flag=int(r[2]))
             for r in q("select guid,master_guid,flag from creature_linking where guid in (%s) or master_guid in (%s)"
                        % (guids, guids))]
    links_t = [dict(entry=int(r[0]), master=int(r[1]), flag=int(r[2]), range=float(r[3]))
               for r in q("select entry,master_entry,flag,search_range from creature_linking_template where map=%d"
                          % mapid)]
    mv = collections.defaultdict(list)
    for r in q("select id,point,position_x,position_y,position_z,waittime,script_id from creature_movement where id in "
               "(%s) order by id,point" % guids):
        mv[int(r[0])].append({"pt": int(r[1]), "pos": (float(r[2]), float(r[3]), float(r[4])), "wait": int(r[5]),
                              "script": int(r[6] or 0)})
    entries = sorted({c["entry"] for c in creatures.values()})
    elist = ",".join(str(e) for e in entries) or "0"
    mvt = collections.defaultdict(list)
    for r in q("select entry,point,position_x,position_y,position_z,waittime,script_id from creature_movement_template "
               "where entry in (%s) order by entry,point" % elist):
        mvt[int(r[0])].append({"pt": int(r[1]), "pos": (float(r[2]), float(r[3]), float(r[4])), "wait": int(r[5]),
                               "script": int(r[6] or 0)})
    pools = {int(r[0]): {"pool": int(r[1]), "chance": float(r[2]), "description": r[3]}
             for r in q("select guid,pool_entry,chance,description from pool_creature where guid in (%s)" % guids)}
    gos = [dict(guid=int(r[0]), entry=int(r[1]), pos=(float(r[2]), float(r[3]), float(r[4])),
                respawn=int(r[5]), name=r[6] or "?", type=int(r[7] or 0))
           for r in q("select g.guid,g.id,g.position_x,g.position_y,g.position_z,g.spawntimesecsmin,"
                      "(select name from gameobject_template t where t.entry=g.id limit 1),"
                      "(select type from gameobject_template t where t.entry=g.id limit 1) "
                      "from gameobject g where g.map=%d order by g.guid" % mapid)]
    triggers = [dict(id=int(r[0]), name=r[1], pos=(float(r[2]), float(r[3]), float(r[4])))
                for r in q("select id,name,target_position_x,target_position_y,target_position_z from "
                           "areatrigger_teleport where target_map=%d order by id" % mapid)]
    return creatures, groups, links, links_t, mv, mvt, pools, gos, triggers


def load_templates(entries):
    if not entries:
        return {}
    elist = ",".join(str(e) for e in sorted(entries))
    t = {}
    for r in q("select entry,patch,name,level_min,level_max,`rank`,detection_range,call_for_help_range,speed_walk,"
               "speed_run,flags_extra,script_name,ai_name from creature_template where entry in (%s) "
               "order by entry,patch" % elist):
        e = int(r[0])
        t[e] = {"entry": e, "name": r[2], "level": (int(r[3]), int(r[4])), "rank": int(r[5]),
                "detection": float(r[6]), "callForHelp": float(r[7]), "walk": float(r[8]), "run": float(r[9]),
                "flagsExtra": int(r[10]), "script": r[11] or "", "ai": r[12] or ""}
    for r in q("select creature_id,comment from creature_ai_events where creature_id in (%s) order by id" % elist):
        e = int(r[0])
        if e in t and r[1]:
            t[e].setdefault("eventAI", []).append(r[1])
    return t


# ---------------------------------------------------------------------------------------------------------------------
# Fight records
# ---------------------------------------------------------------------------------------------------------------------
def load_fights():
    path = os.path.join(ETC, "sui-autopilot-fights.jsonl")
    recs, bad = [], 0
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    r = json.loads(line)
                except ValueError:
                    bad += 1
                    continue
                recs.append({"t": r.get("t", ""), "map": r.get("map"), "instance": r.get("instance"),
                             "secs": r.get("secs", 0), "target": r.get("target"), "outcome": r.get("outcome"),
                             "deaths": len(r.get("deaths") or []), "alive": r.get("alive"),
                             "members": r.get("memberCount"),
                             "foes": [{"entry": x.get("entry"), "guid": x.get("guid"), "name": x.get("name"),
                                       "firstAt": x.get("firstAt", 0), "unprovoked": bool(x.get("unprovoked")),
                                       "dead": bool(x.get("dead"))} for x in (r.get("foes") or [])]})
    except OSError:
        return None, 0, path
    return recs, bad, path


def days_between(t_old, t_new):
    import datetime
    try:
        a = datetime.datetime.strptime(t_old, "%Y-%m-%d %H:%M:%S")
        b = datetime.datetime.strptime(t_new, "%Y-%m-%d %H:%M:%S")
        return (b - a).total_seconds() / 86400.0
    except ValueError:
        return 1e9


# ---------------------------------------------------------------------------------------------------------------------
# Build one raid
# ---------------------------------------------------------------------------------------------------------------------
def build_raid(raid, tmpl_all, scope, playbook, learned, fights):
    mapid = raid["map"]
    creatures, groups, links, links_t, mv, mvt, pools, gos, triggers = load_map(mapid)
    scripts = parse_scripts(raid["scripts"])
    script_entries = set(scripts["enum"].values())
    pb_raid = (playbook.get("raids") or {}).get(str(mapid)) or {}
    order_entries = [o["entry"] for o in pb_raid.get("order", []) if isinstance(o, dict) and "entry" in o]
    fight_entries = set()
    for r in fights or []:
        if r["map"] == mapid:
            fight_entries.update(f["entry"] for f in r["foes"] if f["entry"])
    need = {c["entry"] for c in creatures.values()} | set(order_entries) | fight_entries | \
        {e for e in script_entries if e < 1000000}
    tmpl = load_templates(need - set(tmpl_all))
    tmpl_all.update(tmpl)
    T = tmpl_all

    def tname(e):
        return T[e]["name"] if e in T else "entry %d" % e

    # script entry swaps (instance_molten_core: a Firelord or Lava Annihilator spawn is a coin flip per instance)
    alts = collections.defaultdict(set)
    swap_src = collections.defaultdict(list)
    for sw in scripts["swaps"]:
        a, b = scripts["enum"].get(sw["from"]), scripts["enum"].get(sw["to"])
        if a and b:
            alts[a].add(b)
            swap_src[a].append("%s:%d" % (sw["file"], sw["line"]))
    missing = {e for s in alts.values() for e in s} - set(T)
    if missing:
        T.update(load_templates(missing))

    def mname(e):
        if alts.get(e):
            return "|".join([tname(e)] + sorted(tname(x) for x in alts[e]))
        return tname(e)

    def is_elite(e):
        return e in T and T[e]["rank"] >= 1

    wide_entries = set()
    for row in scope["rows"]:
        if row["key"] == WIDE_REACH_KEY and mapid in row["maps"]:
            wide_entries.update(row["entries"])

    def reach_of(e):
        if e in wide_entries:
            return WIDE_REACH
        if is_elite(e):
            return ELITE_REACH
        return T[e]["detection"] if e in T else 20.0

    # -- packs ----------------------------------------------------------------------------------------------------
    uf = UnionFind()
    for g in creatures:
        uf.find(g)
    db_linked = set()
    group_meta = collections.defaultdict(list)
    for gr in groups:
        if gr["leader"] in creatures and gr["member"] in creatures:
            uf.union(gr["leader"], gr["member"])
            db_linked.update((gr["leader"], gr["member"]))
            group_meta[gr["leader"]].append(gr)
    for lk in links:
        if lk["guid"] in creatures and lk["master"] in creatures:
            uf.union(lk["guid"], lk["master"])
            db_linked.update((lk["guid"], lk["master"]))
    for lt in links_t:
        masters = [c for c in creatures.values() if c["entry"] == lt["master"]]
        for c in creatures.values():
            if c["entry"] != lt["entry"] or not masters:
                continue
            m = min(masters, key=lambda mm: d3(mm["pos"], c["pos"]))
            if lt["range"] <= 0 or d3(m["pos"], c["pos"]) <= lt["range"]:
                uf.union(c["guid"], m["guid"])
                db_linked.update((c["guid"], m["guid"]))
    # proximity: attach ungrouped, non-patrolling spawns (closest pairs first, so an ungrouped spawn between two DB
    # groups joins the nearer one and never bridges them)
    db_root = {g: uf.find(g) for g in db_linked}
    static = [c for c in creatures.values() if c["mt"] != 2]
    pairs = []
    for i, a in enumerate(static):
        for b in static[i + 1:]:
            d = d3(a["pos"], b["pos"])
            if d <= PROX_LINK and not (a["guid"] in db_linked and b["guid"] in db_linked):
                pairs.append((d, a["guid"], b["guid"]))
    pairs.sort()

    def comp_info(root):
        gl = [g for g in creatures if uf.find(g) == root]
        return {db_root[g] for g in gl if g in db_root}, any(creatures[g]["mt"] == 2 for g in gl)

    for d, ga, gb in pairs:
        ra, rb = uf.find(ga), uf.find(gb)
        if ra == rb:
            continue
        dba, pa = comp_info(ra)
        dbb, pb = comp_info(rb)
        if pa or pb:
            continue        # a component that patrols is not extended by proximity
        if dba and dbb and dba != dbb:
            continue        # never merge two DB groups by proximity
        uf.union(ga, gb)
    comps = collections.defaultdict(list)
    for g in creatures:
        comps[uf.find(g)].append(g)

    objects = {}
    for root, gl in comps.items():
        gl.sort()
        members = []
        for g in gl:
            c = creatures[g]
            members.append({"guid": g, "entry": c["entry"], "name": mname(c["entry"]),
                            "alt": sorted(alts.get(c["entry"], ())),
                            "rank": T[c["entry"]]["rank"] if c["entry"] in T else 0,
                            "pos": [r1(v) for v in c["pos"]], "wander": c["wander"], "mt": c["mt"],
                            "respawn": list(c["respawn"])})
        bosses = [m for m in members if m["rank"] == 3]
        patrollers = [m for m in members if m["mt"] == 2]
        cx = sum(m["pos"][0] for m in members) / len(members)
        cy = sum(m["pos"][1] for m in members) / len(members)
        cz = sum(m["pos"][2] for m in members) / len(members)
        centre = (cx, cy, cz)
        radius = max(d3(centre, m["pos"]) + m["wander"] for m in members)
        elites = sum(1 for m in members if m["rank"] >= 1)
        if bosses:
            kind = "boss"
            oid = "B-" + short_boss_name(bosses[0]["name"])
        elif len(members) == 1 and patrollers:
            kind = "patrol"
            oid = "R%d" % members[0]["guid"]
        else:
            kind = "elite" if elites else "pack"
            oid = "P%d" % gl[0]
        reach = max(reach_of(m["entry"]) for m in members)
        sub = UnionFind()
        for a in members:
            sub.find(a["guid"])
            for b in members:
                if a["guid"] < b["guid"] and d3(a["pos"], b["pos"]) <= SPLIT_LINK:
                    sub.union(a["guid"], b["guid"])
        clusters = collections.defaultdict(list)
        for m in members:
            clusters[sub.find(m["guid"])].append(m)
        gm = []
        for m in members:
            for gr in group_meta.get(m["guid"], []):
                gm.append(gr)
        flags = sorted({gr["flags"] for gr in gm})
        resp = sorted({tuple(m["respawn"]) for m in members})
        objects[oid] = {
            "id": oid, "kind": kind, "name": bosses[0]["name"] if bosses else composition(members),
            "composition": composition(members), "centre": [r1(v) for v in centre], "radius": r1(radius),
            "reach": reach, "elites": elites, "members": members,
            "entries": sorted({m["entry"] for m in members} | {a for m in members for a in m["alt"]}),
            "respawn": [list(r) for r in resp],
            "groupLeaders": sorted({gr["leader"] for gr in gm}),
            "groupFlags": [{"flags": f, "meaning": decode_group_flags(f)} for f in flags],
            "linkedAcross": None,
            "patrolling": bool(patrollers), "routes": [], "pools": [pools[g] for g in gl if g in pools],
        }
        if len(clusters) > 1:
            cl = []
            for k, ms in sorted(clusters.items()):
                ccx = sum(m["pos"][0] for m in ms) / len(ms)
                ccy = sum(m["pos"][1] for m in ms) / len(ms)
                ccz = sum(m["pos"][2] for m in ms) / len(ms)
                cl.append({"guids": [m["guid"] for m in ms], "names": composition(ms),
                           "centre": [r1(ccx), r1(ccy), r1(ccz)]})
            span = max(d3(a["centre"], b["centre"]) for a in cl for b in cl)
            objects[oid]["linkedAcross"] = {"clusters": cl, "span": r1(span)}

    guid_obj = {}
    for o in objects.values():
        for m in o["members"]:
            guid_obj[m["guid"]] = o["id"]

    # -- patrol routes ----------------------------------------------------------------------------------------------
    routes = {}
    for c in creatures.values():
        if c["mt"] != 2:
            continue
        src, pts = None, None
        if mv.get(c["guid"]):
            src, pts = "creature_movement (guid)", mv[c["guid"]]
        elif mvt.get(c["entry"]):
            src, pts = "creature_movement_template (entry)", mvt[c["entry"]]
        rid = "R%d" % c["guid"]
        e = c["entry"]
        if not pts:
            routes[rid] = {"id": rid, "guid": c["guid"], "entry": e, "name": tname(e), "owner": guid_obj[c["guid"]],
                           "source": None, "points": [], "note": "movement_type 2 without waypoints"}
            continue
        poly = [p["pos"] for p in pts]
        n = len(poly)
        open_len = sum(d3(poly[i], poly[i + 1]) for i in range(n - 1))
        close = d3(poly[-1], poly[0]) if n > 1 else 0.0
        # back-and-forth encoded as there-and-back points?
        # back-and-forth: split the cyclic path at its two farthest-apart points; the two arcs (the way out and the
        # way back, closing leg included) stay within 10 yd of each other (Hausdorff, point-to-polyline)
        mirrored = False
        if n >= 3:
            i0, k0 = max(((i, k) for i in range(n) for k in range(i + 1, n)), key=lambda ik: d3(poly[ik[0]], poly[ik[1]]))
            out_leg = poly[i0:k0 + 1]
            back_leg = poly[k0:] + poly[:i0 + 1]
            if len(out_leg) >= 2 and len(back_leg) >= 2:
                so = [(out_leg[i], out_leg[i + 1]) for i in range(len(out_leg) - 1)]
                sb = [(back_leg[i], back_leg[i + 1]) for i in range(len(back_leg) - 1)]
                hd = max(max(route_dist(sb, p)[0] for p in out_leg), max(route_dist(so, p)[0] for p in back_leg))
                mirrored = hd <= 10.0
        if mirrored:
            shape = "back-and-forth along a %.0f yd line (way out and way back within 10 yd)" % (
                (open_len + close) / 2.0)
        elif close < 5.0:
            shape = "closed loop"
        else:
            shape = "cycle: walks %.0f yd straight from the last point back to the first" % close
        length = open_len + close
        t = T.get(e, {})
        run = bool(t.get("flagsExtra", 0) & FLAG_ALWAYS_RUN)
        speed = (RUN_BASE * t.get("run", 1.0)) if run else (WALK_BASE * t.get("walk", 1.0))
        waits = sum(p["wait"] for p in pts) / 1000.0
        loop_secs = length / speed + waits if speed > 0 else 0
        tmpl_too = bool(mvt.get(e)) and src.startswith("creature_movement (guid)")
        segs = [(poly[i], poly[(i + 1) % n]) for i in range(n if close > 0.5 else n - 1)]
        routes[rid] = {
            "id": rid, "guid": c["guid"], "entry": e, "name": tname(e), "owner": guid_obj[c["guid"]],
            "source": src, "templatePathIgnored": tmpl_too,
            "points": [{"pt": p["pt"], "pos": [r1(v) for v in p["pos"]], "wait": p["wait"]} for p in pts],
            "spawn": [r1(v) for v in c["pos"]], "length": r1(length), "shape": shape,
            "speed": r1(speed), "runs": run, "waitSecs": r1(waits), "loopSecs": r1(loop_secs),
            "respawn": list(c["respawn"]), "reach": reach_of(e), "segments": segs,
        }
        objects[guid_obj[c["guid"]]]["routes"].append(rid)
    # template paths for entries whose spawns do not walk them
    unused_tmpl = []
    for e, pts in mvt.items():
        spawns = [c for c in creatures.values() if c["entry"] == e]
        if spawns and not any(c["mt"] == 2 for c in spawns):
            unused_tmpl.append({"entry": e, "name": tname(e), "points": len(pts),
                                "spawnMovementType": sorted({c["mt"] for c in spawns})})

    # -- anchors and areas -----------------------------------------------------------------------------------------
    anchors = []
    if triggers:
        anchors.append({"id": "A-Entrance", "name": "Entrance", "pos": [r1(v) for v in triggers[0]["pos"]],
                        "source": "areatrigger_teleport %d (%s)" % (triggers[0]["id"], triggers[0]["name"])})
    boss_objs = [o for o in objects.values() if o["kind"] == "boss"]
    entry_to_boss = {}
    for o in boss_objs:
        for m in o["members"]:
            if m["rank"] == 3:
                entry_to_boss[m["entry"]] = (o, m)
    pb_bosses = playbook.get("bosses") or {}
    ordered = list(order_entries) + sorted(e for e in entry_to_boss if e not in order_entries)
    npc_by_entry = {v: k for k, v in scripts["enum"].items() if k.startswith("NPC_")}
    script_bosses = {}
    for e in ordered:
        if e in entry_to_boss:
            o, m = entry_to_boss[e]
            anchors.append({"id": "A-" + short_boss_name(m["name"]), "name": m["name"], "pos": m["pos"],
                            "source": "spawn guid %d" % m["guid"], "boss": o["id"]})
            continue
        # a boss without a spawn row: playbook summonAt, else the first script position naming it
        pos, src = None, None
        b = pb_bosses.get(str(e)) or {}
        if isinstance(b.get("summonAt"), list) and len(b["summonAt"]) == 3:
            pos, src = [float(v) for v in b["summonAt"]], "playbook summonAt"
        else:
            npc = npc_by_entry.get(e)
            for p in scripts["positions"]:
                if npc and npc in p["label"]:
                    pos, src = [p["x"], p["y"], p["z"]], "%s:%d" % (p["file"], p["line"])
                    break
        if pos:
            name = tname(e)
            bid = "B-" + short_boss_name(name)
            anchors.append({"id": "A-" + short_boss_name(name), "name": name, "pos": [r1(v) for v in pos],
                            "source": src, "boss": bid})
            script_bosses[bid] = {"id": bid, "kind": "boss", "name": name, "entry": e, "scripted": True,
                                  "centre": [r1(v) for v in pos], "source": src}

    def area_of(p):
        if not anchors:
            return None
        return min(anchors, key=lambda a: d3(a["pos"], p))["id"]

    for o in objects.values():
        o["area"] = area_of(o["centre"])
    for sb in script_bosses.values():
        sb["area"] = area_of(sb["centre"])
    for r in routes.values():
        if r.get("segments"):
            seen = []
            for a, b in r["segments"]:
                for k in range(0, 5):
                    t = k / 5.0
                    p = (a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t)
                    ar = area_of(p)
                    if ar not in seen:
                        seen.append(ar)
            r["areas"] = seen
            r["area"] = area_of(r["spawn"])

    # -- overlays ---------------------------------------------------------------------------------------------------
    pb_pts = []
    raid_entries = set(order_entries) | {c["entry"] for c in creatures.values()}
    for k, b in pb_bosses.items():
        if not k.isdigit() or int(k) not in raid_entries:
            continue
        playbook_points(playbook, int(k), b, [], pb_pts, b.get("why", "") if isinstance(b, dict) else "")
    # only triples that are places in this raid (paladinAura / prePotions are id lists, not coordinates)
    sx = [c["pos"][0] for c in creatures.values()]
    sy = [c["pos"][1] for c in creatures.values()]
    pb_pts = [p for p in pb_pts if min(sx) - 200 <= p["x"] <= max(sx) + 200 and min(sy) - 200 <= p["y"] <= max(sy) + 200]
    for p in pb_pts:
        p["label"] = "%s %s" % (short_boss_name(tname(p["entry"])), p["path"])
        p["area"] = area_of((p["x"], p["y"], p["z"]))

    xs = [c["pos"][0] for c in creatures.values()]
    ys = [c["pos"][1] for c in creatures.values()]
    bbox = (min(xs) - 60, max(xs) + 60, min(ys) - 60, max(ys) + 60)
    core_pos = parse_core_positions(bbox)

    golist = []
    gocount = collections.Counter()
    for g in gos:
        gocount[g["name"]] += 1
        for pat, kind in GO_DRAW:
            if pat in g["name"]:
                golist.append({"guid": g["guid"], "entry": g["entry"], "name": g["name"], "kind": kind,
                               "pos": [r1(v) for v in g["pos"]], "respawn": g["respawn"], "type": g["type"]})
                break
    egg_clusters = []
    eggs = [g for g in golist if g["kind"] == "egg"]
    egg_rule = None
    if eggs:
        # eggs belong to the nearest whelp spawn point of the boss script (an array named *Spawn*), else 10 yd linkage
        mouths = [p for p in scripts["positions"] if p["array"] and "spawn" in p["array"].lower()]
        ec = collections.defaultdict(list)
        if mouths:
            egg_rule = "nearest script whelp spawn point (%s)" % ", ".join(
                "%s:%d" % (m["file"], m["line"]) for m in mouths)
            for e in eggs:
                k = min(range(len(mouths)),
                        key=lambda i: d3((mouths[i]["x"], mouths[i]["y"], mouths[i]["z"]), e["pos"]))
                ec[k].append(e)
        else:
            egg_rule = "single-linkage <= 10 yd"
            euf = UnionFind()
            for i, a in enumerate(eggs):
                euf.find(a["guid"])
                for b in eggs[i + 1:]:
                    if d3(a["pos"], b["pos"]) <= 10.0:
                        euf.union(a["guid"], b["guid"])
            for e in eggs:
                ec[euf.find(e["guid"])].append(e)
        for root, es in sorted(ec.items()):
            cx = sum(e["pos"][0] for e in es) / len(es)
            cy = sum(e["pos"][1] for e in es) / len(es)
            cz = sum(e["pos"][2] for e in es) / len(es)
            rad = max(d3((cx, cy, cz), e["pos"]) for e in es)
            mouth = mouths[root] if mouths else None
            egg_clusters.append({"id": "E%d" % min(e["guid"] for e in es), "count": len(es),
                                 "centre": [r1(cx), r1(cy), r1(cz)], "radius": r1(rad),
                                 "xRange": [r1(min(e["pos"][0] for e in es)), r1(max(e["pos"][0] for e in es))],
                                 "yRange": [r1(min(e["pos"][1] for e in es)), r1(max(e["pos"][1] for e in es))],
                                 "whelpSpawn": [mouth["x"], mouth["y"], mouth["z"]] if mouth else None,
                                 "goType": eggs[0].get("type")})

    # registry rows
    reg_rows = [r for r in scope["rows"] if mapid in r["maps"]]
    for o in list(objects.values()) + list(script_bosses.values()):
        ents = set(o.get("entries") or [o.get("entry")])
        o["tactics"] = [r["key"] for r in reg_rows if r["entries"] and ents & set(r["entries"])]
        o["learned"] = {}
        for e in sorted(ents):
            lf = learned_for(e, learned)
            if lf:
                o["learned"][tname(e)] = lf
    for r in routes.values():
        r["tactics"] = [x["key"] for x in reg_rows if x["entries"] and r["entry"] in x["entries"]]
    map_wide = [r for r in reg_rows if not r["entries"]]

    # -- neighbours, passes-near -----------------------------------------------------------------------------------
    still = [o for o in objects.values() if o["kind"] != "patrol"]
    for o in still:
        o["neighbours"] = []
    for i, a in enumerate(still):
        for b in still[i + 1:]:
            best = None
            for ma in a["members"]:
                if ma["mt"] == 2:
                    continue
                for mb in b["members"]:
                    if mb["mt"] == 2:
                        continue
                    d = d3(ma["pos"], mb["pos"])
                    if best is None or d < best[0]:
                        best = (d, ma, mb)
            if best and best[0] <= NEIGHBOUR:
                d, ma, mb = best
                ra, rb = reach_of(ma["entry"]), reach_of(mb["entry"])
                a["neighbours"].append({"id": b["id"], "dist": r1(d), "from": ma["guid"], "to": mb["guid"],
                                        "insideReach": d <= max(ra, rb)})
                b["neighbours"].append({"id": a["id"], "dist": r1(d), "from": mb["guid"], "to": ma["guid"],
                                        "insideReach": d <= max(ra, rb)})
    for o in still:
        o["neighbours"].sort(key=lambda n: n["dist"])

    camp_pts = [p for p in pb_pts]
    for r in routes.values():
        r["passesNear"] = []
        if not r.get("segments"):
            continue
        own = r["owner"]
        for o in objects.values():
            if o["id"] == own:
                continue
            best = None
            for m in o["members"]:
                if m["mt"] == 2:
                    continue
                d, si = route_dist(r["segments"], m["pos"])
                if best is None or d < best[0]:
                    best = (d, si, m)
            if not best:
                continue
            d, si, m = best
            reach = reach_of(m["entry"])
            if d <= reach + NEAR_MARGIN:
                r["passesNear"].append({"id": o["id"], "kind": o["kind"], "dist": r1(d), "member": m["guid"],
                                        "memberName": m["name"], "at": m["pos"], "segment": si,
                                        "reach": reach, "insideReach": d <= reach})
        for p in camp_pts:
            d, si = route_dist(r["segments"], (p["x"], p["y"], p["z"]))
            if d <= r["reach"] + NEAR_MARGIN:
                r["passesNear"].append({"id": "playbook:" + p["label"], "kind": "playbook", "dist": r1(d),
                                        "at": [p["x"], p["y"], p["z"]], "segment": si, "reach": r["reach"],
                                        "insideReach": d <= r["reach"],
                                        "ownBoss": r["owner"] == "B-" + short_boss_name(tname(p["entry"]))})
        r["passesNear"].sort(key=lambda x: x["dist"])
    # back-reference: which routes pass each object
    for o in objects.values():
        o["passedBy"] = []
    for r in routes.values():
        for pn in r["passesNear"]:
            if pn["id"] in objects:
                objects[pn["id"]]["passedBy"].append({"route": r["id"], "owner": r["owner"], "dist": pn["dist"],
                                                      "insideReach": pn["insideReach"]})
    for o in objects.values():
        o["passedBy"].sort(key=lambda x: x["dist"])

    # playbook spots: nearest idle elites
    for p in pb_pts:
        near = []
        for o in still:
            best = None
            for m in o["members"]:
                if m["mt"] == 2:
                    continue
                d = d3(m["pos"], (p["x"], p["y"], p["z"]))
                if best is None or d < best[0]:
                    best = (d, m)
            if best and best[0] <= reach_of(best[1]["entry"]) + NEAR_MARGIN + 20:
                near.append({"id": o["id"], "dist": r1(best[0]), "member": best[1]["guid"],
                             "insideReach": best[0] <= reach_of(best[1]["entry"])})
        p["nearObjects"] = sorted(near, key=lambda x: x["dist"])[:8]
        p["routesNear"] = sorted([{"route": r["id"], "owner": r["owner"], "dist": pn["dist"]}
                                  for r in routes.values() for pn in r["passesNear"]
                                  if pn["id"] == "playbook:" + p["label"]], key=lambda x: x["dist"])

    # -- fights -----------------------------------------------------------------------------------------------------
    fight_info = {"available": fights is not None, "records": 0, "attached": 0, "unattached": 0,
                  "entryMismatch": 0, "latest": None, "byOutcome": {}}
    summon_owner = {}
    for e in fight_entries:
        if e in T and T[e]["rank"] == 3 and "B-" + short_boss_name(T[e]["name"]) in script_bosses:
            summon_owner[e] = "B-" + short_boss_name(T[e]["name"])
    for bid, sb in script_bosses.items():
        summon_owner.setdefault(sb["entry"], bid)
    # script adds of a scripted boss (Majordomo's Flamewaker Elite/Healer, Sons of Flame) by the enum + positions
    for p in scripts["positions"]:
        for npc, ent in scripts["enum"].items():
            if npc in p["label"] and ent not in summon_owner and ent not in {c["entry"] for c in creatures.values()}:
                near = min(script_bosses.values(), key=lambda sb: d3(sb["centre"], (p["x"], p["y"], p["z"])),
                           default=None)
                if near and d3(near["centre"], (p["x"], p["y"], p["z"])) < 80:
                    summon_owner[ent] = near["id"]
    tallies = collections.defaultdict(lambda: {"fights": 0, "win": 0, "wipe": 0, "other": 0, "deaths": 0,
                                               "recentFights": 0, "recentWin": 0, "recentWipe": 0,
                                               "joinedBy": collections.Counter(),
                                               "joinedByUnprovoked": collections.Counter(),
                                               "joinedByWipe": collections.Counter(),
                                               "joinedInto": collections.Counter(),
                                               "joinedOthers": 0, "joinedOthersUnprovoked": 0,
                                               "summoned": collections.Counter(), "last": None})
    if fights:
        mine = [r for r in fights if r["map"] == mapid]
        fight_info["records"] = len(mine)
        latest = max((r["t"] for r in mine), default=None)
        fight_info["latest"] = latest
        fight_info["byOutcome"] = dict(collections.Counter(r["outcome"] for r in mine))
        for r in mine:
            mapped = []
            for f in r["foes"]:
                g = f["guid"]
                if g in creatures and (creatures[g]["entry"] == f["entry"] or
                                       f["entry"] in alts.get(creatures[g]["entry"], ())):
                    mapped.append((guid_obj[g], f))
                elif f["entry"] in summon_owner:
                    mapped.append((summon_owner[f["entry"]], f))
                elif g in creatures:
                    fight_info["entryMismatch"] += 1   # a summoned foe whose runtime guid reuses a spawn guid
            if not mapped:
                fight_info["unattached"] += 1
                continue
            cand = [x for x in mapped if x[1]["entry"] == r["target"]] or \
                [x for x in mapped if not x[1]["unprovoked"]] or mapped
            primary = min(cand, key=lambda x: (x[1]["firstAt"], x[1]["guid"]))[0]
            fight_info["attached"] += 1
            tl = tallies[primary]
            tl["fights"] += 1
            oc = r["outcome"] if r["outcome"] in ("win", "wipe") else "other"
            tl[oc] += 1
            tl["deaths"] += r["deaths"]
            recent = latest and days_between(r["t"], latest) <= RECENT_DAYS
            if recent:
                tl["recentFights"] += 1
                if oc in ("win", "wipe"):
                    tl["recent" + oc.capitalize()] += 1
            if not tl["last"] or r["t"] > tl["last"]["t"]:
                tl["last"] = {"t": r["t"], "outcome": r["outcome"], "deaths": r["deaths"], "secs": r["secs"]}
            others = {}
            for oid, f in mapped:
                if oid != primary:
                    others.setdefault(oid, False)
                    others[oid] = others[oid] or f["unprovoked"]
            for oid, unprov in others.items():
                tl["joinedBy"][oid] += 1
                if unprov:
                    tl["joinedByUnprovoked"][oid] += 1
                if oc == "wipe":
                    tl["joinedByWipe"][oid] += 1
                tallies[oid]["joinedOthers"] += 1
                tallies[oid]["joinedInto"][primary] += 1
                if unprov:
                    tallies[oid]["joinedOthersUnprovoked"] += 1
            mapped_ids = {id(f) for _, f in mapped}
            for f in r["foes"]:
                if id(f) not in mapped_ids:
                    tl["summoned"][f["name"] or str(f["entry"])] += 1
    tallies_out = {}
    for oid, tl in tallies.items():
        tallies_out[oid] = {k: (dict(v.most_common()) if isinstance(v, collections.Counter) else v)
                            for k, v in tl.items()}
    for o in list(objects.values()) + list(script_bosses.values()):
        o["fights"] = tallies_out.get(o["id"])
    for r in routes.values():
        r["fightsOwner"] = r["owner"]

    # -- hazards ----------------------------------------------------------------------------------------------------
    hazards = []
    for r in routes.values():
        for pn in r["passesNear"]:
            if pn["insideReach"] and not pn.get("ownBoss"):   # a lure beside its own boss's walk is by design
                tgt = pn["id"]
                score = 100 - pn["dist"] + (30 if tgt.startswith("B-") or tgt.startswith("playbook") else 0) + \
                    (15 if (objects.get(tgt) or {}).get("elites", 0) >= 3 else 0)
                hazards.append({"score": r1(score), "type": "route-inside-reach",
                                "text": "%s (%s, %s) passes %.0f yd from %s %s at %s - inside its %.0f yd reach" % (
                                    r["id"], r["name"], "owned by " + r["owner"] if r["owner"] != r["id"] else "lone",
                                    pn["dist"], pn["id"], ("(" + objects[tgt]["composition"] + ")")
                                    if tgt in objects else "", fmt_xyz(pn["at"]), pn["reach"]),
                                "ids": [r["id"], r["owner"], tgt]})
    for o in objects.values():
        la = o.get("linkedAcross")
        if la and la["span"] > SPLIT_LINK:
            hazards.append({"score": r1(60 + la["span"]), "type": "linked-across",
                            "text": "%s is ONE DB group (%s) split into %d clusters %.0f yd apart: %s" % (
                                o["id"], ", ".join(g["meaning"] for g in o["groupFlags"]) or "linked",
                                len(la["clusters"]), la["span"],
                                "; ".join("%s at %s" % (c["names"], fmt_xyz(c["centre"])) for c in la["clusters"])),
                            "ids": [o["id"]]})
    seen_pairs = set()
    for o in still:
        for n in o["neighbours"]:
            if not n["insideReach"]:
                continue
            key = tuple(sorted((o["id"], n["id"])))
            if key in seen_pairs:
                continue
            seen_pairs.add(key)
            other = objects[n["id"]]
            if not (o["elites"] and other["elites"]):
                continue
            score = 70 - n["dist"] + (25 if "B-" in key[0] + key[1] else 0)
            hazards.append({"score": r1(score), "type": "idle-neighbours",
                            "text": "%s (%s) and %s (%s) stand %.0f yd apart (%d -> %d): inside idle reach" % (
                                o["id"], o["composition"], n["id"], other["composition"], n["dist"], n["from"],
                                n["to"]), "ids": [o["id"], n["id"]]})
    for p in pb_pts:
        for no in p["nearObjects"]:
            if no["insideReach"] and no["id"] not in ("B-" + short_boss_name(tname(p["entry"])),):
                hazards.append({"score": r1(80 - no["dist"]), "type": "spot-inside-reach",
                                "text": "playbook %s %s is %.0f yd from %s (%s) - inside its idle reach" % (
                                    p["label"], fmt_xyz((p["x"], p["y"], p["z"])), no["dist"], no["id"],
                                    objects[no["id"]]["composition"]), "ids": [no["id"]]})
        for rn in p["routesNear"]:
            if rn["owner"] != "B-" + short_boss_name(tname(p["entry"])):
                hazards.append({"score": r1(75 - rn["dist"]), "type": "spot-on-route",
                                "text": "playbook %s %s is %.0f yd from patrol %s (%s)" % (
                                    p["label"], fmt_xyz((p["x"], p["y"], p["z"])), rn["dist"], rn["route"],
                                    routes[rn["route"]]["name"]), "ids": [rn["route"]]})
    for oid, tl in tallies_out.items():
        def comp_of(x):
            ob = objects.get(x) or script_bosses.get(x) or {}
            return ob.get("composition") or ob.get("name") or "?"

        for jid, n in tl["joinedBy"].items():
            if n >= 3:
                hazards.append({"score": r1(40 + 3 * n + 2 * tl["joinedByWipe"].get(jid, 0)), "type": "fight-joiner",
                                "text": "fight records: %s (%s) joined %d of %d fights on %s (%s); %d of those were "
                                        "wipes, %d times it struck first (unprovoked)" % (
                                            jid, comp_of(jid), n, tl["fights"], oid, comp_of(oid),
                                            tl["joinedByWipe"].get(jid, 0), tl["joinedByUnprovoked"].get(jid, 0)),
                                "ids": [oid, jid]})
    for o in objects.values():
        fast = [m for m in o["members"] if m["respawn"][0] < 7200 and m["rank"] >= 1]
        if fast:
            hazards.append({"score": r1(30 + 3000.0 / max(60, fast[0]["respawn"][0])), "type": "fast-respawn",
                            "text": "%s (%s) respawns every %s" % (o["id"], o["composition"],
                                                                   fmt_secs(fast[0]["respawn"][0])),
                            "ids": [o["id"]]})
    if alts:
        swapped = [c for c in creatures.values() if c["entry"] in alts]
        hazards.append({"score": 95.0, "type": "random-entry",
                        "text": "%d spawns are a coin flip per instance (%s): %s. Pack composition, Lava Spawn "
                                "splitting (Firelord) and every tactic keyed on these entries change per lockout; "
                                "the map shows both names" % (
                                    len(swapped), ", ".join(sorted({s for v in swap_src.values() for s in v})),
                                    "; ".join("%s -> %s" % (tname(a), "/".join(tname(b) for b in sorted(bs)))
                                              for a, bs in sorted(alts.items()))),
                        "ids": sorted({guid_obj[c["guid"]] for c in swapped})})
    hazards.sort(key=lambda h: -h["score"])

    # -- Onyxia-style script extras ---------------------------------------------------------------------------------
    flight = scripts["flight"]
    for f in flight:
        f["breath"] = f["spell"].replace("SPELL_BREATH_", "").replace("_TO_", " -> ").replace("_", " ")
        f["area"] = area_of((f["x"], f["y"], f["z"]))
    for p in scripts["positions"]:
        p["area"] = area_of((p["x"], p["y"], p["z"]))

    # entries table
    counts = collections.Counter(c["entry"] for c in creatures.values())
    entries_tbl = []
    for e in sorted(set(counts) | set(script_bosses[b]["entry"] for b in script_bosses)):
        t = T.get(e, {})
        resp = sorted({c["respawn"] for c in creatures.values() if c["entry"] == e})
        entries_tbl.append({"entry": e, "name": mname(e), "rank": t.get("rank"), "count": counts.get(e, 0),
                            "respawn": [list(x) for x in resp], "detection": t.get("detection"),
                            "callForHelp": t.get("callForHelp"), "reach": reach_of(e),
                            "speedWalk": t.get("walk"), "speedRun": t.get("run"),
                            "alwaysRun": bool(t.get("flagsExtra", 0) & FLAG_ALWAYS_RUN),
                            "script": t.get("script"), "ai": t.get("ai"), "eventAI": t.get("eventAI", []),
                            "learned": learned_for(e, learned),
                            "tactics": [r["key"] for r in reg_rows if e in r["entries"]]})

    wanderers = []
    for c in creatures.values():
        if c["mt"] == 1 and c["wander"] > 0:
            wanderers.append({"guid": c["guid"], "entry": c["entry"], "name": tname(c["entry"]),
                              "pos": [r1(v) for v in c["pos"]], "radius": c["wander"], "object": guid_obj[c["guid"]]})

    for r in routes.values():
        r.pop("segments", None)

    return {
        "map": mapid, "key": raid["key"], "name": raid["name"],
        "rules": {"proxLink": PROX_LINK, "eliteReach": ELITE_REACH, "wideReach": WIDE_REACH,
                  "wideReachEntries": sorted(wide_entries), "nearMargin": NEAR_MARGIN, "neighbour": NEIGHBOUR,
                  "splitLink": SPLIT_LINK, "recentDays": RECENT_DAYS},
        "anchors": anchors, "objects": objects, "scriptBosses": script_bosses, "routes": routes,
        "unusedTemplatePaths": unused_tmpl, "wanderers": wanderers, "entries": entries_tbl,
        "entrySwaps": {str(a): {"to": sorted(b), "source": swap_src[a]} for a, b in alts.items()},
        "playbook": pb_pts, "registry": reg_rows, "registryMapWide": [r["key"] for r in map_wide],
        "scripts": {"positions": scripts["positions"], "thresholds": scripts["thresholds"],
                    "constants": scripts["constants"], "flight": flight, "enum": scripts["enum"],
                    "dir": os.path.relpath(raid["scripts"], SRC)},
        "corePositions": core_pos, "gameobjects": golist, "gameobjectCounts": dict(gocount),
        "eggClusters": egg_clusters, "eggRule": egg_rule, "fights": fight_info, "hazards": hazards,
        "counts": {"spawns": len(creatures), "objects": len(objects),
                   "packs": sum(1 for o in objects.values() if o["kind"] in ("elite", "pack")),
                   "elitePacks": sum(1 for o in objects.values() if o["kind"] == "elite"),
                   "bosses": sum(1 for o in objects.values() if o["kind"] == "boss") + len(script_bosses),
                   "lonePatrols": sum(1 for o in objects.values() if o["kind"] == "patrol"),
                   "routes": len(routes), "wanderers": len(wanderers), "playbookSpots": len(pb_pts),
                   "registryRows": len(reg_rows), "scriptPositions": len(scripts["positions"]),
                   "corePositions": len(core_pos), "gameobjectsDrawn": len(golist)},
    }


# ---------------------------------------------------------------------------------------------------------------------
# SVG
# ---------------------------------------------------------------------------------------------------------------------
def esc(s):
    return str(s).replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace('"', "&quot;")


def render_svg(R):
    pts = []
    for o in R["objects"].values():
        for m in o["members"]:
            pts.append(m["pos"])
    for r in R["routes"].values():
        for p in r["points"]:
            pts.append(p["pos"])
    for p in R["playbook"]:
        pts.append((p["x"], p["y"], p["z"]))
    for p in R["scripts"]["positions"]:
        pts.append((p["x"], p["y"], p["z"]))
    for g in R["gameobjects"]:
        pts.append(g["pos"])
    for a in R["anchors"]:
        pts.append(a["pos"])
    # screen: sx = -y, sy = -x (north up)
    minsx = min(-p[1] for p in pts) - 45
    maxsx = max(-p[1] for p in pts) + 45
    minsy = min(-p[0] for p in pts) - 45
    maxsy = max(-p[0] for p in pts) + 45
    W = 1600.0
    s = W / max(maxsx - minsx, maxsy - minsy)
    LEG = 250
    width = (maxsx - minsx) * s + LEG
    height = max((maxsy - minsy) * s, 700)
    fs = 11  # base font px

    def X(p):
        return ((-p[1]) - minsx) * s

    def Y(p):
        return ((-p[0]) - minsy) * s

    o = []
    o.append('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 %.0f %.0f" width="%.0f" height="%.0f" '
             'font-family="Segoe UI, Helvetica, Arial, sans-serif">' % (width, height, width, height))
    o.append('<title>%s (map %d) - generated raid map</title>' % (esc(R["name"]), R["map"]))
    o.append('<rect x="0" y="0" width="%.0f" height="%.0f" fill="#fbfaf7"/>' % (width, height))
    # grid every 50 yd
    step = 50
    gx0 = int(math.floor(-maxsy / step)) * step
    gx1 = int(math.ceil(-minsy / step)) * step
    gy0 = int(math.floor(-maxsx / step)) * step
    gy1 = int(math.ceil(-minsx / step)) * step
    for gx in range(gx0, gx1 + 1, step):      # game x lines -> horizontal
        yy = Y((gx, 0, 0))
        if 0 <= yy <= height:
            o.append('<line x1="0" y1="%.1f" x2="%.1f" y2="%.1f" stroke="#e6e2da" stroke-width="1"/>' %
                     (yy, width - LEG, yy))
            o.append('<text x="3" y="%.1f" font-size="9" fill="#9a958c">x %d</text>' % (yy - 2, gx))
    for gy in range(gy0, gy1 + 1, step):      # game y lines -> vertical
        xx = X((0, gy, 0))
        if 0 <= xx <= width - LEG:
            o.append('<line x1="%.1f" y1="0" x2="%.1f" y2="%.1f" stroke="#e6e2da" stroke-width="1"/>' %
                     (xx, xx, height))
            o.append('<text x="%.1f" y="10" font-size="9" fill="#9a958c">y %d</text>' % (xx + 2, gy))
    # area anchors
    for a in R["anchors"]:
        o.append('<text x="%.1f" y="%.1f" font-size="%d" fill="#d5cfc4" font-weight="bold" text-anchor="middle">'
                 '%s</text>' % (X(a["pos"]), Y(a["pos"]) - 30, fs + 9, esc(a["id"])))
    # Onyxia-style thresholds (x lines)
    for t in R["scripts"]["thresholds"]:
        if t["axis"] == "x":
            yy = Y((t["value"], 0, 0))
            o.append('<line x1="0" y1="%.1f" x2="%.1f" y2="%.1f" stroke="%s" stroke-dasharray="8 5" stroke-width="1.2"/>'
                     % (yy, width - LEG, yy, COLORS["threshold"]))
            o.append('<text x="%.1f" y="%.1f" font-size="%d" fill="%s">x %s %.0f (%s)</text>' % (
                width - LEG - 330, yy - 3, fs - 1, COLORS["threshold"], esc(t["op"]), t["value"], esc(t["func"])))
    # reach circles
    for ob in R["objects"].values():
        if ob["kind"] == "patrol":
            continue
        c = ob["centre"]
        col = COLORS["boss"] if ob["kind"] == "boss" else COLORS["elite"] if ob["kind"] == "elite" else COLORS["pack"]
        o.append('<circle cx="%.1f" cy="%.1f" r="%.1f" fill="%s" fill-opacity="0.06" stroke="%s" stroke-opacity="0.45" '
                 'stroke-dasharray="4 3" stroke-width="1"/>' % (X(c), Y(c), (ob["radius"] + ob["reach"]) * s, col, col))
    # wander circles
    for w in R["wanderers"]:
        o.append('<circle cx="%.1f" cy="%.1f" r="%.1f" fill="none" stroke="%s" stroke-opacity="0.5" stroke-width="0.8" '
                 'stroke-dasharray="1.5 2"/>' % (X(w["pos"]), Y(w["pos"]), w["radius"] * s, COLORS["wander"]))
    # egg clusters
    for e in R["eggClusters"]:
        bx0, bx1 = e["xRange"]
        by0, by1 = e["yRange"]
        o.append('<rect x="%.1f" y="%.1f" width="%.1f" height="%.1f" fill="%s" fill-opacity="0.05" stroke="%s" '
                 'stroke-opacity="0.6" stroke-dasharray="3 3" stroke-width="1"/>' % (
                     X((0, by1)), Y((bx1, 0)), (by1 - by0) * s, (bx1 - bx0) * s, COLORS["egg"], COLORS["egg"]))
        if e.get("whelpSpawn"):
            o.append('<line x1="%.1f" y1="%.1f" x2="%.1f" y2="%.1f" stroke="%s" stroke-width="1" stroke-opacity="0.6"/>'
                     % (X(e["centre"]), Y(e["centre"]), X(e["whelpSpawn"]), Y(e["whelpSpawn"]), COLORS["egg"]))
        o.append('<text x="%.1f" y="%.1f" font-size="%d" fill="#5d4037" text-anchor="middle">%s: %d eggs</text>' % (
            X(e["centre"]), Y(e["centre"]) + 4, fs, esc(e["id"]), e["count"]))
    # Deep Breath lines
    fl = {f["id"]: f for f in R["scripts"]["flight"]}
    for f in R["scripts"]["flight"]:
        to = fl.get(f["to"])
        if not to:
            continue
        x1, y1, x2, y2 = X((f["x"], f["y"])), Y((f["x"], f["y"])), X((to["x"], to["y"])), Y((to["x"], to["y"]))
        o.append('<line x1="%.1f" y1="%.1f" x2="%.1f" y2="%.1f" stroke="%s" stroke-opacity="0.35" stroke-width="2" '
                 'stroke-dasharray="10 6" marker-end="url(#breath)"/>' % (x1, y1, x2, y2, COLORS["breath"]))
    # gameobjects
    for g in R["gameobjects"]:
        if g["kind"] == "egg":
            o.append('<circle cx="%.1f" cy="%.1f" r="1.6" fill="%s"/>' % (X(g["pos"]), Y(g["pos"]), COLORS["egg"]))
        elif g["kind"] in ("fissure", "lavatrap"):
            o.append('<circle cx="%.1f" cy="%.1f" r="2.2" fill="%s" fill-opacity="%s"/>' % (
                X(g["pos"]), Y(g["pos"]), COLORS["fissure"], "0.7" if g["kind"] == "fissure" else "0.35"))
        else:
            o.append('<rect x="%.1f" y="%.1f" width="7" height="7" fill="%s" transform="rotate(45 %.1f %.1f)"/>' % (
                X(g["pos"]) - 3.5, Y(g["pos"]) - 3.5, COLORS["go"], X(g["pos"]), Y(g["pos"])))
            o.append('<text x="%.1f" y="%.1f" font-size="%d" fill="#34495e">%s</text>' % (
                X(g["pos"]) + 6, Y(g["pos"]) + 3, fs - 2, esc(g["name"].replace("Molten Core Circle ", "Circle "))))
    # members + pack circles
    for ob in R["objects"].values():
        col = COLORS["boss"] if ob["kind"] == "boss" else COLORS["elite"] if ob["kind"] == "elite" else \
            COLORS["patrol"] if ob["kind"] == "patrol" else COLORS["pack"]
        if ob["kind"] != "patrol":
            c = ob["centre"]
            o.append('<circle cx="%.1f" cy="%.1f" r="%.1f" fill="%s" fill-opacity="0.18" stroke="%s" stroke-width="1.3"/>'
                     % (X(c), Y(c), max(ob["radius"], 2.5) * s, col, col))
        for m in ob["members"]:
            rr = 3.2 if m["rank"] >= 1 else 2.0
            if m["rank"] == 3:
                rr = 6
            o.append('<circle cx="%.1f" cy="%.1f" r="%.1f" fill="%s"><title>%d %s</title></circle>' % (
                X(m["pos"]), Y(m["pos"]), rr, col, m["guid"], esc(m["name"])))
    # routes
    o.append('<defs><marker id="breath" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" '
             'orient="auto"><path d="M0,0 L10,5 L0,10 z" fill="%s" fill-opacity="0.5"/></marker></defs>'
             % COLORS["breath"])
    for r in R["routes"].values():
        if not r["points"]:
            continue
        poly = [p["pos"] for p in r["points"]]
        closed = "closed" in r["shape"] or "cycle" in r["shape"]
        col = COLORS["boss"] if r["owner"].startswith("B-") else COLORS["patrol"]
        path = " ".join("%.1f,%.1f" % (X(p), Y(p)) for p in poly + ([poly[0]] if closed else []))
        o.append('<polyline points="%s" fill="none" stroke="%s" stroke-width="1.8" stroke-opacity="0.85"/>' % (path, col))
        seq = poly + ([poly[0]] if closed else [])
        for i in range(len(seq) - 1):
            a, b = seq[i], seq[i + 1]
            ax, ay, bx, by = X(a), Y(a), X(b), Y(b)
            L = math.hypot(bx - ax, by - ay)
            if L < 14:
                continue
            mx, my = (ax + bx) / 2, (ay + by) / 2
            ux, uy = (bx - ax) / L, (by - ay) / L
            tip = (mx + ux * 5, my + uy * 5)
            l1 = (mx - ux * 4 - uy * 3.5, my - uy * 4 + ux * 3.5)
            l2 = (mx - ux * 4 + uy * 3.5, my - uy * 4 - ux * 3.5)
            o.append('<polygon points="%.1f,%.1f %.1f,%.1f %.1f,%.1f" fill="%s"/>' % (
                tip[0], tip[1], l1[0], l1[1], l2[0], l2[1], col))
        sp = r["spawn"]
        o.append('<text x="%.1f" y="%.1f" font-size="%d" fill="%s" font-weight="bold">%s</text>' % (
            X(sp) + 5, Y(sp) + 12, fs, col, esc(r["id"] if r["owner"] == r["id"] else r["id"])))
    # script positions
    for p in R["scripts"]["positions"]:
        x, y = X((p["x"], p["y"])), Y((p["x"], p["y"]))
        o.append('<path d="M%.1f,%.1f l6,6 M%.1f,%.1f l-6,6" stroke="%s" stroke-width="1.6"/>' % (
            x - 3, y - 3, x + 3, y - 3, COLORS["script"]))
    for f in R["scripts"]["flight"]:
        o.append('<text x="%.1f" y="%.1f" font-size="%d" fill="%s">fp%d %s</text>' % (
            X((f["x"], f["y"])) + 6, Y((f["x"], f["y"])) - 4, fs - 1, COLORS["script"], f["id"], esc(f["breath"])))
    labelled = set()
    for p in R["scripts"]["positions"]:
        if p["label"] in labelled or p["file"].startswith("boss_onyxia") and p["array"] == "aMoveData":
            continue
        labelled.add(p["label"])
        o.append('<text x="%.1f" y="%.1f" font-size="%d" fill="%s">%s</text>' % (
            X((p["x"], p["y"])) + 6, Y((p["x"], p["y"])) + 10, fs - 2, COLORS["script"],
            esc(p["label"].replace("SummonCreature ", "summon ").replace("NPC_", ""))))
    # core positions
    for p in R["corePositions"]:
        x, y = X((p["x"], p["y"])), Y((p["x"], p["y"]))
        o.append('<rect x="%.1f" y="%.1f" width="8" height="8" fill="none" stroke="%s" stroke-width="2"/>' % (
            x - 4, y - 4, COLORS["core"]))
        o.append('<text x="%.1f" y="%.1f" font-size="%d" fill="%s">core %s:%d</text>' % (
            x + 7, y - 5, fs - 2, COLORS["core"], esc(os.path.basename(p["file"])), p["line"]))
    # playbook
    for p in R["playbook"]:
        x, y = X((p["x"], p["y"])), Y((p["x"], p["y"]))
        o.append('<path d="M%.1f,%.1f l7,7 l-7,7 l-7,-7 z" fill="%s" stroke="#fff" stroke-width="1"/>' % (
            x, y - 7, COLORS["playbook"]))
        o.append('<text x="%.1f" y="%.1f" font-size="%d" fill="%s" font-weight="bold">%s</text>' % (
            x + 9, y + 4, fs, COLORS["playbook"], esc(p["label"])))
    # object labels last
    for ob in R["objects"].values():
        if ob["kind"] == "patrol":
            continue
        c = ob["centre"]
        col = "#7b241c" if ob["kind"] == "boss" else "#6e2c00" if ob["kind"] == "elite" else "#4d5656"
        size = fs + 3 if ob["kind"] == "boss" else fs if ob["kind"] == "elite" else fs - 2
        o.append('<text x="%.1f" y="%.1f" font-size="%d" fill="%s" font-weight="%s" text-anchor="middle" '
                 'stroke="#fbfaf7" stroke-width="2.5" paint-order="stroke">%s</text>' % (
                     X(c), Y(c) - max(ob["radius"], 2.5) * s - 3, size, col,
                     "bold" if ob["kind"] in ("boss", "elite") else "normal", esc(ob["id"])))
    for sb in R["scriptBosses"].values():
        c = sb["centre"]
        o.append('<circle cx="%.1f" cy="%.1f" r="7" fill="none" stroke="%s" stroke-width="2.5"/>' % (
            X(c), Y(c), COLORS["boss"]))
        o.append('<text x="%.1f" y="%.1f" font-size="%d" fill="#7b241c" font-weight="bold" text-anchor="middle" '
                 'stroke="#fbfaf7" stroke-width="2.5" paint-order="stroke">%s (script)</text>' % (
                     X(c), Y(c) - 10, fs + 3, esc(sb["id"])))
    # legend
    lx = width - LEG + 12
    ly = 20
    o.append('<rect x="%.1f" y="0" width="%d" height="%.0f" fill="#f3f0ea"/>' % (width - LEG, LEG, height))
    o.append('<text x="%.1f" y="%.1f" font-size="15" font-weight="bold" fill="#1b1b1b">%s</text>' % (
        lx, ly, esc(R["name"])))
    ly += 18
    o.append('<text x="%.1f" y="%.1f" font-size="10" fill="#444">map %d - north up, 50 yd grid</text>' % (
        lx, ly, R["map"]))
    ly += 14
    o.append('<text x="%.1f" y="%.1f" font-size="10" fill="#444">generated: tools/raidmap/regen.sh</text>' % (lx, ly))
    ly += 22
    items = [("boss", "boss + linked adds"), ("elite", "elite pack"), ("pack", "non-elite pack"),
             ("patrol", "patrol route (arrows = direction)")]
    for k, label in items:
        if k == "patrol":
            o.append('<line x1="%.1f" y1="%.1f" x2="%.1f" y2="%.1f" stroke="%s" stroke-width="2"/>' % (
                lx, ly - 4, lx + 18, ly - 4, COLORS[k]))
        else:
            o.append('<circle cx="%.1f" cy="%.1f" r="6" fill="%s" fill-opacity="0.3" stroke="%s"/>' % (
                lx + 9, ly - 4, COLORS[k], COLORS[k]))
        o.append('<text x="%.1f" y="%.1f" font-size="11" fill="#222">%s</text>' % (lx + 26, ly, esc(label)))
        ly += 18
    extra = [("dashed ring = pack radius + idle reach", COLORS["elite"]),
             ("dotted ring = wander radius", COLORS["wander"]),
             ("purple diamond = playbook lure/camp/spot", COLORS["playbook"]),
             ("green x = boss-script position", COLORS["script"]),
             ("gold square = Core hard-coded position", COLORS["core"]),
             ("grey diamond = gameobject", COLORS["go"])]
    if R["eggClusters"]:
        extra.append(("brown box = Onyxia egg cluster (line to its whelp spawn)", COLORS["egg"]))
    if R["scripts"]["flight"]:
        extra.append(("red dashed arrow = Deep Breath path", COLORS["breath"]))
    if R["scripts"]["thresholds"]:
        extra.append(("red dashed line = script x threshold", COLORS["threshold"]))
    for label, col in extra:
        o.append('<text x="%.1f" y="%.1f" font-size="10.5" fill="%s">%s</text>' % (lx, ly, col, esc(label)))
        ly += 16
    ly += 10
    c = R["counts"]
    for line in ["%d spawns" % c["spawns"], "%d bosses, %d packs (%d elite)" % (c["bosses"], c["packs"], c["elitePacks"]),
                 "%d routes (%d lone patrols)" % (c["routes"], c["lonePatrols"]), "%d wanderers" % c["wanderers"]]:
        o.append('<text x="%.1f" y="%.1f" font-size="10.5" fill="#333">%s</text>' % (lx, ly, esc(line)))
        ly += 15
    o.append("</svg>")
    return "\n".join(o) + "\n"


# ---------------------------------------------------------------------------------------------------------------------
# Markdown
# ---------------------------------------------------------------------------------------------------------------------
def md_fights(f):
    if not f:
        return "no fight record attached"
    s = "%d fights as the pulled target (%d win / %d wipe%s), %d member deaths" % (
        f["fights"], f["win"], f["wipe"], (" / %d other" % f["other"]) if f["other"] else "", f["deaths"])
    if f["recentFights"]:
        s += "; last %d d: %d (%d/%d)" % (RECENT_DAYS, f["recentFights"], f["recentWin"], f["recentWipe"])
    if f["last"]:
        s += "; last %s %s" % (f["last"]["t"], f["last"]["outcome"])
    if f["joinedBy"]:
        s += "; joined by " + ", ".join("%s x%d (%d wipe, %d unprovoked)" % (
            k, v, f["joinedByWipe"].get(k, 0), f["joinedByUnprovoked"].get(k, 0))
            for k, v in list(f["joinedBy"].items())[:8])
    if f["joinedOthers"]:
        s += "; joined other fights %d times (%d unprovoked): %s" % (
            f["joinedOthers"], f["joinedOthersUnprovoked"],
            ", ".join("%s x%d" % kv for kv in list(f["joinedInto"].items())[:6]))
    if f["summoned"]:
        s += "; summoned/unmapped foes " + ", ".join("%s x%d" % kv for kv in list(f["summoned"].items())[:4])
    return s


def md_object(o, R):
    L = []
    title = "%s - %s" % (o["id"], o["name"] if o["kind"] == "boss" else o["composition"])
    L.append("#### %s" % title)
    L.append("")
    kinds = {"boss": "boss", "elite": "elite pack", "pack": "non-elite pack", "patrol": "lone patrol"}
    L.append("- **%s**, %s; centre %s, radius %.0f yd, reach ring %.0f yd; %d elite(s); spawns %s" % (
        kinds[o["kind"]], o["composition"] if o["kind"] == "boss" else "%d spawn(s)" % len(o["members"]),
        fmt_xyz(o["centre"]), o["radius"], o["radius"] + o["reach"], o["elites"],
        guid_ranges(m["guid"] for m in o["members"])))
    L.append("- entries %s; respawn %s" % (
        ", ".join(str(e) for e in o["entries"]),
        ", ".join(fmt_secs(r[0]) if r[0] == r[1] else "%s-%s" % (fmt_secs(r[0]), fmt_secs(r[1]))
                  for r in o["respawn"])))
    if o["groupLeaders"]:
        L.append("- DB link: creature_groups leader %s; flags %s" % (
            ", ".join(str(g) for g in o["groupLeaders"]),
            "; ".join("0x%02X = %s" % (g["flags"], g["meaning"]) for g in o["groupFlags"])))
    else:
        L.append("- DB link: none (pack by proximity <= %.0f yd)" % PROX_LINK if len(o["members"]) > 1
                 else "- DB link: none (single spawn)")
    if o.get("linkedAcross"):
        la = o["linkedAcross"]
        L.append("- **linked across %.0f yd**: %s" % (la["span"], "; ".join(
            "%s at %s" % (c["names"], fmt_xyz(c["centre"])) for c in la["clusters"])))
    wand = [m for m in o["members"] if m["wander"] > 0]
    if wand:
        L.append("- wanders: %s" % ", ".join("%d %.0f yd" % (m["guid"], m["wander"]) for m in wand))
    if o["routes"]:
        L.append("- walks: %s" % ", ".join("%s (%.0f yd %s, ~%.0f s per lap)" % (
            r, R["routes"][r]["length"], R["routes"][r]["shape"].split(":")[0], R["routes"][r]["loopSecs"])
            for r in o["routes"] if R["routes"][r]["points"]))
    if o.get("neighbours"):
        L.append("- neighbours <= %.0f yd: %s" % (NEIGHBOUR, ", ".join(
            "%s %.0f yd%s" % (n["id"], n["dist"], " **inside reach**" if n["insideReach"] else "")
            for n in o["neighbours"][:8])))
    if o.get("passedBy"):
        L.append("- patrols passing: %s" % ", ".join(
            "%s%s %.0f yd%s" % (p["route"], "" if p["owner"] == p["route"] else " (%s)" % p["owner"], p["dist"],
                                " **inside reach**" if p["insideReach"] else "") for p in o["passedBy"]))
    near_pb = [(p, no) for p in R["playbook"] for no in p["nearObjects"] if no["id"] == o["id"]]
    if near_pb:
        L.append("- playbook spots near: %s" % ", ".join(
            "%s %.0f yd%s" % (p["label"], no["dist"], " **inside reach**" if no["insideReach"] else "")
            for p, no in near_pb))
    if o.get("pools"):
        L.append("- pools: %s" % ", ".join("%d (%s)" % (p["pool"], p["description"]) for p in o["pools"]))
    if o.get("tactics"):
        L.append("- registry tactics: %s" % ", ".join("`%s`" % t for t in o["tactics"]))
    if o.get("learned"):
        L.append("- learned: %s" % "; ".join("%s: %s" % (k, ", ".join(v)) for k, v in o["learned"].items()))
    L.append("- fights: %s" % md_fights(o.get("fights")))
    L.append("")
    return L


def render_md(raids, meta):
    L = []
    L.append("<!-- GENERATED by tools/raidmap/regen.sh (tools/raidmap/raid_map.py) - DO NOT HAND-EDIT. "
             "Change the generator or the data and regenerate. -->")
    L.append("")
    L.append("# Raid map - Molten Core and Onyxia's Lair")
    L.append("")
    L.append("Generated from the VMaNGOS world DB (`mangos`, read-only), the boss scripts, `SuiTacticScope.h`, the raid "
             "playbook and the autopilot's learned files and fight records on %s. Fight data as of the newest record "
             "(%s). Machine-readable twins: `raid-map/mc.json`, `raid-map/onyxia.json`." % (
                 meta["host"], ", ".join("%s %s" % (R["key"], R["fights"]["latest"]) for R in raids)))
    L.append("")
    L.append("**Ids are stable and derived from data, never running numbers**: attach tactics, wipes and notes to them.")
    L.append("")
    L.append("| id | meaning |")
    L.append("|---|---|")
    L.append("| `B-<name>` | a boss and everything DB-linked to it (creature_groups/linking); scripted bosses with no "
             "spawn row (Majordomo, Ragnaros) use the same form |")
    L.append("| `P<guid>` | a pack: lowest spawn guid of its members |")
    L.append("| `R<guid>` | a waypoint route (movement_type 2), guid of the walker; a lone patroller's object id too |")
    L.append("| `A-<name>` | an area: the entrance or a boss |")
    L.append("| `E<guid>` | an Onyxia egg cluster: lowest egg gameobject guid |")
    L.append("")
    L.append("## Rules")
    L.append("")
    L.append("- **Packs**: connected components of `creature_groups` (leader-member), `creature_linking` and "
             "`creature_linking_template` edges; then single-linkage <= %.0f yd (3D) attaches *ungrouped*, "
             "non-patrolling spawns to a neighbour. Proximity never merges two DB groups and never extends a "
             "patrolling component. A component with a rank-3 creature is a boss. A DB group whose members fall "
             "into clusters more than %.0f yd apart is reported as **linked across**." % (PROX_LINK, SPLIT_LINK))
    L.append("- **Reach**: %.0f yd for an idle elite (the autopilot's credit), %.0f yd for the kinds in the "
             "`%s` registry row, template `detection_range` for non-elites. Reach ring = pack radius + reach. "
             "**Passes near** = a route comes within reach + %.0f yd of a member spawn (3D, point-to-segment); "
             "within reach is marked **inside reach**. Playbook spots use the walker's reach. Neighbours are packs "
             "whose closest members are <= %.0f yd apart." % (ELITE_REACH, WIDE_REACH, WIDE_REACH_KEY, NEAR_MARGIN,
                                                                NEIGHBOUR))
    L.append("- **Routes**: `creature_movement` by spawn guid, else `creature_movement_template` by entry (vmangos "
             "order). Paths are cyclic: a route whose last point is not on its first walks straight back. Lap time = "
             "length / speed + waits; speed = %.1f yd/s x speed_walk, or %.1f x speed_run when flags_extra has "
             "ALWAYS_RUN (0x40). Estimates, not measured." % (WALK_BASE, RUN_BASE))
    L.append("- **Areas**: nearest anchor (3D distance from the object's centre). Anchors: the instance entrance "
             "(`areatrigger_teleport` target) and every boss (spawn row; else playbook `summonAt`; else the first "
             "script position naming it). This is a Voronoi split, not walls: an object near a boundary may sit in "
             "the neighbouring room - check its coordinates.")
    L.append("- **Fights** (`sui-autopilot-fights.jsonl`): each foe carries its spawn guid, so records map to objects "
             "exactly (an entry mismatch is dropped). The fight's object = the earliest foe whose entry is the "
             "record's `target`, else the earliest foe the raid struck first. Every other object in the record "
             "*joined* it (`unprovoked` = it struck the raid first). Summoned foes (Lava Spawn, whelps, Sons of "
             "Flame) are counted as summoned; Majordomo/Ragnaros and their script adds go to the scripted boss.")
    L.append("- **Entry swaps**: an instance script that calls `UpdateEntry(NPC_Y)` under `case NPC_X:` turns a spawn "
             "into another kind at runtime (MC: Firelord <-> Lava Annihilator, 50/50 per spawn per instance). Such "
             "members are named `X|Y`, both entries count for tactics/learned data, and fight foes of either entry "
             "map to the spawn.")
    L.append("- **Hazards** below are ranked by a simple score (closer + boss/camp/large pack first); they are "
             "computed facts, not advice.")
    L.append("")
    for R in raids:
        L.extend(render_raid_md(R))
    return "\n".join(L) + "\n"


def render_raid_md(R):
    L = []
    c = R["counts"]
    L.append("## %s (map %d)" % (R["name"], R["map"]))
    L.append("")
    L.append("![%s](raid-map/%s.svg)" % (R["name"], R["key"]))
    L.append("")
    L.append("%d spawns -> %d bosses (%d scripted), %d packs (%d elite), %d routes (%d lone patrols), %d wanderers; "
             "%d playbook spots, %d registry rows, %d script positions, %d Core hard-coded positions." % (
                 c["spawns"], c["bosses"], len(R["scriptBosses"]), c["packs"], c["elitePacks"], c["routes"],
                 c["lonePatrols"], c["wanderers"], c["playbookSpots"], c["registryRows"], c["scriptPositions"],
                 c["corePositions"]))
    f = R["fights"]
    if f["available"]:
        L.append("")
        L.append("Fight records: %d on this map (%s), %d attached to objects, %d with no mappable foe, %d foe rows "
                 "dropped for an entry mismatch." % (f["records"], ", ".join("%s %d" % kv for kv in
                                                                              sorted(f["byOutcome"].items())),
                                                     f["attached"], f["unattached"], f["entryMismatch"]))
    L.append("")
    L.append("### Hazard facts")
    L.append("")
    titles = [("random-entry", "Spawns whose kind changes per instance"),
              ("linked-across", "DB groups that aggro together across a distance"),
              ("fight-joiner", "Who joined whose fights (fight records)"),
              ("route-inside-reach", "Routes passing inside an idle pack's / boss's / playbook spot's reach"),
              ("idle-neighbours", "Idle elite packs within each other's reach"),
              ("spot-inside-reach", "Playbook spots inside an idle pack's reach"),
              ("spot-on-route", "Playbook spots near a patrol route"),
              ("fast-respawn", "Elites that respawn in under 2 h")]
    TOP = 15
    for t, title in titles:
        hs = [h for h in R["hazards"] if h["type"] == t]
        if not hs:
            continue
        L.append("**%s** (%d%s)" % (title, len(hs), ", top %d" % TOP if len(hs) > TOP else ""))
        L.append("")
        for h in hs[:TOP]:
            L.append("- %s" % h["text"])
        L.append("")
    L.append("All hazards, ranked, with their ids: `raid-map/%s.json` -> `hazards`." % R["key"])
    L.append("")
    L.append("### Areas")
    L.append("")
    L.append("| area | anchor | at | bosses | packs | elites | lone patrols |")
    L.append("|---|---|---|---|---|---|---|")
    for a in R["anchors"]:
        objs = [o for o in R["objects"].values() if o["area"] == a["id"]]
        L.append("| %s | %s (%s) | %s | %s | %d | %d | %d |" % (
            a["id"], a["name"], a["source"], fmt_xyz(a["pos"]),
            ", ".join(o["id"] for o in objs if o["kind"] == "boss") or
            ", ".join(sb["id"] for sb in R["scriptBosses"].values() if sb["area"] == a["id"]) or "-",
            sum(1 for o in objs if o["kind"] in ("elite", "pack")), sum(o["elites"] for o in objs),
            sum(1 for o in objs if o["kind"] == "patrol")))
    L.append("")
    for a in R["anchors"]:
        L.append("### Area %s" % a["id"])
        L.append("")
        objs = [o for o in R["objects"].values() if o["area"] == a["id"]]
        order = {"boss": 0, "elite": 1, "pack": 2, "patrol": 3}
        objs.sort(key=lambda o: (order[o["kind"]], -o["elites"], o["id"]))
        for sb in R["scriptBosses"].values():
            if sb["area"] == a["id"]:
                L.append("#### %s - %s (scripted, no spawn row)" % (sb["id"], sb["name"]))
                L.append("")
                L.append("- at %s from %s; entry %d" % (fmt_xyz(sb["centre"]), sb["source"], sb["entry"]))
                if sb.get("tactics"):
                    L.append("- registry tactics: %s" % ", ".join("`%s`" % t for t in sb["tactics"]))
                if sb.get("learned"):
                    L.append("- learned: %s" % "; ".join("%s: %s" % (k, ", ".join(v)) for k, v in sb["learned"].items()))
                L.append("- fights: %s" % md_fights(sb.get("fights")))
                L.append("")
        for o in objs:
            if o["kind"] == "patrol":
                continue
            L.extend(md_object(o, R))
        lone = [o for o in objs if o["kind"] == "patrol"]
        if lone:
            L.append("Lone patrols starting here: %s (see Patrols)." % ", ".join(o["id"] for o in lone))
            L.append("")
    # routes
    L.append("### Patrols (waypoint routes)")
    L.append("")
    for r in sorted(R["routes"].values(), key=lambda r: (r["entry"], r["guid"])):
        L.append("#### %s - %s%s" % (r["id"], r["name"], "" if r["owner"] == r["id"] else " (walks for %s)" % r["owner"]))
        L.append("")
        if not r["points"]:
            L.append("- %s" % r.get("note", "no points"))
            L.append("")
            continue
        L.append("- spawn %s, area %s, crosses %s; %d points from %s%s" % (
            fmt_xyz(r["spawn"]), r["area"], " -> ".join(r["areas"]), len(r["points"]), r["source"],
            " (an entry template path exists and is NOT used)" if r.get("templatePathIgnored") else ""))
        L.append("- %.0f yd, %s; %s %.1f yd/s, waits %.0f s -> ~%.0f s per lap; respawn %s; reach %.0f yd" % (
            r["length"], r["shape"], "runs" if r["runs"] else "walks", r["speed"], r["waitSecs"], r["loopSecs"],
            fmt_secs(r["respawn"][0]), r["reach"]))
        pts = r["points"]
        L.append("- points: %s" % " ".join("%d%s" % (p["pt"], fmt_xy(p["pos"])) +
                                          ("w%ds" % (p["wait"] // 1000) if p["wait"] else "") for p in pts))
        if r["passesNear"]:
            L.append("- passes near: %s" % "; ".join(
                "%s %.0f yd%s (seg %d)" % (p["id"], p["dist"], " **inside reach**" if p["insideReach"] else "",
                                          p["segment"]) for p in r["passesNear"]))
        else:
            L.append("- passes near: nothing within reach + %.0f yd" % NEAR_MARGIN)
        if r.get("tactics"):
            L.append("- registry tactics: %s" % ", ".join("`%s`" % t for t in r["tactics"]))
        L.append("")
    if R["unusedTemplatePaths"]:
        L.append("Entry template paths not walked (the spawn's movement_type is not 2): %s." % ", ".join(
            "%s (%d, %d points, movement_type %s)" % (u["name"], u["entry"], u["points"],
                                                      "/".join(str(x) for x in u["spawnMovementType"]))
            for u in R["unusedTemplatePaths"]))
        L.append("")
    # wanderers
    L.append("### Wanderers (movement_type 1)")
    L.append("")
    if R["wanderers"]:
        byobj = collections.defaultdict(list)
        for w in R["wanderers"]:
            byobj[w["object"]].append(w)
        L.append("| object | wanderers (guid radius) |")
        L.append("|---|---|")
        for oid in sorted(byobj):
            L.append("| %s | %s |" % (oid, ", ".join("%d %s %.0f yd" % (w["guid"], w["name"], w["radius"])
                                                     for w in byobj[oid])))
    else:
        L.append("none")
    L.append("")
    # playbook
    L.append("### Playbook spots (`sui-raid-playbook.json`)")
    L.append("")
    if R["playbook"]:
        for p in R["playbook"]:
            L.append("- **%s** %s (area %s): nearest idle %s%s" % (
                p["label"], fmt_xyz((p["x"], p["y"], p["z"])), p["area"],
                ", ".join("%s %.0f yd%s" % (n["id"], n["dist"], " **inside reach**" if n["insideReach"] else "")
                          for n in p["nearObjects"][:5]) or "-",
                ("; routes " + ", ".join("%s (%s) %.0f yd" % (x["route"], x["owner"], x["dist"])
                                         for x in p["routesNear"])) if p["routesNear"] else ""))
    else:
        L.append("none for this raid")
    L.append("")
    # scripts
    L.append("### Boss-script positions (`%s`)" % R["scripts"]["dir"])
    L.append("")
    if R["scripts"]["flight"]:
        L.append("Flight table (`aMoveData`): point -> the point she flies to while casting Deep Breath.")
        L.append("")
        L.append("| fp | at | ground z | breath | flies to | comment |")
        L.append("|---|---|---|---|---|---|")
        for f in R["scripts"]["flight"]:
            L.append("| %d | %s | %.1f | %s | fp%d | %s |" % (f["id"], fmt_xyz((f["x"], f["y"], f["z"])), f["zGround"],
                                                             f["breath"], f["to"], f["comment"]))
        L.append("")
    if R["scripts"]["positions"]:
        L.append("| file:line | label | at | area | comment |")
        L.append("|---|---|---|---|---|")
        for p in R["scripts"]["positions"]:
            if p["array"] == "aMoveData":
                continue
            L.append("| %s:%d | %s | %s | %s | %s |" % (p["file"], p["line"], p["label"],
                                                       fmt_xyz((p["x"], p["y"], p["z"])), p["area"],
                                                       p["comment"].replace("|", "/")))
        L.append("")
    if R["scripts"]["thresholds"]:
        L.append("Position thresholds in the script:")
        L.append("")
        for t in R["scripts"]["thresholds"]:
            L.append("- %s:%d `%s` in %s: %s" % (t["file"], t["line"], "%s %s %.1f" % (t["axis"], t["op"], t["value"]),
                                                 t["func"] or "?", t["code"].replace("|", "/")))
        L.append("")
    if R["scripts"]["constants"]:
        L.append("Named constants: %s." % ", ".join("%s = %g (%s:%d)" % (c["name"], c["value"], c["file"], c["line"])
                                                   for c in R["scripts"]["constants"]))
        L.append("")
    L.append("### Core hard-coded positions inside this raid (SuperUiContent)")
    L.append("")
    if R["corePositions"]:
        for p in R["corePositions"]:
            L.append("- `%s:%d` (%.1f, %.1f%s): `%s`" % (p["file"], p["line"], p["x"], p["y"],
                                                         (", %.1f" % p["z"]) if p["z"] is not None else "",
                                                         p["code"].replace("`", "'")))
    else:
        L.append("none")
    L.append("")
    # gameobjects
    L.append("### Gameobjects")
    L.append("")
    if R["eggClusters"]:
        L.append("Egg clusters (%s; gameobject type %s): %s." % (R.get("eggRule"), R["eggClusters"][0]["goType"], "; ".join(
            "%s %d eggs, centre %s, x %.0f..%.0f, y %.0f..%.0f%s" % (
                e["id"], e["count"], fmt_xyz(e["centre"]), e["xRange"][0], e["xRange"][1], e["yRange"][0],
                e["yRange"][1], (", whelps spawn at " + fmt_xyz(e["whelpSpawn"])) if e["whelpSpawn"] else "")
            for e in R["eggClusters"])))
        L.append("")
    named = [g for g in R["gameobjects"] if g["kind"] not in ("egg", "fissure", "lavatrap")]
    for g in named:
        L.append("- %s (%d, guid %d) %s" % (g["name"], g["entry"], g["guid"], fmt_xyz(g["pos"])))
    L.append("- counts: %s" % ", ".join("%s x%d" % (k, v) for k, v in sorted(R["gameobjectCounts"].items())
                                        if v > 1 or "Doodad" not in k))
    L.append("")
    # registry
    L.append("### Registry rows (`SuiTacticScope.h`) on this map")
    L.append("")
    L.append("| key | entries | attached to | what | origin |")
    L.append("|---|---|---|---|---|")
    for row in R["registry"]:
        att = [o["id"] for o in list(R["objects"].values()) + list(R["scriptBosses"].values())
               if row["key"] in (o.get("tactics") or [])]
        att += [r["id"] for r in R["routes"].values() if row["key"] in r.get("tactics", []) and r["id"] not in att]
        L.append("| `%s` | %s | %s | %s | %s |" % (
            row["key"], ", ".join(str(e) for e in row["entries"]) or "**map-wide**",
            ", ".join(sorted(att)) if row["entries"] else "all", row["what"].replace("|", "/"),
            row["origin"].replace("|", "/")))
    L.append("")
    # entries
    L.append("### Creature kinds")
    L.append("")
    L.append("| entry | name | rank | spawns | respawn | aggro / help | reach | speed walk/run | learned | EventAI |")
    L.append("|---|---|---|---|---|---|---|---|---|---|")
    for e in R["entries"]:
        L.append("| %d | %s | %s | %d | %s | %s / %s | %.0f | %s/%s%s | %s | %s |" % (
            e["entry"], e["name"], e["rank"], e["count"],
            ", ".join(fmt_secs(r[0]) for r in e["respawn"]) or "-", e["detection"], e["callForHelp"], e["reach"],
            e["speedWalk"], e["speedRun"], " always runs" if e["alwaysRun"] else "",
            ", ".join(e["learned"]) or "-", "; ".join(e["eventAI"][:4]).replace("|", "/") or "-"))
    L.append("")
    return L


# ---------------------------------------------------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------------------------------------------------
def bundle():
    scope = parse_scope(SCOPE_H)
    playbook, pb_path = load_playbook()
    learned = load_learned()
    fights, bad, fpath = load_fights()
    tmpl = {}
    raids = []
    for raid in RAIDS:
        raids.append(build_raid(raid, tmpl, scope, playbook, learned, fights))
    meta = {"host": "the VMaNGOS box", "playbook": pb_path, "scope": SCOPE_H, "fightsFile": fpath,
            "fightLinesUnparsed": bad, "learnedFiles": {k: v is not None for k, v in learned.items()}}
    files = {"shared_docs/RAID_MAP.md": render_md(raids, meta)}
    for R in raids:
        files["shared_docs/raid-map/%s.svg" % R["key"]] = render_svg(R)
        J = dict(R)
        J["generator"] = "tools/raidmap/regen.sh (tools/raidmap/raid_map.py)"
        J["sources"] = meta
        files["shared_docs/raid-map/%s.json" % R["key"]] = json.dumps(J, indent=1, sort_keys=True) + "\n"
    summary = {R["key"]: dict(R["counts"], fights=R["fights"], hazards=len(R["hazards"])) for R in raids}
    return {"files": files, "summary": summary}


def unpack(path, root):
    with open(path, encoding="utf-8") as f:
        b = json.load(f)
    for rel, content in b["files"].items():
        out = os.path.join(root, *rel.split("/"))
        os.makedirs(os.path.dirname(out), exist_ok=True)
        with open(out, "w", encoding="utf-8", newline="\n") as f:
            f.write(content)
        print("wrote %s (%d bytes)" % (rel, len(content.encode("utf-8"))))
    print(json.dumps(b.get("summary", {}), indent=1))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--bundle", action="store_true", help="on the box: print the JSON bundle")
    ap.add_argument("--unpack", help="locally: bundle file to write into --root")
    ap.add_argument("--root", default=".")
    a = ap.parse_args()
    if a.unpack:
        unpack(a.unpack, a.root)
    elif a.bundle:
        json.dump(bundle(), sys.stdout)
    else:
        ap.print_help()


if __name__ == "__main__":
    main()
