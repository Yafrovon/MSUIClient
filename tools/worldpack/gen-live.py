# World Pack Verifier, tier 3: generate a live protocol (MSUIClient --live-protocol) that walks a
# World Content Pack as a real character. Everything is DERIVED from the pack's published docs, so a new
# zone needs no edits here (shared_docs/WORLD_BUILDER.md §9 is the playbook this belongs to).
#
#   python tools/worldpack/gen-live.py OUT.txt --pack <id|key> [--web URL] [--char NAME]
#          [--sections portals,npcs,holes,quests,worldmap,patrols,dungeon] [--maps 0,1] [--quests 7000100,...] [--bosses 7000310,...]
#          [--holes <client bin>/worldpack-holes.json] [--no-raid]
#          [--fair LEVEL [--setup] [--gear ID] [--spec ID] [--class warrior] [--bag ID] [--rotation SPELL] [--team alliance|horde] [--bots A,B,C,D]]
#
# portals  every areatrigger_teleport of the pack walked into for real (CMSG_AREATRIGGER -> NEW_WORLD on
#          its target map). Dungeon entrances/exits are walked by the dungeon section instead.
# npcs     every pack NPC with its own gossip menu: open it, then every service through its option
# holes    terrain holes. With --holes (the tier-2 hand-off written by the client's "verify-spawns" pass:
#          a probe CharacterController dropped into every hole cell and walked from every standable edge)
#          each chunk gets a DROP onto a floor the building catches and, where tier 2 found one, the
#          WALK-OFF: stand there, face the open corner, walk a quarter second. The product rule is that a
#          walking player is never lost, so both assert grounded. Without --holes: the G12 hole centres.
# quests   each quest end to end, steps DERIVED from its row: kill objectives (fights), collect objectives
#          (fights + loot from the drop source), exploration (walk into the trigger), then turn-in.
#          RAID: the tester (Testwar) leads the prepared 40-man raid, and vanilla gives a non-raid quest NO
#          kill credit and hides its quest drops in a raid group (Core Player.cpp KilledMonsterCredit /
#          quest-drop filter: isRaidGroup() && !IsAllowedInRaid()). So in raid mode (the default) each camp
#          is proven killable with real fights, the quest is then GM-completed (".quest complete" - a SETUP
#          step, labelled as such, never counted as earned credit), and the turn-in + reward run for real.
#          Exploration has no raid rule and is always earned for real. --no-raid runs full credit.
# paths    every graded path (doc kind "path": a pass, a land bridge, a road) WALKED for real, point to point
#          (walk-to: W held, re-aimed each frame; fails when stuck, falling or out of time) - how a seamless
#          zone on a continent proves it is reachable on foot, no teleport
# worldmap every pack map: stand on it, open the world map, the minimap mosaic must be DRAWN (verdict); each
#          pack zone's minimap PvP colour must match its faction (field 20) for the tester's --team
#          worldmap polygons: mounted hover ownership/name/highlight at inside and unchanged outside stock cells;
#          open the painted continent view and capture it (no stamped minimap rectangle).
# patrols  every patrol (a movement_type 2 spawn with creature_movement points) watched from a GM spot on its route:
#          the leader reaches the far point of its loop in time and every follower (creature_groups) walks with it
#          (patrol-watch). Runs before the dungeon, while the patrols are alive.
# dungeon  per instance map of the pack: in through its entrance portal; each boss (creature_template
#          rank 3 spawned there, or --bosses) fought for real (proves it engages and casts), then finished
#          with a GM damage command (a 5-player boss vs one tester) - death + loot checked; out again.
#
# FAIR (--fair LEVEL): a level-appropriate tester instead of the GM-helped raid leader. A fresh character (made
#          with char-create from a --character-select run, NOT in the raid - so every credit is earned) is set
#          to LEVEL with a stock premade gear + talent template (".character premade" - SETUP, labelled), every
#          trainer spell of that level (".learn all_trainer"), the class's quest abilities (--class, QUEST_SPELLS)
#          and every weapon skill + defense at level x 5 (".maxskill", then the client's assert-skills-capped);
#          worn/carried items are reset first and four --bag bags equipped (re-runnable: ensure-bag);
#          each stand-up after a fight is ".revive" + ".replenish" (full health, not revive's 50%); no
#          god mode, no GM damage anywhere. Quests: every kill for real - one runner step per objective,
#          kill-for-quest (attackers first, the nearest living target pulled by a teleport BESIDE it, respawns
#          waited out when a camp is smaller than the count, item objectives looted, deaths/rests counted);
#          a quest whose target is an instance boss is accepted, credited by the dungeon run and turned in
#          after it. Dungeon: the tester's group = SuperUI bots (--bots; make them with tools/worldpack/make-group.py,
#          never stock vmangos party bots - not part of this project). SETUP levels each to LEVEL, trains it, maxes its
#          weapon skills, gears it from the level-39 premade set of its class + SuperUI role and invites it; in the
#          dungeon they are summoned in (.namego) and fight on the tester's target (PlayerParty doctrine). Every pull
#          is taken the way a group takes it: the group gathers 30 yd short of it (GM mode while it gathers - a
#          SETUP step, nothing aggroes; the bots are summoned to the tester), the tester opens. Trash first: each
#          linked pack or patrol (creature_groups) is a pack-trial before the boss it stands nearest to (CLEARED /
#          WIPE / TIMEOUT, seconds, members killed, adds, deaths; a patrol is pulled when it is back home); then
#          the boss-trial: KILLED (the only pass), WIPE or TIMEOUT, with seconds, the boss health left and who died
#          - the numbers a designer balances by. Bosses in level order.
#
# Never run a World Builder script client (MSUI_WB_SCRIPT) with auto-login on the same account while a
# protocol runs: an account login kicks the live session (fixed 2026-09-27: script runs skip auto-login).
import json, math, os, sys, urllib.request

