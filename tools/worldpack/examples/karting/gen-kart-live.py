# Tier-3 live protocol for the WoW Karting course (shared_docs/WOW_KARTING.md): drive the circuit for real on a
# server kart - every leg's route at kart speed and INTO its Real Portal - for N laps, asserting the kart survives
# every crossing. Run with tools/worldpack/launch-live.ps1 (-Character <a test driver>).
#
#   python tools/worldpack/examples/karting/gen-kart-live.py OUT.txt [--laps 1] [--kart 38001] [--character Kartwar]
#
# SETUP (labelled, not part of the result): revive + level the test driver, teleport to the raceway arrival, mount.
import json, math, os, sys, urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
KART_DISPLAY = {38001: 10318, 38002: 2490, 38003: 71000}   # 38003 = the custom go-kart (kart-model.py)


def arg(name, default):
    return sys.argv[sys.argv.index(name) + 1] if name in sys.argv else default


def main():
    out = sys.argv[1]
    laps = int(arg("--laps", "1"))
    kart = int(arg("--kart", "38003"))
    who = arg("--character", "Kartwar")
    course = json.load(open(os.path.join(HERE, "course.json")))
    legs = course["legs"]
    only = int(arg("--leg", "-1"))      # --leg N: drive only leg N, from its arrival (iterate on one jump)
    first = legs[max(only, 0)]
    # Start where the previous leg's portal drops a racer: this leg's arrival, on the ground.
    ax, ay = first["arrive"][0], first["arrive"][1]
    web = os.environ.get("MSUI_WEB", "http://192.168.0.2:5000")
    az = json.load(urllib.request.urlopen(f"{web}/WorldMap/GetHeight?map={first['map']}&x={ax}&y={ay}", timeout=30))["z"] + 1
    lines = [
        f"gm .revive {who}", "wait 2", f"gm .character level {who} 60", "wait 2",            # SETUP
        # Mount BEFORE teleporting (the kart survives GM teleports and portals): a moment on foot at a hostile
        # arrival gets the driver attacked, and a creature already fighting a rider dazes and dismounts it
        # (2026-10-04 live, Deadwind). A kart aura survives logout: drop both before mounting the one under test.
        *[f"gm .unaura {k}" for k in KART_DISPLAY], "wait 1",
        f"gm .aura {kart}", "wait 2",
        f"gm .go xyz {ax} {ay} {az:.1f} {first['map']}", "wait 6", "wait-grounded 15", f"gm .combatstop {who}",
        f"kart-assert {KART_DISPLAY[kart]} 20", "camera 0 -12 8", "mark",
    ]
    if "--race" in sys.argv:
        # A real race (Core SuiKartingRace.cpp): /kart start fills the grid with racer bots, the race teleports
        # everyone onto the grid, roots them and counts 3-2-1-GO; then drive the laps and wait for the result.
        lines += ["slash /kart start", "waitfor-new step=notice outcome=JOINED 10",
                  "waitfor-new step=phase outcome=COUNTDOWN 45", "wait 2", "camera 180 -18 12", "dump race-grid",
                  "waitfor-new step=countdown outcome=BEAT2 15", "dump race-countdown",
                  "waitfor-new step=countdown outcome=GO 10", "camera 0 -12 8"]
    for lap in range(1, laps + 1):
        for i, leg in enumerate(legs):
            if only >= 0 and i != only:
                continue
            p = leg["portal"]
            dest = legs[p["to"]]
            # Aim through the portal: the last route point, then 25 yd beyond the window along its facing.
            beyond = [round(p["x"] + math.cos(p["o"]) * 25, 1), round(p["y"] + math.sin(p["o"]) * 25, 1)]
            jumps = set(leg.get("jumps", []))
            # route points may carry a height ([x, y, z]); a jump index drives as "x,y,j" (press jump there)
            pts = [list(pt[:2]) + (["j"] if k in jumps else []) for k, pt in enumerate(leg["route"]) if k > 0]                 + [[p["x"], p["y"]], beyond]
            if "--items" in sys.argv:
                # MK64 items (Phase 5): the kart passes the 0.4 box row; stop driving at 0.55 of the leg (past the
                # 2 s roulette), fire the held item and capture the item window, then drive the rest of the leg.
                total = sum(math.dist(pts[k][:2], pts[k + 1][:2]) for k in range(len(pts) - 1))
                run, cut = 0.0, 1
                while cut < len(pts) - 2 and run + math.dist(pts[cut - 1][:2], pts[cut][:2]) < total * 0.55:
                    run += math.dist(pts[cut - 1][:2], pts[cut][:2])
                    cut += 1
                head = pts[1:cut + 1]
                if head:
                    lines.append(f"drive {int(run / 15) + 30} 8 " + " ".join(",".join(str(v) for v in pt) for pt in head))
                    lines += [f"dump race-item-l{lap}-{i}", f"slash /kart use{' back' if (lap + i) % 2 else ''}",
                              "wait 1", f"dump race-used-l{lap}-{i}"]
                    pts = pts[cut:]
            route = " ".join(",".join(str(v) for v in pt) for pt in pts)
            timeout = int(sum(math.dist(pts[k][:2], pts[k + 1][:2]) for k in range(len(pts) - 1)) / 15) + 30
            lines.append(f"drive-through {dest['map']} {timeout} {route}")
            lines += ["wait 3", "wait-grounded 15", f"kart-assert {KART_DISPLAY[kart]} 20"]
        lines.append(f"dump kart-lap-{lap}")
    if "--race" in sys.argv:
        # The last portal lands the kart before the start/finish line: cross it to finish.
        lx, ly = course["start"]["line"]
        lines += [f"drive 30 6 {lx},{ly} {lx + 60},{ly}",
                  "waitfor-new step=notice outcome=FINISHED 30", "waitfor-new step=phase outcome=FINISHED 150", "wait 2",
                  "dump race-results", "wait 20"]
    with open(out, "w", newline="\n") as f:
        f.write("\n".join(lines) + "\n")
    print(f"{out}: {len(lines)} step(s), {laps} lap(s), kart {kart}")


if __name__ == "__main__":
    main()
