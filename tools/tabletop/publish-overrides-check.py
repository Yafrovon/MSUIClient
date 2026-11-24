"""Quest Tabletop P5: a REAL publish round trip of the three Blizzard-content overrides on the LOCAL WSL web app
(shared_docs/QUEST_TABLETOP.md §13; World Builder target, never 192.168.0.2).

  1. In the QA pack (tabletop-edit-qa): a quest-override of quest 12 (new title), a spawn-override of Gryan
     Stoutmantle's spawn (5 yd east) and a text-override of broadcast_text 9. Enable the pack.
  2. Publish (no server restart) and read the world DB (read-only SQL through WSL): the patch-10 row of quest 12, the
     spawn's position, the line's text, the baselines.
  3. Remove the three docs, publish again, check every stock value is back exactly; disable the pack.

Usage: python -I tools/tabletop/publish-overrides-check.py   (prints PASS/FAIL lines and a VERDICT)
"""
import json, subprocess, sys, time, urllib.request

WEB = "http://localhost:5000"
PACK_KEY = "tabletop-edit-qa"
QUEST, SPAWN, TEXT = 12, 66975, 9
fails = 0


def call(method, path, body=None):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(WEB + path, data=data, method=method, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=120) as r:
        return json.loads(r.read().decode())


def check(name, ok, detail=""):
    global fails
    print(f"{'PASS' if ok else 'FAIL'}  {name}" + (f"  [{detail}]" if detail else ""), flush=True)
    if not ok:
        fails += 1


SQL_HEAD = r'''CS=$(python3 -c 'import json;print(json.load(open("/opt/mangossuperui/server-config.json"))["connectionStrings"]["%s"])')
get() { echo "$CS" | tr ';' '\n' | grep -i "^$1=" | cut -d= -f2-; }
mysql -h "$(get Server)" -P "$(get Port)" -u "$(get User)" -p"$(get Password)" "$(get Database)" -N -B -e "$Q" 2>/dev/null'''


def sql(db, query):
    """Read-only query through WSL; returns rows as lists of strings."""
    script = f"Q={json.dumps(query)}\n" + SQL_HEAD % db
    out = subprocess.run(["wsl", "-e", "bash", "-s"], input=script.encode(), capture_output=True, timeout=60).stdout.decode()
    return [line.split("\t") for line in out.strip().splitlines() if line]


def snapshot():
    q = sql("mangos", f"SELECT patch, Title FROM quest_template WHERE entry = {QUEST} ORDER BY patch")
    s = sql("mangos", f"SELECT position_x, position_y, position_z, orientation FROM creature WHERE guid = {SPAWN}")
    t = sql("mangos", f"SELECT male_text, female_text FROM broadcast_text WHERE entry = {TEXT}")
    return {"quest": q, "spawn": s, "text": t}


def publish(label):
    r = call("POST", "/WorldPacks/Publish", {"restartServer": False, "operator": "tabletop-p5-check"})
    check(f"{label}: publish started", bool(r.get("success")), r.get("error") or f"build {r.get('buildId')}")
    if not r.get("success"):
        return False
    t0 = time.time()
    while time.time() - t0 < 1800:
        time.sleep(5)
        st = call("GET", "/WorldPacks/Status")["build"]
        if not st.get("running"):
            log = st.get("log") or []
            ok = st.get("status", "done") != "failed" and not any("FAILED" in l or "failed:" in l for l in log[-5:])
            for line in log:
                if any(k in line for k in ("quest overrides", "spawn moves", "text rewrites", "preflight", "world DB")):
                    print("   log:", line[:200])
            check(f"{label}: publish finished", ok, f"{time.time() - t0:.0f} s; last: {log[-1][:160] if log else '-'}")
            return ok
    check(f"{label}: publish finished", False, "timed out")
    return False


packs = call("GET", "/WorldPacks/Packs")["packs"]
pack = next((p for p in packs if p["packKey"] == PACK_KEY), None)
if pack is None:
    print("no QA pack - run the edit probe first"); sys.exit(2)