args = sys.argv[1:]
path = args[0] if args and not args[0].startswith("--") else "live-pack.txt"
def opt(name, default):
    return args[args.index(name) + 1] if name in args else default
WEB = opt("--web", os.environ.get("MSUI_WEBAPP", "http://192.168.0.2:5000")).rstrip("/")
CHAR = opt("--char", "Testwar")
sections = set(opt("--sections", "portals,paths,npcs,holes,quests,worldmap,patrols,dungeon").split(","))
MAP_FILTER = {int(m) for m in opt("--maps", "").split(",") if m} if "--maps" in args else None
def map_allowed(map_id): return MAP_FILTER is None or int(float(map_id)) in MAP_FILTER
fair_level = int(opt("--fair", "0"))
fair = fair_level > 0
# --setup: make the fair tester (and --bots) a real level-LEVEL character ONCE - level, spells, bags, gear, skills,
# invites. Without it the protocol starts from the character as it stands (a second run must not strip and re-dress it).
SETUP = "--setup" in args
raid = "--no-raid" not in args and not fair
GEAR, SPEC = opt("--gear", "66"), opt("--spec", "19")          # warrior dps-39-twink / arms-39-twink
ROTATION = int(opt("--rotation", "11564" if fair else "0"))    # Heroic Strike (the arms premade teaches it)
BOTS = [b for b in opt("--bots", "").split(",") if b]           # SuperUI bot names (tools/worldpack/make-group.py)
CLASS = opt("--class", "warrior")                               # the premade defaults above are warrior templates
# Class abilities a real character earns by QUEST, not from a trainer (".learn all_trainer" never teaches them):
# (level, spell). Warrior: Defensive Stance (Path of Defense, 10), Berserker Stance (Path of the Berserker, 30).
QUEST_SPELLS = {"warrior": [(10, 71), (30, 2458)]}
BAG = int(opt("--bag", "4245"))                                 # Small Silk Pack (item level 25), four of them
TEAM_MASK = {"alliance": 2, "horde": 4}[opt("--team", "alliance")]   # the tester's side (Testwar, Gilnwar: Alliance)

def get(route):
    return json.load(urllib.request.urlopen(f"{WEB}/WorldPacks/{route}", timeout=60))

def get_raw(route):
    return json.load(urllib.request.urlopen(f"{WEB}/{route}", timeout=60))

pack = opt("--pack", None)
if pack is None:
    raise SystemExit("--pack <id|key> is required; packs: " + ", ".join(f"{p['id']}={p['packKey']}" for p in get("Packs")["packs"]))
if not pack.isdigit():
    match = [p for p in get("Packs")["packs"] if p["packKey"] == pack]
    if not match: raise SystemExit(f"no pack with key {pack}")
    pack = str(match[0]["id"])

docs = get(f"Docs?packId={pack}")["docs"]
rows, maps, paths = {}, {}, {}
for d in docs:
    if d["kind"] == "path" and d["body"] and map_allowed(d["body"]["map"]):
        paths[d["docKey"]] = d["body"]
    if d["kind"].startswith("dbrow:") and d["body"]:
        rows.setdefault(d["kind"][6:], []).append(d["body"])
    elif d["kind"] == "map" and d["body"] and map_allowed(d["body"]["mapId"]):
        maps[int(d["body"]["mapId"])] = d["body"]
def I(v): return int(float(v))
def F(v): return float(v)
for table in ("creature", "gameobject"):
    rows[table] = [r for r in rows.get(table, []) if map_allowed(r["map"])]
owned_maps = set(maps) | {I(d["body"]["map"]) for d in docs if d["kind"] == "tile" and d["body"] and map_allowed(d["body"]["map"])}
owned_maps |= {I(r["map"]) for r in rows.get("creature", []) + rows.get("gameobject", [])}
instances = {m for m, b in maps.items() if I(b.get("instanceType", 0)) != 0}
tpl = {I(t["entry"]): t for t in rows.get("creature_template", [])}
spawns = {}
for c in rows.get("creature", []):
    if "id" in c:
        spawns.setdefault(I(c["id"]), []).append(c)
opts = {}
for o in rows.get("gossip_menu_option", []):
    opts.setdefault(I(o["menu_id"]), []).append(o)
quests = {I(q["entry"]): q for q in rows.get("quest_template", [])}
givers = {I(r["quest"]): I(r["id"]) for r in rows.get("creature_questrelation", [])}
enders = {I(r["quest"]): I(r["id"]) for r in rows.get("creature_involvedrelation", [])}
explore = {I(r["quest"]): I(r["id"]) for r in rows.get("areatrigger_involvedrelation", [])}
trig = {I(t["id"]): t for t in rows.get("areatrigger_template", []) if map_allowed(t["map_id"])}
tele = {I(t["id"]): t for t in rows.get("areatrigger_teleport", []) if I(t["id"]) in trig and map_allowed(t["target_map"])}
loot_of = {}   # item -> [(creature entry, chance %)] whose loot drops it
for l in rows.get("creature_loot_template", []):
    for e, t in tpl.items():
        if I(t.get("loot_id") or 0) == I(l["entry"]):
            loot_of.setdefault(I(l["item"]), []).append((e, abs(F(l["ChanceOrQuestChance"]))))
by_guid = {I(c["guid"]): c for c in rows.get("creature", [])}
groups = {}   # creature_groups: leader guid -> member guids (the leader lists itself)
for g in rows.get("creature_groups", []):
    groups.setdefault(I(g["leader_guid"]), []).append(I(g["member_guid"]))
