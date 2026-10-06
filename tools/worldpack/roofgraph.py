# Rooftop graph from roof-scan CSVs (live `roof-scan`): drivable roof patches (flat enough, high above the street)
# grouped into connected pieces, and the JUMPS between them a kart can make (gap <= --gap yd, landing no higher
# than --up yd above takeoff, no lower than --down). For laying the WoW Karting rooftop leg (course v2).
#   python tools/worldpack/roofgraph.py dumps/roofscan-<a>.csv [more.csv] [--street 86] [--min-height 10]
#       [--flat 0.8] [--gap 14] [--up 1.5] [--down 10] [--min-area 60]
import csv, math, sys
from collections import deque


def arg(name, default):
    return sys.argv[sys.argv.index(name) + 1] if name in sys.argv else default


def main():
    files = [a for a in sys.argv[1:] if a.endswith(".csv")]
    cells = {}
    for f in files:
        for r in csv.DictReader(open(f)):
            cells[(round(float(r["x"])), round(float(r["y"])))] = (float(r["z"]), float(r["nz"]))
    zs = sorted(v[0] for v in cells.values())
    street = float(arg("--street", str(zs[len(zs) // 10])))
    min_h, flat = float(arg("--min-height", "10")), float(arg("--flat", "0.8"))
    gap, up, down = float(arg("--gap", "14")), float(arg("--up", "1.5")), float(arg("--down", "10"))
    min_area = int(arg("--min-area", "60"))
    roof = {k for k, (z, nz) in cells.items() if nz >= flat and z - street >= min_h}
    # Connected pieces: neighbours within 1 cell AND a height step a kart can drive (<= 0.9 yd).
    comp, pieces = {}, []
    for start in roof:
        if start in comp:
            continue
        idx = len(pieces)
        q, members = deque([start]), []
        comp[start] = idx
        while q:
            c = q.popleft(); members.append(c)
            for dx in (-1, 0, 1):
                for dy in (-1, 0, 1):
                    n = (c[0] + dx, c[1] + dy)
                    if n in roof and n not in comp and abs(cells[n][0] - cells[c][0]) <= 0.9:
                        comp[n] = idx; q.append(n)
        pieces.append(members)
    keep = [i for i, m in enumerate(pieces) if len(m) >= min_area]
    print(f"street z {street:.1f}; {len(roof)} roof cell(s) in {len(pieces)} piece(s), {len(keep)} >= {min_area} cells")
    info = {}
    for i in keep:
        m = pieces[i]
        cx = sum(c[0] for c in m) / len(m); cy = sum(c[1] for c in m) / len(m)
        z = sum(cells[c][0] for c in m) / len(m)
        # principal axis (length of the drivable run)
        sxx = sum((c[0] - cx) ** 2 for c in m) / len(m); syy = sum((c[1] - cy) ** 2 for c in m) / len(m)
        sxy = sum((c[0] - cx) * (c[1] - cy) for c in m) / len(m)
        ang = 0.5 * math.atan2(2 * sxy, sxx - syy)
        proj = [(c[0] - cx) * math.cos(ang) + (c[1] - cy) * math.sin(ang) for c in m]
        info[i] = (cx, cy, z, len(m), max(proj) - min(proj), math.degrees(ang))
        print(f"  piece {i:3d}: centre ({cx:8.1f},{cy:7.1f}) z {z:6.1f} (+{z - street:4.1f}) area {len(m):5d} run {max(proj) - min(proj):5.1f} yd axis {math.degrees(ang):6.1f}")
    # Jumps: the closest edge-cell pair between two pieces.
    edge = {i: [c for c in pieces[i] if any((c[0] + dx, c[1] + dy) not in roof for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)))] for i in keep}
    print("jumps (from -> to: gap, dz, takeoff -> landing):")
    for a in keep:
        for b in keep:
            if a == b:
                continue
            best = None
            for ca in edge[a][::2]:
                for cb in edge[b][::2]:
                    d = math.dist(ca, cb)
                    if d <= gap and (best is None or d < best[0]):
                        best = (d, ca, cb)
            if best:
                d, ca, cb = best
                dz = cells[cb][0] - cells[ca][0]
                if -down <= dz <= up:
                    print(f"  {a:3d} -> {b:3d}: gap {d:4.1f} dz {dz:+5.1f}  ({ca[0]},{ca[1]},{cells[ca][0]:.1f}) -> ({cb[0]},{cb[1]},{cells[cb][0]:.1f})")


if __name__ == "__main__":
    main()
