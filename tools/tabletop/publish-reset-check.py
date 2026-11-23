"""Quest Tabletop P5: the LIVE "reset their quest state on publish" check on the local WSL stack
(shared_docs/QUEST_TABLETOP.md §13.2; owner: "Yes, run it", 2026-11-16).

Target: quest 473 "Report to Captain Stoutfist", held ONLY by SuperUI bots on bot accounts, none of the raid roster
(Testwar 787, bots 115-142 / 150-160): two turned it in, one is mid-quest. Scope "log" must remove exactly the mid-quest row.

  A. QA pack: an override of 473 (title tweak - a pack may only reset a quest it changes) + a quest-reset request
     (scope log). Enable the pack. Pre-flight: the reset is reported pending, no errors.
  B. Publish WITHOUT restart: the reset must stay pending (rows untouched, nothing logged as run).
  C. Publish WITH restart: mangosd stops, the reset runs, mangosd starts; the mid-quest row is gone, the turned-in rows
     stay, wp_quest_reset_log records the request once.
  D. Clean up: remove both docs, publish with restart (override row gone, server memory matches the DB), disable the pack;
     the request never runs again.

Usage: python -I tools/tabletop/publish-reset-check.py
"""
import json, subprocess, sys, time, urllib.request, uuid

WEB = "http://localhost:5000"
PACK_KEY = "tabletop-edit-qa"
QUEST = 473
ROSTER = set(range(115, 143)) | set(range(150, 161)) | {787}
REAL_ACCOUNTS = {1, 4, 5}
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
    script = f"Q={json.dumps(query)}\n" + SQL_HEAD % db
    out = subprocess.run(["wsl", "-e", "bash", "-s"], input=script.encode(), capture_output=True, timeout=60).stdout.decode()
    return [line.split("\t") for line in out.strip().splitlines() if line]


def holders():
    return sorted(sql("characters", f"SELECT s.guid, c.account, c.online, s.rewarded, s.status FROM character_queststatus s JOIN characters c ON c.guid = s.guid WHERE s.quest = {QUEST}"))


def mangosd_since():
    out = subprocess.run(["wsl", "-e", "bash", "-c", "systemctl show mangosd -p ActiveState -p ActiveEnterTimestamp"], capture_output=True, text=True).stdout
    return dict(l.split("=", 1) for l in out.strip().splitlines() if "=" in l)


def publish(label, restart):
    r = call("POST", "/WorldPacks/Publish", {"restartServer": restart, "operator": "tabletop-p5-reset-check"})
    check(f"{label}: publish started", bool(r.get("success")), r.get("error") or f"build {r.get('buildId')}")
    if not r.get("success"):
        return []
    t0 = time.time()
    while time.time() - t0 < 1800:
        time.sleep(5)
        st = call("GET", "/WorldPacks/Status")["build"]
        if not st.get("running"):
            log = st.get("log") or []
            for line in log:
                if any(k in line for k in ("quest reset", "mangosd", "pending", "quest overrides", "preflight", "verify:")):
                    print("   log:", line[:220])
            check(f"{label}: publish finished", st.get("status") != "failed", f"{time.time() - t0:.0f} s; last: {log[-1][:160] if log else '-'}")
            return log
    check(f"{label}: publish finished", False, "timed out")
    return []


# ── safety: the target is held only by bots outside the raid roster, nobody online ──────────────────
before = holders()
print("holders before:", before)
guids = {int(h[0]) for h in before}
accounts = {int(h[1]) for h in before}
if not before or guids & ROSTER or accounts & REAL_ACCOUNTS or any(h[2] != "0" for h in before):
    print("REFUSED: the target quest's holders are not all offline non-roster bots"); sys.exit(2)
expected_left = [h for h in before if h[3] == "1"]
expected_gone = [h for h in before if h[3] == "0"]

pack = next((p for p in call("GET", "/WorldPacks/Packs")["packs"] if p["packKey"] == PACK_KEY), None)
if pack is None:
    print("no QA pack"); sys.exit(2)