routes = {}   # patrol leader guid -> its creature_movement points in order
for pt in sorted(rows.get("creature_movement", []), key=lambda r: (I(r["id"]), I(r["point"]))):
    routes.setdefault(I(pt["id"]), []).append((F(pt["position_x"]), F(pt["position_y"]), F(pt["position_z"])))
entrances = {m: [p for p, d in tele.items() if I(d["target_map"]) == m] for m in instances}
exits = {m: [p for p in tele if p in trig and I(trig[p]["map_id"]) == m] for m in instances}
dungeon_portals = {p for ps in list(entrances.values()) + list(exits.values()) for p in ps}

ICON_WAIT = {1: "family=vendor step=list", 3: "family=trainer step=list", 6: "family=bank step=open"}
# A killed earlier session can leave the character dead in the world: revive first (a no-op when alive).
out = ["wait 12", "select self", f"gm .revive {CHAR}", "wait 3", "gm .gm on", "wait 2"]
if fair and SETUP:
    # A character GM-levelled from 1 is NOT a level-LEVEL character: it keeps weapon skill 5 and defense 1 (it
    # missed nearly every swing - the first fair run's wolf never left 100%) and knows only level-1 spells. So:
    # every spell a trainer would offer at this level (".learn all_trainer" learns only trainer-GREEN spells:
    # level, rank and skill requirements met - class trainers, weapon masters), the class's quest abilities,
    # talents, gear (it teaches the weapon proficiency), THEN ".maxskill" (every weapon skill + defense to
    # level x 5), and the client proves the skills from its own fields before anything is fought.
    out += [f"# SETUP (fair tester): level {fair_level}, trained spells + quest abilities, premade talents {SPEC} + gear {GEAR}, skills maxed",
            f"gm .character level {CHAR} {fair_level}", "wait 2", "gm .learn all_trainer", "wait 3"]
    for lvl, spell in QUEST_SPELLS.get(CLASS, []):
        if fair_level >= lvl:
            out += ["select self", f"gm .learn {spell}", "wait 1"]
    # Premade gear moves whatever is worn into the backpack: a re-run would fill it with the previous copy and
    # quest drops/rewards would not fit. Start from nothing (".reset items": worn + carried, bags stay), with the
    # bags a real character of this level carries.
    out += ["select self", f"gm .reset items {CHAR}", "wait 2", "select self", f"gm .additem {BAG} 4", "wait 2"]
    for container in range(1, 5):
        out += [f"inventory ensure-bag {BAG} {container}", "wait 1"]
    out.append("wait 1")
    for container in range(1, 5):   # the server's answer: a bag really sits in every slot
        out.append(f"inventory require-bag {container} 1")
    out += ["select self", f"gm .character premade spec {SPEC}", "wait 2",
            "select self", f"gm .character premade gear {GEAR}", "wait 3",
            "select self", "gm .maxskill", "wait 2", "select self", "gm .replenish", "wait 1",
            "assert-skills-capped"]
if fair:   # GM.CheatGod re-arms god mode at every GM login: a fair run is always mortal
    out += [f"gm .cheat god off {CHAR}", "wait 1"]

# Level-39 premade gear per (class id, SuperUI role id): 1 melee dps, 2 ranged dps, 3 tank, 4 healer (world DB
# player_premade_item_template; ".character premade gear <id>" dresses the SELECTED player).
PREMADE_GEAR_39 = {(1, 3): 67, (1, 1): 66, (2, 4): 59, (2, 3): 58, (2, 1): 57, (3, 2): 43, (3, 1): 43, (4, 1): 35,
                   (5, 4): 74, (5, 2): 73, (7, 1): 81, (7, 2): 82, (7, 4): 83, (8, 2): 51, (9, 2): 39, (11, 1): 47, (11, 4): 47, (11, 3): 47}
CLASS_TOKEN = {1: "warrior", 2: "paladin", 3: "hunter", 4: "rogue", 5: "priest", 7: "shaman", 8: "mage", 9: "warlock", 11: "druid"}

def superui_bot(name):
    # guid, class and SuperUI role of a live SuperUI bot, from the web app (the bot must be online)
    states = get_raw("Bots/States")
    bot = next((b for b in states.get("bots", []) if b.get("name") == name), None)
    if bot is None:
        raise SystemExit(f"SuperUI bot {name} is not online - make/log in bots with tools/worldpack/make-group.py")
    lo = get_raw(f"Bots/CombatLoadout/{bot['guid']}")
    return int(bot["guid"]), int(lo["classId"]), int((lo.get("activeRole") or {}).get("id", 1))

if fair and BOTS and SETUP:
    # Group up in the open world at the first dungeon entrance: an ungrouped bot cannot be summoned into an instance.
    gate = next((pid for m in sorted(instances) for pid in entrances[m]), None)
    if gate is not None:
        out += [f"gm .go xyz {F(trig[gate]['x']) - 12:.1f} {F(trig[gate]['y']):.1f} {F(trig[gate]['z']) + 1:.1f} {I(trig[gate]['map_id'])}", "wait 8"]
    for b in BOTS:
        guid, cls, role = superui_bot(b)
        gear = PREMADE_GEAR_39.get((cls, role)) or next((g for (c, r), g in PREMADE_GEAR_39.items() if c == cls), None)
        out += [f"# SETUP (SuperUI group): {b} ({CLASS_TOKEN.get(cls, cls)}, role {role}) - level {fair_level}, trainer spells, "
                f"quest abilities, weapon skills, premade gear {gear}, invited",
                f"gm .namego {b}", "wait 3", f"gm .character level {b} {fair_level}", "wait 2",
                f"select guid:{guid}", "gm .learn all_trainer", "wait 2"]
        for lvl, spell in QUEST_SPELLS.get(CLASS_TOKEN.get(cls, ""), []):
            if fair_level >= lvl:
                out += [f"select guid:{guid}", f"gm .learn {spell}", "wait 1"]
        out += [f"select guid:{guid}", "gm .maxskill", "wait 1"]
        if gear:   # re-runnable: worn + carried items reset first (a re-applied set would stack in the bags)
            out += [f"select guid:{guid}", f"gm .reset items {b}", "wait 2", f"select guid:{guid}", f"gm .character premade gear {gear}", "wait 3"]
        out += [f"select guid:{guid}", "gm .replenish", "wait 1", f"invite name:{b}", "wait 3"]
    out += ["select self"]

