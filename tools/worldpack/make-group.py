# Make a SuperUI bot group for a test (World Builder tier 3: the fair dungeon run, any group test).
# SuperUI bots are the project's bots (AiBotAI + the MangosSuperUI brain); stock vmangos party bots are NOT - never
# use them for a test. The box is a test environment: create bots freely, delete them when done.
#
#   python tools/worldpack/make-group.py                       # tank warrior, healer priest, mage, rogue (Alliance)
#   python tools/worldpack/make-group.py --horde               # tank warrior, healer priest, mage, rogue (Horde races)
#   python tools/worldpack/make-group.py human:warrior dwarf:priest gnome:mage nightelf:hunter
#
# What it does: POST /Bots/AddBots (the web app runs ".bot addai <class> <race> <name>" per bot), waits until every new
# bot is online (/Bots/States), prints each bot's name, guid, class and SuperUI role (/Bots/CombatLoadout/<guid>: the
# role comes from the class's default talent profile - warrior = Protection/Tank, priest = Discipline/Healer), and the
# last line is the argument for gen-live.py:  --bots Name1,Name2,Name3,Name4
# Levelling, spells, weapon skills, gear and the party invite are SETUP steps the gen-live.py --fair protocol does.
# Faction: the bots must be the tester's faction to group (Gilnwar is Alliance). Web app: --web URL or MSUI_WEBAPP.
import json, os, sys, time, urllib.request

args = sys.argv[1:]
WEB = (args[args.index("--web") + 1] if "--web" in args else os.environ.get("MSUI_WEBAPP", "http://192.168.0.2:5000")).rstrip("/")
pairs = [a for a in args if ":" in a and not a.startswith("http")]
if not pairs:
    pairs = ["orc:warrior", "undead:priest", "undead:mage", "orc:rogue"] if "--horde" in args else \
            ["human:warrior", "human:priest", "gnome:mage", "human:rogue"]
ROLE = {1: "melee dps", 2: "ranged dps", 3: "tank", 4: "healer"}


def call(route, body=None):
    req = urllib.request.Request(f"{WEB}/{route}", data=None if body is None else json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json"}, method="GET" if body is None else "POST")
    return json.load(urllib.request.urlopen(req, timeout=60))


before = {b["name"] for b in call("Bots/States").get("bots", [])}
spawns = [{"race": r, "cls": c, "count": 1} for r, c in (p.split(":", 1) for p in pairs)]
started = call("Bots/AddBots", {"spawns": spawns})
if not started.get("success"):
    raise SystemExit(f"AddBots refused: {started.get('error')}")
for _ in range(60):                                   # the batch runs in the background on the web app
    job = call("Bots/AddBotsStatus").get("job") or {}
    if job.get("phase") != "running":
        break
    time.sleep(2)
if job.get("failed"):
    print(f"warning: {job['failed']} bot(s) failed: {job.get('failedNames')}")
new = []
for _ in range(30):                                   # ...and each bot comes online over the bridge
    new = [b for b in call("Bots/States").get("bots", []) if b["name"] not in before]
    if len(new) >= len(spawns):
        break
    time.sleep(2)
if len(new) < len(spawns):
    raise SystemExit(f"only {len(new)} of {len(spawns)} new bots came online")
for b in new:
    lo = call(f"Bots/CombatLoadout/{b['guid']}")
    role = (lo.get("activeRole") or {}).get("id", 0)
    print(f"{b['name']}  guid {b['guid']}  {lo.get('className')}  role {ROLE.get(role, role)}  level {lo.get('level')}")
print("--bots " + ",".join(b["name"] for b in new))