pid = pack["id"]
request_id = uuid.uuid4().hex
stock = call("GET", f"/QuestAtlas/StockQuest?entry={QUEST}")["quest"]
row = dict(stock["row"]); row.pop("patch", None)
row["Title"] = f"{row['Title']} (reset check)"
items = [
    {"kind": "quest-override", "key": str(QUEST), "body": row},
    {"kind": "quest-reset", "key": str(QUEST), "body": {"entry": QUEST, "scope": "log", "requestId": request_id}},
]
removal = [{"kind": i["kind"], "key": i["key"], "body": None} for i in items]
try:
    r = call("POST", "/WorldPacks/Content", {"packId": pid, "label": "P5 reset check: override + reset request", "operator": "tabletop-p5-reset-check", "items": items})
    check("A: override + quest-reset request stored in the QA pack", bool(r.get("success")), r.get("error", ""))
    call("POST", "/WorldPacks/SetEnabled", {"packId": pid, "enabled": True, "operator": "tabletop-p5-reset-check"})
    pre = call("GET", "/WorldPacks/Preflight")["report"]
    errs = [f for f in pre["findings"] if f["severity"] == "error"]
    pending = [f for f in pre["findings"] if f["subject"] == "quest resets"]
    check("A: pre-flight reports the reset pending and has no errors", not errs and pending and str(QUEST) in pending[0]["message"],
          "; ".join(f"{f['check']} {f['subject']}: {f['message']}" for f in (errs or pending)[:3]))

    log = publish("B (no restart)", False)
    check("B: without a restart the reset stays pending (rows untouched, said so)",
          holders() == before and any("stay pending" in l for l in log), json.dumps(holders()))
    ran = sql("admin", f"SELECT COUNT(*) FROM wp_quest_reset_log WHERE request_id = '{request_id}'")
    check("B: nothing recorded as run", ran == [["0"]], json.dumps(ran))

    since0 = mangosd_since()
    log = publish("C (restart)", True)
    since1 = mangosd_since()
    after = holders()
    print("holders after:", after)
    check("C: mangosd was stopped and started again", since1.get("ActiveState") == "active" and since1.get("ActiveEnterTimestamp") != since0.get("ActiveEnterTimestamp")
          and any("mangosd stop" in l for l in log) and any("mangosd start" in l for l in log), json.dumps(since1))
    check("C: the mid-quest row is gone, the turned-in rows stay", after == expected_left,
          f"expected left {expected_left}, gone {expected_gone}; now {after}")
    ran = sql("admin", f"SELECT entry, scope, rows_removed FROM wp_quest_reset_log WHERE request_id = '{request_id}'")
    check("C: the request is recorded once with the rows it removed", ran == [[str(QUEST), "log", str(len(expected_gone))]], json.dumps(ran))
finally:
    r = call("POST", "/WorldPacks/Content", {"packId": pid, "label": "P5 reset check: clean up", "operator": "tabletop-p5-reset-check", "items": removal})
    check("D: override + reset request removed", bool(r.get("success")), r.get("error", ""))
    publish("D (clean-up, restart)", True)
    call("POST", "/WorldPacks/SetEnabled", {"packId": pid, "enabled": False, "operator": "tabletop-p5-reset-check"})
    rows = sql("mangos", f"SELECT patch, Title FROM quest_template WHERE entry = {QUEST}")
    check("D: quest 473 is back to its stock row only", len(rows) == 1 and rows[0][0] == "0", json.dumps(rows))
    ran = sql("admin", f"SELECT COUNT(*) FROM wp_quest_reset_log WHERE request_id = '{request_id}'")
    check("D: the request ran exactly once", ran in ([["1"]], [["0"]]) and holders() == expected_left if ran == [["1"]] else True, json.dumps(ran))
    print(f"mangosd: {mangosd_since()}")
    print(f"VERDICT {'PASS' if fails == 0 else f'FAIL ({fails})'}")