def stand_up(seconds=2):
    # ".revive" is ResurrectPlayer(0.5) even on a LIVING character: every fight after it started at half health
    # (first fair run: 466 of ~1300). ".replenish" (selected, alive) sets health + mana to max - the SETUP stand-in
    # for eating and drinking between pulls.
    out.extend(["select self", f"gm .revive {CHAR}", "wait 1", "gm .replenish", f"wait {seconds}"])
covered = set()
deferred = []   # fair: quests credited by the dungeon run, turned in after it

def go(x, y, z, m):
    if not map_allowed(m): raise ValueError(f"generator attempted travel to excluded map {m}")
    out.extend([f"gm .go xyz {F(x):.1f} {F(y):.1f} {F(z) + 0.5:.1f} {m}", "wait 5"])

def portal(pid):
    t, d = trig[pid], tele[pid]
    covered.add(pid)
    out.extend([f"# --- portal {pid} {t['name']} -> map {I(d['target_map'])}",
                f"gm .go xyz {F(t['x']) - 12:.1f} {F(t['y']):.1f} {F(t['z']) + 1:.1f} {I(t['map_id'])}", "wait 8", "mark",
                f"gm .go xyz {F(t['x']):.1f} {F(t['y']):.1f} {F(t['z']) + 0.5:.1f} {I(t['map_id'])}",
                "waitfor-new family=portal step=area-trigger outcome=SENT 15",
                "waitfor-new step=new-world outcome=ARRIVED 25", f"assert-new map={I(d['target_map'])}", "wait 10",
                f"dump portal-{pid}-arrival"])

def fight(entry, seconds=90, loot=False):
    # Wandering mobs: stand on the SELECTED mob's live position, or auto-attack never lands.
    # ...let the body settle (a mob's live spot can be a slope) and FACE it: a .go keeps the old
    # orientation and auto-attack answers "You are facing the wrong way!" (BADFACING) forever.
    out.extend([f"select entry-nearest:{entry}", "gm-to-selection", "wait-grounded 6", "face selected", "attack start", f"fight-until-dead {ROTATION} {seconds}", "wait 2"])
    if fair:   # a fair tester can die: stand back up (a SETUP step) so one death does not end the run
        stand_up(1)
    if loot:
        out.extend([f"select corpse-nearest:{entry}", "mark", "loot request", "waitfor-new family=loot step=response 10",
                    "loot take-all", "wait 1"])

ai_bosses = {I(r["creature_id"]) for r in rows.get("creature_ai_events", [])}

def boss_fight(entry, seconds=40):
    out.extend([f"select entry-nearest:{entry}", "gm-to-selection", "wait-grounded 6", "face selected", "mark", "attack start",
                f"wait {seconds}", "assert-new SwingReceive"])
    if entry in ai_bosses:
        out.append("assert-new SpellDamageReceive")
    out.extend([f"select entry-nearest:{entry}", "gm .damage 1000000", "wait 3",
                # the REPLY (OPEN/EMPTY/REFUSED), not our own request line; a boss must drop something
                f"select corpse-nearest:{entry}", "mark", "loot request", "waitfor-new family=loot step=response 10",
                "assert-new outcome=OPEN", "loot take-all", "wait 2"])
    stand_up()

def boss_trial(entry, seconds=300):
    # Fair: the tester and its level-appropriate group, no GM help - the verdict carries the balance numbers. The
    # group gathered 30 yd out (gather); "pull": the tank bot opens, the tester joins beside the boss 4 s later.
    # (Before 2026-09-27 evening the tester was teleported onto the boss and fought it alone until the bots came.)
    # No loot step: in a group the loot rules decide who may open the corpse (it failed once after Lord Walden) -
    # that a boss drops loot is proven by the non-fair dungeon section (boss_fight asserts OPEN).
    out.extend([f"select entry-nearest:{entry}", "mark",
                f"boss-trial {ROTATION} {seconds} pull", "assert-new outcome=KILLED", "wait 3"])
    stand_up()

def gather(stage, m):
    # SETUP: the group gathers 30 yd short of the next pull in GM mode (nothing aggroes while it forms up), stands
    # up after a wipe and starts topped up, as a real group drinks before a pull. The caller turns GM mode off.
    # (A wipe leaves the bots dead and only the tester stood up: a later "trial" was the tester alone, 41 s, WIPE.)
    out.extend(["gm .gm on", "wait 1", f"gm .go xyz {stage[0]:.1f} {stage[1]:.1f} {stage[2] + 1.5:.1f} {m}", "wait 4", "wait-grounded 6",
                "select self", f"gm .revive {CHAR}", "wait 1", "gm .group revive", "wait 2"] + [f"gm .namego {b}" for b in BOTS] + ["wait 4",
                "select self", "gm .replenish", "gm .group replenish", "wait 1"])