pid = pack["id"]
before = snapshot()
print("before:", json.dumps(before))

stock = call("GET", f"/QuestAtlas/StockQuest?entry={QUEST}")["quest"]
row = dict(stock["row"]); row.pop("patch", None)
row["Title"] = "The People's Militia (publish check)"
x, y, z, o = (float(v) for v in before["spawn"][0])
items = [
    {"kind": "quest-override", "key": str(QUEST), "body": row},
    {"kind": "spawn-override", "key": f"creature:{SPAWN}", "body": {"table": "creature", "guid": SPAWN, "entry": 234, "map": 0,
        "x": round(x, 2), "y": round(y - 5, 2), "z": round(z, 2), "o": round(o, 3), "fromX": x, "fromY": y, "fromZ": z}},
    {"kind": "text-override", "key": f"broadcast_text:{TEXT}", "body": {"table": "broadcast_text", "entry": TEXT, "text": "Follow me, $n. (publish check)"}},
]
r = call("POST", "/WorldPacks/Content", {"packId": pid, "label": "P5 publish check: three overrides", "operator": "tabletop-p5-check", "items": items})
check("three override docs stored in the QA pack", bool(r.get("success")), r.get("error", ""))
call("POST", "/WorldPacks/SetEnabled", {"packId": pid, "enabled": True, "operator": "tabletop-p5-check"})
pre = call("GET", "/WorldPacks/Preflight")["report"]
errs = [f for f in pre["findings"] if f["severity"] == "error"]
check("pre-flight has no errors with the overrides", not errs, "; ".join(f"{f['check']} {f['subject']}: {f['message']}" for f in errs[:5]))

try:
    if publish("install"):
        after = snapshot()
        print("installed:", json.dumps(after))
        check("quest 12 gained a patch-10 row with the new title; the stock row is untouched",
              ["10", "The People's Militia (publish check)"] in after["quest"] and before["quest"][0] in after["quest"], json.dumps(after["quest"]))
        check("Gryan's spawn moved 5 yd", abs(float(after["spawn"][0][1]) - (y - 5)) < 0.05, json.dumps(after["spawn"]))
        check("broadcast_text 9 reads the rewrite", after["text"][0][0] == "Follow me, $n. (publish check)", json.dumps(after["text"]))
        base = sql("admin", f"SELECT (SELECT COUNT(*) FROM wp_quest_override WHERE entry = {QUEST}), (SELECT COUNT(*) FROM wp_spawn_baseline WHERE guid = {SPAWN}), (SELECT COUNT(*) FROM wp_text_baseline WHERE row_key = 'broadcast_text:{TEXT}')")
        check("baselines recorded for all three", base and base[0] == ["1", "1", "1"], json.dumps(base))
finally:
    removal = [{"kind": i["kind"], "key": i["key"], "body": None} for i in items]
    r = call("POST", "/WorldPacks/Content", {"packId": pid, "label": "P5 publish check: remove the overrides", "operator": "tabletop-p5-check", "items": removal})
    check("override docs removed", bool(r.get("success")), r.get("error", ""))
    if publish("restore"):
        restored = snapshot()
        print("restored:", json.dumps(restored))
        check("quest 12, Gryan's spawn and line 9 are exactly as before", restored == before,
              "" if restored == before else json.dumps(restored))
        base = sql("admin", f"SELECT (SELECT COUNT(*) FROM wp_quest_override), (SELECT COUNT(*) FROM wp_spawn_baseline), (SELECT COUNT(*) FROM wp_text_baseline)")
        check("no baseline left behind", base and base[0] == ["0", "0", "0"], json.dumps(base))
    call("POST", "/WorldPacks/SetEnabled", {"packId": pid, "enabled": False, "operator": "tabletop-p5-check"})
    print(f"VERDICT {'PASS' if fails == 0 else f'FAIL ({fails})'}")
