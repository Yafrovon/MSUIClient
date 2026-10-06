# Drivable rooftop paths over roof-scan height fields (live `roof-scan`): A* across 1-yd cells a kart can drive
# (normal z >= --flat, at least --min-height above the street), stepping only between neighbours whose height
# differs by <= --step, preferring cells with clearance from the roof edge (the kart is ~2 yd wide). Prints the
# path simplified to waypoints "x,y" (+ z) for the live `drive` command and the course.
#   python tools/worldpack/roofpath.py dumps/roofscan-a.csv [b.csv] --from x,y --to x,y [--street 98]
#       [--min-height 6] [--flat 0.75] [--step 0.9] [--clear 2.5]
#   --farthest instead of --to: route to the reachable cell farthest (by path) from --from.
import csv, heapq, math, sys


def arg(name, default=None):
    return sys.argv[sys.argv.index(name) + 1] if name in sys.argv else default


def main():
    files = [a for a in sys.argv[1:] if a.endswith(".csv")]
    cells = {}
    rows = [r for f in files for r in csv.DictReader(open(f))]
    xs = sorted({float(r["x"]) for r in rows})
    S = min((b - a for a, b in zip(xs, xs[1:]) if b - a > 0.01), default=1.0)   # scan step (yd per cell)
    for f in [None]:
        for r in rows:
            # A cell counts only where the collision surface IS the visible one (zr = rendered WMO height):
            # Stormwind's roofs carry invisible collision planes the kart would otherwise drive on in mid-air.
            if "zr" in r and (r["zr"] == "" or abs(float(r["zr"]) - float(r["z"])) > 1.0):
                continue
            cells[(round(float(r["x"]) / S), round(float(r["y"]) / S))] = (float(r["z"]), float(r["nz"]))
    street, min_h = float(arg("--street", "98")), float(arg("--min-height", "6"))
    flat, step, clear = float(arg("--flat", "0.75")), float(arg("--step", "0.9")), float(arg("--clear", "2.5"))
    ok = {k for k, (z, nz) in cells.items() if nz >= flat and z - street >= min_h}
    nbr = [(dx, dy) for dx in (-1, 0, 1) for dy in (-1, 0, 1) if dx or dy]

    def passable(a, b):
        return b in ok and abs(cells[a][0] - cells[b][0]) <= step

    # clearance: distance (yd, up to 4) to the nearest cell that is not passable from its neighbour
    edge = {c for c in ok if any(not passable(c, (c[0] + dx, c[1] + dy)) for dx, dy in nbr)}
    dist = {}
    frontier = list(edge)
    for c in frontier:
        dist[c] = 0
    d = 0
    while frontier and d < 4:
        d += 1
        nxt = []
        for c in frontier:
            for dx, dy in nbr:
                n = (c[0] + dx, c[1] + dy)
                if n in ok and n not in dist:
                    dist[n] = d
                    nxt.append(n)
        frontier = nxt

    def nearest(p):
        p = (p[0] / S, p[1] / S)
        return min(ok, key=lambda c: (c[0] - p[0]) ** 2 + (c[1] - p[1]) ** 2)

    sx, sy = map(float, arg("--from").split(","))
    start = nearest((sx, sy))
    goal = None
    if arg("--to"):
        gx, gy = map(float, arg("--to").split(","))
        goal = nearest((gx, gy))
    # Dijkstra / A*
    g = {start: 0.0}
    came = {}
    pq = [(0.0, start)]
    while pq:
        f, c = heapq.heappop(pq)
        if goal is not None and c == goal:
            break
        for dx, dy in nbr:
            n = (c[0] + dx, c[1] + dy)
            if not passable(c, n):
                continue
            cl = dist.get(n, 4)
            cost = S * math.hypot(dx, dy) * (1.0 + (3.0 if cl < clear else 0.0) + (0.3 if cl < clear + 1 else 0.0))
            ng = g[c] + cost
            if ng < g.get(n, 1e18):
                g[n] = ng
                came[n] = c
                h = math.dist(n, goal) if goal is not None else 0.0
                heapq.heappush(pq, (ng + h, n))
    if goal is None:
        goal = max(g, key=g.get)
    if goal not in g:
        print(f"no rooftop path from {start} to {goal} (reachable cells: {len(g)})")
        return
    path = [goal]
    while path[-1] != start:
        path.append(came[path[-1]])
    path.reverse()

    # simplify (Douglas-Peucker, 1.5 yd), keep z
    def simplify(pts, eps):
        if len(pts) < 3:
            return pts
        a, b = pts[0], pts[-1]
        ax, ay, bx, by = a[0], a[1], b[0], b[1]
        L = math.hypot(bx - ax, by - ay) or 1e-6
        idx, dmax = 0, 0.0
        for i in range(1, len(pts) - 1):
            dd = abs((bx - ax) * (ay - pts[i][1]) - (ax - pts[i][0]) * (by - ay)) / L
            if dd > dmax:
                idx, dmax = i, dd
        if dmax > eps:
            return simplify(pts[:idx + 1], eps)[:-1] + simplify(pts[idx:], eps)
        return [a, b]
    pts = simplify(path, float(arg("--eps", "1.5")) / S)
    length = S * sum(math.dist(path[i], path[i + 1]) for i in range(len(path) - 1))
    minclear = min(dist.get(c, 4) for c in path)
    print(f"path {start} -> {goal}: {length:.0f} yd, {len(pts)} waypoint(s), min clearance {minclear} yd, z {cells[start][0]:.1f} -> {cells[goal][0]:.1f}")
    print("drive:", " ".join(f"{p[0] * S:.1f},{p[1] * S:.1f}" for p in pts))
    print("xyz:", " ".join(f"{p[0] * S:.1f},{p[1] * S:.1f},{cells[p][0]:.1f}" for p in pts))
    # what lies beyond the goal (for a jump / portal): the height profile continuing along the last heading
    if len(path) > 3:
        hx, hy = path[-1][0] - path[-4][0], path[-1][1] - path[-4][1]
        hl = math.hypot(hx, hy) or 1
        prof = []
        for s in range(1, 25, 2):
            p = (round(path[-1][0] + hx / hl * s / S), round(path[-1][1] + hy / hl * s / S))
            prof.append(f"{s}:{cells[p][0]:.0f}" if p in cells else f"{s}:-")
        print("beyond the end (yd:z):", " ".join(prof))


if __name__ == "__main__":
    main()