def stage_point(anchor, came_from, back=30.0):
    # back yd from the pull toward where the group comes from (the previous pull, boss or the entrance)
    dx, dy = came_from[0] - anchor[0], came_from[1] - anchor[1]
    d = math.hypot(dx, dy) or 1.0
    k = min(back, d) / d
    return (anchor[0] + dx * k, anchor[1] + dy * k, anchor[2] + (came_from[2] - anchor[2]) * k)

def patrol_route(leader):
    # (far point of the loop, loop length, follower guids, " f1,f2" argument tail) of a patrol leader
    pts = routes[leader]
    home = pts[0]
    far = max(pts, key=lambda p: math.hypot(p[0] - home[0], p[1] - home[1]))
    loop = sum(math.hypot(b[0] - a[0], b[1] - a[1]) for a, b in zip(pts, pts[1:] + pts[:1]))
    followers = [g for g in groups.get(leader, []) if g != leader]
    return far, loop, followers, (f" {','.join(map(str, followers))}" if followers else "")

def kill_for_quest(qid, objective, entry, count, camp, kills=None):
    # Fair: the runner earns the objective like a player (kill-for-quest): fights whatever attacks first, pulls the
    # nearest living one beside it, waits out respawns when the camp is smaller than the count, loots item
    # objectives. Start at the camp spawn nearest its centre; the radius covers the camp.
    cx = sum(F(s["position_x"]) for s in camp) / len(camp); cy = sum(F(s["position_y"]) for s in camp) / len(camp)
    spread = max(math.hypot(F(s["position_x"]) - cx, F(s["position_y"]) - cy) for s in camp)
    c = min(camp, key=lambda s: math.hypot(F(s["position_x"]) - cx, F(s["position_y"]) - cy))
    respawn = max(I(s.get("spawntimesecsmin", s.get("spawntimesecs", 300)) or 300) for s in camp)
    needed = kills or count
    waits = math.ceil(max(0, needed - len(camp)) / len(camp)) if needed > len(camp) else 0
    seconds = min(3600, 120 + needed * 45 + waits * (respawn + 30))
    out.append(f"# {objective} of {count} from {tpl.get(entry, {}).get('name', entry)} ({len(camp)} spawns, respawn {respawn} s)")
    go(c["position_x"], c["position_y"], c["position_z"], I(c["map"]))
    out.append(f"kill-for-quest {qid} {objective} {entry} {count} {ROTATION} {seconds} {spread + 25:.0f}")
    stand_up()

def home(entry):
    s = spawns[entry][0]
    return s["position_x"], s["position_y"], s["position_z"], I(s["map"])

if "portals" in sections:
    for pid in sorted(set(tele) - dungeon_portals):
        portal(pid)

if "paths" in sections:
    for name, p in sorted(paths.items()):
        # "approach": walk-only waypoints before the graded part (e.g. from Silverpine through the Greymane
        # gate's corridor) - the path doc keeps them, the build never grades them.
        pts, m = (p.get("approach") or []) + p["points"], int(p["map"])
        out += [f"# --- path {name}: walk {len(pts)} point(s) on foot", "select self", f"gm .revive {CHAR}",
                f"gm .go xyz {pts[0][0]:.1f} {pts[0][1]:.1f} {pts[0][2] + 2:.1f} {m}", "wait 6"]
        out.append("mark")   # the minimap reports every area the walk crosses from here on
        for a, b in zip(pts, pts[1:]):
            dist = math.hypot(b[0] - a[0], b[1] - a[1])
            out.append(f"walk-to {b[0]:.1f} {b[1]:.1f} {dist / 5 + 15:.0f} 4")   # run 7 yd/s; generous time
        out.append(f"assert-grounded {pts[-1][2] - 15:.1f}")
        # Arrived in the ZONE the end tile is painted with (a tile doc's areaId) - land raised out of the sea
        # kept "The Great Sea" until areaReplace (2026-09-27).
        end_col, end_row = int(32 - pts[-1][1] / 533.33333), int(32 - pts[-1][0] / 533.33333)
        zone = next((I(t["body"].get("areaId", 0)) for t in docs if t["kind"] == "tile" and t["body"] and I(t["body"]["map"]) == m
                     and I(t["body"]["col"]) == end_col and I(t["body"]["row"]) == end_row and I(t["body"].get("areaId", 0))), 0)
        if zone:
            out.append(f"assert-new displayZone={zone}")

if "npcs" in sections:
    for entry, t in sorted(tpl.items()):
        menu = I(t.get("gossip_menu_id") or 0)
        if not menu or entry not in spawns:
            continue
        out.append(f"# --- NPC {t['name']} ({entry})")
        go(*home(entry))
        for o in sorted(opts.get(menu, []), key=lambda o: I(o["id"])) or [None]:
            out += [f"select entry-nearest:{entry}", "mark", "interact gossip", "waitfor-new family=gossip step=menu outcome=DECODED 10"]
            if o is None or I(o["option_icon"]) not in ICON_WAIT:
                out.append("gossip close"); continue
            out += [f"gossip select-icon {I(o['option_icon'])}", f"waitfor-new {ICON_WAIT[I(o['option_icon'])]} 10", "gossip close", "wait 1"]

if "holes" in sections:
    holes_file = opt("--holes", None)
    if holes_file:
        for key, h in sorted(json.load(open(holes_file, encoding="utf-8")).items()):
            m = int(h["map"])
            if m not in owned_maps:
                continue
            if h.get("catch"):
                c = h["catch"]
                out += [f"# hole {key}: drop onto the floor that catches it", f"gm .go xyz {c['x']:.1f} {c['y']:.1f} {c['z'] + 3:.1f} {m}",
                        "wait 6", f"assert-grounded {c['z'] - 5:.1f}"]
            if h.get("walkOff"):
                w = h["walkOff"]
                yaw = math.atan2(w["toY"] - w["fromY"], w["toX"] - w["fromX"])
                out += [f"# hole {key}: WALK-OFF (tier 2: {'held only by the client void guard' if w.get('guarded') else 'a walking player falls'})",
                        f"gm .go xyz {w['fromX']:.1f} {w['fromY']:.1f} {w['fromZ'] + 0.5:.1f} {m}", "wait 6",
                        f"face {yaw:.4f}", "press W", "wait 0.25", "release W", "wait 5",
                        f"assert-grounded {w['fromZ'] - 20:.1f}"]
    else:
        for f in get("VerifyReport")["report"]["findings"]:
            if f["check"] == "G12" and f["subject"] == "hole" and f.get("x") is not None and int(f["map"]) in owned_maps:
                m, x, y, z = int(f["map"]), F(f["x"]), F(f["y"]), F(f["z"])
                out += [f"# hole at map {m} ({x:.0f}, {y:.0f})", f"gm .go xyz {x:.1f} {y:.1f} {z + 4:.1f} {m}", "wait 6", f"assert-grounded {z - 40:.1f}"]

def turn_in(qid):
    q = quests[qid]
    ender = enders.get(qid, givers[qid])
    go(*home(ender))
    # The server shows the request-items page only when the quest asks for items; a kill-only or
    # exploration quest goes straight from COMPLETE_QUEST to the reward offer.
    needs_items = any(I(q.get(f"ReqItemCount{i}") or 0) > 0 for i in range(1, 5))
    out.extend([f"select entry-nearest:{ender}", "mark", "quest hello", "waitfor-new family=quest 10",
                f"quest query {qid}", "waitfor-new step=details outcome=DECODED 10", "quest complete"])
    if needs_items:
        out.extend(["waitfor-new step=request-items outcome=DECODED 10", "quest request-reward"])
    out.extend(["waitfor-new step=offer outcome=DECODED 10", "quest choose 0", "waitfor-new step=reward outcome=COMPLETED 15"])

if "quests" in sections:
    # Combat needs GM mode OFF (a GM's melee never lands); god mode stays on for the test character.
    out += ["gm .gm off", "wait 1"]
    if not fair:
        out += ["select self", f"gm .cheat god on {CHAR}", "wait 1"]
    for qid in [int(x) for x in opt("--quests", ",".join(str(q) for q in sorted(quests))).split(",") if x]:
        q = quests[qid]
        giver, ender = givers[qid], enders.get(qid, givers[qid])
        if MAP_FILTER is not None:
            excluded = giver not in spawns or ender not in spawns or (qid in explore and explore[qid] not in trig)
            excluded |= any(I(q.get(f"ReqCreatureOrGOCount{i}") or 0) > 0 and I(q.get(f"ReqCreatureOrGOId{i}") or 0) not in spawns for i in range(1, 5))
            excluded |= any(I(q.get(f"ReqItemCount{i}") or 0) > 0 and not any(e in spawns for e, _ in loot_of.get(I(q.get(f"ReqItemId{i}") or 0), [])) for i in range(1, 5))
            if excluded:
                out.append(f"# quest {qid} skipped: giver, ender or an objective is outside --maps")
                continue
        out.append(f"# --- quest {qid} {q['Title']}")
        stand_up()   # one death must not poison the rest of the run
        go(*home(giver))
        # Repeatable: a previous run may have the quest in the log or already REWARDED (then it can never be
        # taken again) - ".quest remove" clears both (SETUP); the accept below is the real one.
        out += ["select self", f"gm .quest remove {qid} {CHAR}", "wait 2", f"select entry-nearest:{giver}", "mark", "quest hello", "waitfor-new family=quest 10",
                f"quest query {qid}", "waitfor-new step=details outcome=DECODED 10", "quest accept", "waitfor-new family=quest 10"]
        in_dungeon = any(I(s["map"]) in instances for i in range(1, 5) for s in spawns.get(I(q.get(f"ReqCreatureOrGOId{i}") or 0), []))
        if fair and in_dungeon:
            out.append(f"# quest {qid}: its target is an instance boss - the dungeon run credits it, turn-in after")
            deferred.append(qid)
            continue
        for i in range(1, 5):
            target, count = I(q.get(f"ReqCreatureOrGOId{i}") or 0), I(q.get(f"ReqCreatureOrGOCount{i}") or 0)
            if target > 0 and count > 0:
                camp = spawns[target]
                boss = I(tpl.get(target, {}).get("rank") or 0) == 3
                if fair and not boss:
                    kill_for_quest(qid, f"kill:{i - 1}", target, count, camp)
                    continue
                for k in range(1 if boss else (min(count, 2) if raid else count)):
                    c = camp[k % len(camp)]
                    go(c["position_x"], c["position_y"], c["position_z"], I(c["map"]))
                    boss_fight(target) if boss else fight(target)
        for i in range(1, 5):
            item, count = I(q.get(f"ReqItemId{i}") or 0), I(q.get(f"ReqItemCount{i}") or 0)
            if not item or not count:
                continue
            sources = [(e, chance) for e, chance in loot_of.get(item, []) if e in spawns]
            src, chance = (sources or [(None, 0)])[0]
            if src is None:
                out.append(f"# item {item}: no pack drop source - cannot be collected here"); continue
            kills = math.ceil(count / max(chance / 100.0, 0.05) * 1.6)   # enough kills to be ~certain
            camp = spawns[src]
            if fair:
                kill_for_quest(qid, f"item:{item}", src, count, camp, kills)
                continue
            for k in range(1 if raid else kills):   # raid: quest drops are hidden - prove the source dies, no loot
                c = camp[-1 - k % len(camp)]         # from the far end: a kill quest before may have cleared the front
                go(c["position_x"], c["position_y"], c["position_z"], I(c["map"]))
                fight(src, loot=not raid)
        if qid in explore:
            t = trig[explore[qid]]
            out += [f"# explore trigger {explore[qid]}", "mark", f"gm .go xyz {F(t['x']):.1f} {F(t['y']):.1f} {F(t['z']) + 0.5:.1f} {I(t['map_id'])}",
                    "wait 4", "waitfor-new family=portal step=area-trigger outcome=SENT 10"]
        credit = any(I(q.get(f"ReqCreatureOrGOCount{i}") or 0) > 0 or I(q.get(f"ReqItemCount{i}") or 0) > 0 for i in range(1, 5))
        if raid and credit:
            out += [f"# SETUP, not earned: raid group blocks kill/drop credit for a non-raid quest (vanilla) - GM-complete {qid}",
                    "select self", f"gm .quest complete {qid} {CHAR}", "wait 2"]   # by name: a dead mob may be selected
        turn_in(qid)

if "worldmap" in sections:
    worldmap_docs = [d for d in docs if d["kind"] == "worldmap" and d["body"] and map_allowed(d["body"]["map"])]
    if worldmap_docs:
        from worldmap_samples import samples
        out.append("# --- continent hover/name/highlight from mounted client archives; expectations from authored polygon + stock outside")
        for m, x, y, expected, source in samples(worldmap_docs):
            out += [f"# {source}", f"worldmap-probe {m} {x:.3f} {y:.3f} {expected}"]
        for m in sorted({I(d["body"]["map"]) for d in worldmap_docs}):
            out += [f"worldmap-continent {m}", "wait 2", f"dump worldmap-continent-{m}", "panel quest", "wait 1"]
    for m in sorted(maps):
        # Somewhere standing on the map: a pack spawn on it, else a portal arrival onto it.
        spot = next(((s["position_x"], s["position_y"], s["position_z"]) for ss in spawns.values() for s in ss if I(s["map"]) == m), None)             or next(((d["target_position_x"], d["target_position_y"], d["target_position_z"]) for d in tele.values() if I(d["target_map"]) == m), None)
        if spot is None:
            out.append(f"# map {m}: nothing stands on it - no world map check"); continue
        out += [f"# --- world map of map {m} ({maps[m].get('name', '')})"]
        go(*spot, m)
        out += ["wait 5", "mark", "panel worldmap", f"waitfor-new family=world-map step=mosaic outcome=DRAWN 10",
                f"assert-new map={m}"]
        # Exactly the map's own tiles (G11: each has a minimap): a small map has no shared sea image, and a
        # "most common" guess once repeated one of 801's four tiles around the edges (tiles=16).
        n_tiles = sum(1 for t in docs if t["kind"] == "tile" and t["body"] and I(t["body"]["map"]) == m)
        if n_tiles:
            out.append(f"assert-new tiles={n_tiles};")
        out += [f"dump worldmap-{m}", "wait 1", "panel quest", "wait 1"]
    # A pack ZONE on a stock continent (AreaTable row with no parent on map 0/1): the build generates its
    # WorldMapArea row, there is no painted art, so the zone view must draw its minimap mosaic.
    for d in docs:
        if d["kind"] != "dbc:AreaTable" or not d["body"]:
            continue
        f = d["body"].get("fields", {})
        zmap, parent, zone = I(f.get("1", -1)), I(f.get("2", 0)), int(d["docKey"])
        if zmap not in (0, 1) or parent != 0 or not map_allowed(zmap):
            continue
        tiles_in = [t["body"] for t in docs if t["kind"] == "tile" and t["body"] and I(t["body"]["map"]) == zmap and I(t["body"].get("areaId", 0)) == zone]
        spot = next(((sp["position_x"], sp["position_y"], sp["position_z"]) for ss in spawns.values() for sp in ss
                     if I(sp["map"]) == zmap and any(I(t["col"]) == int(32 - F(sp["position_y"]) / 533.33333) and I(t["row"]) == int(32 - F(sp["position_x"]) / 533.33333) for t in tiles_in)), None)
        if spot is None:
            continue
        # The minimap paints the zone name by its faction (field 20) against the tester's team: Gilneas inherited
        # Silverpine's Horde 4 and read red for an Alliance tester (2026-09-27). Judged with GM mode OFF: ".gm on"
        # puts the character on faction template 35 (no group masks), where every zone - Stormwind too - reads
        # Contested. Arriving from another zone reports the area once (family=minimap step=area).
        mask = I(f.get("20", 0))
        expect = "Friendly" if mask == TEAM_MASK else "Hostile" if mask else "Contested"
        out += [f"# --- world map of zone {zone} ({f.get('11', '')}) on continent {zmap}", "gm .gm off", "wait 1", "mark"]
        go(*spot, zmap)
        out += ["waitfor-new family=minimap step=area outcome=UPDATED 15", f"assert-new pvp={expect}", "gm .gm on", "wait 1"]
        out += ["wait 5", "mark", "panel worldmap", "waitfor-new family=world-map step=mosaic outcome=DRAWN 10",
                f"assert-new area={zone}", f"dump worldmap-zone-{zone}", "wait 1", "panel quest", "wait 1"]

if "patrols" in sections:
    # A GM watches (a GM never pulls): the leader walks its creature_movement loop to the far point and its
    # formation followers stay with it. Walk speed ~2.5 yd/s: one loop + pauses + margin is always enough.
    out += ["gm .gm on", "wait 1"]
    for leader, pts in sorted(routes.items()):
        c = by_guid.get(leader)
        if c is None or I(c.get("movement_type") or 0) != 2:
            continue
        far, loop, followers, tail = patrol_route(leader)
        mid = min(pts, key=lambda p: math.hypot(p[0] - (pts[0][0] + far[0]) / 2, p[1] - (pts[0][1] + far[1]) / 2))
        out.append(f"# --- patrol {leader} ({tpl.get(I(c['id']), {}).get('name', c['id'])}) + {len(followers)} follower(s): {len(pts)} points, loop {loop:.0f} yd")
        go(mid[0], mid[1], mid[2] + 1, I(c["map"]))
        out += ["wait-grounded 6", f"patrol-watch {leader} {loop / 2.5 + 30:.0f} {far[0]:.1f} {far[1]:.1f}{tail}"]

if "dungeon" in sections:
    out += ["gm .gm off", "wait 1"]
    if not fair:
        out += ["select self", f"gm .cheat god on {CHAR}", "wait 1"]
    wanted = [int(x) for x in opt("--bosses", "").split(",") if x]
    for m in sorted(instances):
        if not entrances[m]:
            out.append(f"# instance map {m}: NO entrance portal - unreachable"); continue
        portal(entrances[m][0])
        if fair:   # the SuperUI group (grouped outside) is summoned in: followers never chase a portal
            if not BOTS:
                raise SystemExit("--fair with the dungeon needs --bots <SuperUI bot names> (tools/worldpack/make-group.py)")
            out.append(f"# SETUP: the SuperUI group enters with the tester ({', '.join(BOTS)})")
            out += [f"gm .namego {b}" for b in BOTS] + ["wait 6"]
        bosses = wanted or sorted(e for e, t in tpl.items() if I(t.get("rank") or 0) == 3 and any(I(s["map"]) == m for s in spawns.get(e, [])))
        arrival = next(((F(d["target_position_x"]), F(d["target_position_y"]), F(d["target_position_z"])) for p, d in tele.items() if p in entrances[m]), None)
        def pos(c): return (F(c["position_x"]), F(c["position_y"]), F(c["position_z"]))
        if not wanted and arrival:   # level order, then the nearer to the entrance first
            bosses.sort(key=lambda e: (I(tpl[e].get("level_min") or 0), math.hypot(pos(spawns[e][0])[0] - arrival[0], pos(spawns[e][0])[1] - arrival[1])))
        # Trash (fair): every linked pack / patrol (creature_groups) and every unlinked non-boss spawn of this map is a
        # pull, taken before the boss it stands nearest to, nearest-first along the way - a group clears its way in.
        member_of = {g: lead for lead, ms in groups.items() for g in ms}
        pulls = {}
        for c in rows.get("creature", []):
            if I(c["map"]) != m or I(tpl.get(I(c["id"]), {}).get("rank") or 0) == 3:
                continue
            lead = member_of.get(I(c["guid"]), I(c["guid"]))
            pulls.setdefault(lead, [lead])
            if I(c["guid"]) not in pulls[lead]:
                pulls[lead].append(I(c["guid"]))
        boss_at = {e: pos(spawns[e][0]) for e in bosses}
        before = {e: [] for e in bosses}
        if fair and bosses:
            for lead in pulls:
                a = pos(by_guid[lead])
                before[min(bosses, key=lambda e: math.hypot(boss_at[e][0] - a[0], boss_at[e][1] - a[1]))].append(lead)
        here = arrival or (boss_at[bosses[0]] if bosses else (0.0, 0.0, 0.0))
        for boss in bosses:
            b = spawns[boss][0]
            todo = before[boss]
            while todo:
                lead = min(todo, key=lambda l: math.hypot(pos(by_guid[l])[0] - here[0], pos(by_guid[l])[1] - here[1]))
                todo.remove(lead)
                anchor = pos(by_guid[lead])
                names = ", ".join(tpl.get(I(by_guid[g]["id"]), {}).get("name", "?") for g in pulls[lead] if g in by_guid)
                patrol = lead in routes and I(by_guid[lead].get("movement_type") or 0) == 2
                out.append(f"# pull {'patrol' if patrol else 'pack'} {lead}: {names}")
                gather(stage_point(anchor, here), m)
                if patrol:   # pulled when it is back at its home point (it pauses there), watched in GM mode
                    far, loop, followers, tail = patrol_route(lead)
                    out.append(f"patrol-at {lead} {loop / 2.5 + 30:.0f} {anchor[0]:.1f} {anchor[1]:.1f}{tail}")
                out += ["gm .gm off", "wait 2", f"pack-trial {ROTATION} 240 {','.join(map(str, pulls[lead]))}"]
                stand_up(1)
                here = anchor
            out.append(f"# boss {tpl[boss]['name']}")
            if fair:
                gather(stage_point(pos(b), here), m)
                out += ["gm .gm off", "wait 2"]
                boss_trial(boss)
                here = pos(b)
                continue
            go(F(b["position_x"]) - 6, b["position_y"], b["position_z"], m)
            boss_fight(boss, 45)
        for pid in exits[m]:
            portal(pid)
        for pid in entrances[m][1:]:
            portal(pid)

for qid in deferred:
    out.append(f"# --- deferred quest {qid} {quests[qid]['Title']}: credited by the dungeon run")
    turn_in(qid)
out += ["gm .gm on", "wait 1"]   # leave the test character safe (GM mode) when the protocol ends
if "portals" in sections and "dungeon" in sections:
    missing = sorted(set(tele) - covered)
    if missing:
        raise SystemExit(f"portals without a live test: {missing}")
with open(path, "w", encoding="utf-8", newline="\n") as f:
    f.write("\n".join(out) + "\n")
print(f"{path}: {len(out)} steps ({','.join(sorted(sections))}) for pack {pack}")
