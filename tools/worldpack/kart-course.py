# WoW Karting course planner (shared_docs/WOW_KARTING.md): lay a circuit on the REAL roads by drawing it over the
# game's own minimap art, and measure it before anything is published.
#
#   python tools/worldpack/kart-course.py view OUT.png MAP xNorth xSouth yWest yEast [ppy]
#       stitched minimap of a box (MAP = 0 Eastern Kingdoms / 1 Kalimdor), 100-yd grid, labelled every 500 yd
#   python tools/worldpack/kart-course.py draw OUT.png COURSE.json LEG [pad]
#       one leg of a course over its minimap: route (yellow), checkpoints (cyan rings), portal (magenta),
#       arrival (green), distance ticks every 200 yd
#   python tools/worldpack/kart-course.py trace MAP x0 y0 x1 y1 [tolerance] [pad]
#       follow the road painted on the minimap between two points ON the road; prints a simplified route
#   python tools/worldpack/kart-course.py props COURSE.json [radius]
#       ADT props (trees, rocks, roots) within radius yd of each leg route: a kart stops dead on a trunk
#   python tools/worldpack/kart-course.py measure COURSE.json
#       per-leg length (route polyline, 2D) and the lap time at the course speed
#
# Tiles come from the web app (GET /WorldMap/Tile, decoded from the client MPQs) and are cached in
# scratch/minimap-cache/ (git-ignored). Minimap tile (a, b): a = floor(32 - y/533.33), b = floor(32 - x/533.33);
# image right = east (y falls), image down = south (x falls). WoW: x north, y west, z up.
#
# COURSE.json: { "name", "laps", "speed" (yd/s), "legs": [ { "name", "map", "route": [[x,y],...],
#   "checkpoints": [[x,y,r],...], "portal": {"x","y","z","o","to": legIndex} | null, "arrive": [x,y,z,o] } ] }
import json, math, os, sys, urllib.request

from PIL import Image, ImageDraw

TILE = 533.33333
PX = 256
WEB = os.environ.get("MSUI_WEB", "http://192.168.0.2:5000")
FOLDER = {0: "Azeroth", 1: "Kalimdor"}
CACHE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "scratch", "minimap-cache")


def tile(mapid, a, b):
    path = os.path.join(CACHE, f"{FOLDER[mapid]}_{a}_{b}.png")
    if not os.path.exists(path):
        os.makedirs(CACHE, exist_ok=True)
        url = f"{WEB}/WorldMap/Tile?map={FOLDER[mapid]}&row={a}&col={b}"
        try:
            data = urllib.request.urlopen(url, timeout=60).read()
        except Exception:
            data = b""
        with open(path, "wb") as f:
            f.write(data)
    if os.path.getsize(path) == 0:
        return None
    return Image.open(path).convert("RGB")


class View:
    """A world box rendered at ppy pixels per yard: (x,y) world -> (u,v) image."""

    def __init__(self, mapid, x_north, x_south, y_west, y_east, ppy=0.5):
        self.mapid, self.xn, self.xs, self.yw, self.ye, self.ppy = mapid, x_north, x_south, y_west, y_east, ppy
        w = max(1, int((y_west - y_east) * ppy))
        h = max(1, int((x_north - x_south) * ppy))
        self.img = Image.new("RGB", (w, h), (20, 20, 30))
        a0, a1 = math.floor(32 - y_west / TILE), math.floor(32 - y_east / TILE)
        b0, b1 = math.floor(32 - x_north / TILE), math.floor(32 - x_south / TILE)
        for a in range(a0, a1 + 1):
            for b in range(b0, b1 + 1):
                t = tile(mapid, a, b)
                if t is None:
                    continue
                # tile (a, b) covers y in [(32-a-1)*TILE, (32-a)*TILE], x in [(32-b-1)*TILE, (32-b)*TILE]
                y_top, x_top = (32 - a) * TILE, (32 - b) * TILE
                u, v = self.uv(x_top, y_top)
                size = int(round(TILE * ppy))
                self.img.paste(t.resize((size, size)), (int(round(u)), int(round(v))))
        self.draw = ImageDraw.Draw(self.img)

    def uv(self, x, y):
        return (self.yw - y) * self.ppy, (self.xn - x) * self.ppy

    def grid(self):
        for step, col in ((100, (60, 60, 60)), (500, (140, 140, 140))):
            y = math.ceil(self.ye / step) * step
            while y <= self.yw:
                u, _ = self.uv(0, y)
                self.draw.line([(u, 0), (u, self.img.height)], fill=col, width=1)
                if step == 500:
                    self.draw.text((u + 2, 2), f"y{int(y)}", fill=(255, 255, 255))
                y += step
            x = math.ceil(self.xs / step) * step
            while x <= self.xn:
                _, v = self.uv(x, 0)
                self.draw.line([(0, v), (self.img.width, v)], fill=col, width=1)
                if step == 500:
                    self.draw.text((2, v + 2), f"x{int(x)}", fill=(255, 255, 255))
                x += step

    def ring(self, x, y, r, col, width=2):
        u, v = self.uv(x, y)
        rr = max(3, r * self.ppy)
        self.draw.ellipse([u - rr, v - rr, u + rr, v + rr], outline=col, width=width)

    def label(self, x, y, text, col=(255, 255, 255)):
        u, v = self.uv(x, y)
        self.draw.text((u + 6, v - 6), text, fill=col)


def length(route):
    return sum(math.dist(route[i][:2], route[i + 1][:2]) for i in range(len(route) - 1))


def cmd_view(out, mapid, xn, xs, yw, ye, ppy=0.5):
    v = View(int(mapid), float(xn), float(xs), float(yw), float(ye), float(ppy))
    v.grid()
    v.img.save(out)
    print(f"{out}: {v.img.width}x{v.img.height}")


def cmd_draw(out, course_path, leg_index, pad=120):
    course = json.load(open(course_path))
    leg = course["legs"][int(leg_index)]
    pts = [p[:2] for p in leg["route"]]
    extra = [c[:2] for c in leg.get("checkpoints", [])]
    if leg.get("portal"):
        extra.append([leg["portal"]["x"], leg["portal"]["y"]])
    if leg.get("arrive"):
        extra.append(leg["arrive"][:2])
    xs = [p[0] for p in pts + extra]
    ys = [p[1] for p in pts + extra]
    pad = float(pad)
    v = View(leg["map"], max(xs) + pad, min(xs) - pad, max(ys) + pad, min(ys) - pad, 1.0)
    v.grid()
    line = [v.uv(x, y) for x, y in pts]
    v.draw.line(line, fill=(255, 220, 0), width=3)
    walked = 0.0
    nxt = 200.0
    for i in range(len(pts) - 1):
        seg = math.dist(pts[i], pts[i + 1])
        while walked + seg >= nxt:
            t = (nxt - walked) / seg
            x = pts[i][0] + (pts[i + 1][0] - pts[i][0]) * t
            y = pts[i][1] + (pts[i + 1][1] - pts[i][1]) * t
            v.ring(x, y, 3, (255, 255, 255), 1)
            v.label(x, y, f"{int(nxt)}")
            nxt += 200.0
        walked += seg
    for i, (x, y) in enumerate(pts):
        v.label(x, y, f"p{i}", (255, 220, 0))
    for i, c in enumerate(leg.get("checkpoints", [])):
        v.ring(c[0], c[1], c[2] if len(c) > 2 else 15, (0, 230, 255))
        v.label(c[0], c[1], f"cp{i}", (0, 230, 255))
    if leg.get("portal"):
        p = leg["portal"]
        v.ring(p["x"], p["y"], 4, (255, 0, 255), 4)
        v.label(p["x"], p["y"], "PORTAL", (255, 0, 255))
    if leg.get("arrive"):
        a = leg["arrive"]
        v.ring(a[0], a[1], 4, (0, 255, 0), 4)
        v.label(a[0], a[1], "ARRIVE", (0, 255, 0))
    v.draw.text((4, v.img.height - 14), f"{leg['name']}: {walked:.0f} yd", fill=(255, 255, 0))
    v.img.save(out)
    print(f"{out}: leg {leg_index} '{leg['name']}' {walked:.0f} yd, {v.img.width}x{v.img.height}")


def cmd_trace(mapid, x0, y0, x1, y1, tolerance=28, pad=150):
    """Follow the road painted on the minimap from (x0,y0) to (x1,y1): A* over pixels at 1 px/yd where a
    pixel costs little when its colour is close to the road colour sampled at both ends. Prints the
    simplified polyline (world coords, ~4 yd error) for a course leg's "route"."""
    import heapq
    mapid, x0, y0, x1, y1, tol, pad = int(mapid), float(x0), float(y0), float(x1), float(y1), float(tolerance), float(pad)
    v = View(mapid, max(x0, x1) + pad, min(x0, x1) - pad, max(y0, y1) + pad, min(y0, y1) - pad, 1.0)
    px = v.img.load()
    w, h = v.img.size

    def at(x, y):
        u, vv = v.uv(x, y)
        return int(u), int(vv)

    import colorsys

    def sat(c):
        return colorsys.rgb_to_hls(*[t / 255 for t in c])[2]

    def snap(c, radius=30):
        # Roads are painted as low-saturation cobble/dirt over saturated grass, sand or rock: the least
        # saturated pixel near an end point is on the road (eyeballed points are often 10-20 yd off).
        best = None
        for i in range(-radius, radius + 1):
            for j in range(-radius, radius + 1):
                q = (c[0] + i, c[1] + j)
                if 0 <= q[0] < w and 0 <= q[1] < h and i * i + j * j <= radius * radius:
                    key = (sat(px[q]), i * i + j * j)
                    if best is None or key < best[0]:
                        best = (key, q)
        return best[1]

    s, g = snap(at(x0, y0)), snap(at(x1, y1))

    def mean(c):
        cols = [px[min(w - 1, max(0, c[0] + i)), min(h - 1, max(0, c[1] + j))] for i in range(-1, 2) for j in range(-1, 2)]
        return tuple(sum(cc[k] for cc in cols) / len(cols) for k in range(3))

    road = [mean(s), mean(g)]

    def cost(p):
        c = px[p]
        d = min(math.dist(c, r) for r in road)
        return 1.0 if d <= tol else 1.0 + (d - tol) * 0.6

    dist = {s: 0.0}
    prev = {}
    heap = [(0.0, s)]
    steps = [(1, 0, 1), (-1, 0, 1), (0, 1, 1), (0, -1, 1), (1, 1, 1.414), (1, -1, 1.414), (-1, 1, 1.414), (-1, -1, 1.414)]
    while heap:
        f, p = heapq.heappop(heap)
        if p == g:
            break
        d0 = dist[p]
        if f - math.dist(p, g) > d0 + 1e-6:
            continue
        for dx, dy, k in steps:
            q = (p[0] + dx, p[1] + dy)
            if not (0 <= q[0] < w and 0 <= q[1] < h):
                continue
            nd = d0 + k * cost(q)
            if nd < dist.get(q, 1e18):
                dist[q] = nd
                prev[q] = p
                heapq.heappush(heap, (nd + math.dist(q, g), q))
    if g not in prev and g != s:
        raise SystemExit("no path")
    path = [g]
    while path[-1] != s:
        path.append(prev[path[-1]])
    path.reverse()

    def simplify(pts, eps):
        if len(pts) < 3:
            return pts
        a, b = pts[0], pts[-1]
        best, idx = 0.0, 0
        for i in range(1, len(pts) - 1):
            p = pts[i]
            ab = math.dist(a, b) or 1e-9
            dd = abs((b[0] - a[0]) * (a[1] - p[1]) - (a[0] - p[0]) * (b[1] - a[1])) / ab
            if dd > best:
                best, idx = dd, i
        if best <= eps:
            return [a, b]
        return simplify(pts[:idx + 1], eps)[:-1] + simplify(pts[idx:], eps)

    pts = simplify(path, 4.0)
    world = [[round(v.xn - q[1] / v.ppy), round(v.yw - q[0] / v.ppy)] for q in pts]
    off = sum(1 for q in path if cost(q) > 1.0)
    print(json.dumps(world))
    print(f"# {len(world)} points, {length(world):.0f} yd, {off}/{len(path)} px off the road colour")


def adt_doodads(mapid, col, row):
    """ADT props (MDDF) of one tile, from the client's archives (patch-7 first): [(name, x, y, z, scale)]."""
    import struct
    sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "mpqpy"))
    from mpq import MpqArchive
    data_dir = os.environ.get("MSUI_DATA", os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "GameData", "Data"))
    if not hasattr(adt_doodads, "arcs"):
        adt_doodads.arcs = [MpqArchive(os.path.join(data_dir, n)) for n in (() if os.environ.get("MSUI_STOCK") else ("patch-7.MPQ",)) + ("patch-2.MPQ", "patch.MPQ", "terrain.MPQ")
                            if os.path.exists(os.path.join(data_dir, n))]
    path = f"World\\Maps\\{FOLDER[mapid]}\\{FOLDER[mapid]}_{col}_{row}.adt"
    adt = None
    for a in adt_doodads.arcs:
        try:
            adt = a.read_file(path)
        except Exception:
            adt = None
        if adt:
            break
    if not adt:
        return []
    o, chunks = 0, {}
    while o < len(adt) - 8:
        tag = adt[o:o + 4][::-1].decode("latin1")
        size = struct.unpack_from("<I", adt, o + 4)[0]
        chunks.setdefault(tag, adt[o + 8:o + 8 + size])
        o += 8 + size
    mmdx, mmid, mddf = chunks.get("MMDX", b""), chunks.get("MMID", b""), chunks.get("MDDF", b"")
    offs = struct.unpack(f"<{len(mmid) // 4}I", mmid)
    out = []
    for k in range(len(mddf) // 36):
        nid, uid, px, py, pz, rx, ry, rz, sc, fl = struct.unpack_from("<2I6f2H", mddf, k * 36)
        s = offs[nid]
        out.append((mmdx[s:mmdx.index(b"\0", s)].decode("latin1").split("\\")[-1], 17066.66667 - pz, 17066.66667 - px, py, sc / 1024))
    return out


def read_adt(mapid, col, row):
    adt_doodads(mapid, col, row)                       # opens the archives once
    path = f"World\\Maps\\{FOLDER[mapid]}\\{FOLDER[mapid]}_{col}_{row}.adt"
    for a in adt_doodads.arcs:
        try:
            data = a.read_file(path)
        except Exception:
            data = None
        if data:
            return data
    return None


def texture_weights(mapid, col, row, substr):
    """Visible weight (0..1) of every texture whose path contains SUBSTR, per alpha cell of one ADT tile:
    {(chunkCornerX, chunkCornerY): 64x64 list}. Layers blend in order, so layer i shows a_i * prod(1 - a_j, j > i)
    and the base layer shows prod(1 - a_j). This is where the game PAINTS its roads - the racing line follows it."""
    import struct
    adt = read_adt(mapid, col, row)
    if not adt:
        return {}
    o, tex, out = 0, [], {}
    while o < len(adt) - 8:
        tag = adt[o:o + 4][::-1].decode("latin1")
        size = struct.unpack_from("<I", adt, o + 4)[0]
        body = adt[o + 8:o + 8 + size]
        if tag == "MTEX":
            tex = [t.decode("latin1").lower() for t in body.split(b"\0") if t]
        elif tag == "MCNK":
            n_layers = struct.unpack_from("<I", body, 12)[0]
            ofs_layer, ofs_alpha = struct.unpack_from("<I", body, 28)[0], struct.unpack_from("<I", body, 36)[0]
            px, py = struct.unpack_from("<2f", body, 0x68)
            layers = []
            for k in range(n_layers):
                tid, flags, aofs, _ = struct.unpack_from("<4I", adt, o + ofs_layer + 8 + 16 * k)
                alpha = None
                if k > 0 and flags & 0x100:
                    raw = adt[o + ofs_alpha + 8 + aofs:o + ofs_alpha + 8 + aofs + 2048]
                    alpha = [0.0] * 4096
                    for i, b in enumerate(raw):
                        alpha[2 * i] = (b & 0xF) / 15.0
                        alpha[2 * i + 1] = (b >> 4) / 15.0
                layers.append((substr.lower() in (tex[tid] if tid < len(tex) else ""), alpha))
            weight = [0.0] * 4096
            for k, (match, _) in enumerate(layers):
                if not match:
                    continue
                for i in range(4096):
                    w = 1.0 if k == 0 else (layers[k][1][i] if layers[k][1] else 0.0)
                    for j in range(k + 1, len(layers)):
                        a = layers[j][1]
                        if a:
                            w *= 1.0 - a[i]
                    weight[i] += w
            out[(px, py)] = weight
        o += 8 + size
    return out


def road_grid(mapid, x0, x1, y0, y1, substr):
    """Road weight on a 1-yd grid over x in [x0, x1), y in [y0, y1): grid[i][j] at (x0 + i, y0 + j)."""
    cell = TILE / 16 / 64
    W, H = int(y1 - y0), int(x1 - x0)
    g = [[0.0] * W for _ in range(H)]
    tiles = {(math.floor(32 - y / TILE), math.floor(32 - x / TILE)) for x in (x0, x1) for y in (y0, y1)}
    tiles = {(c, r) for c in range(min(t[0] for t in tiles), max(t[0] for t in tiles) + 1)
             for r in range(min(t[1] for t in tiles), max(t[1] for t in tiles) + 1)}
    for c, r in tiles:
        for (px, py), wgt in texture_weights(mapid, c, r, substr).items():
            for ar in range(64):
                for ac in range(64):
                    x, y = px - (ar + 0.5) * cell, py - (ac + 0.5) * cell
                    i, j = int(x - x0), int(y - y0)
                    if 0 <= i < H and 0 <= j < W:
                        g[i][j] = max(g[i][j], wgt[ar * 64 + ac])
    return g


def cmd_road(out, mapid, xn, xs, yw, ye, substr):
    """Render the road weight (texture SUBSTR) of a box, white = road, at 1 px/yd, beside nothing - compare with view."""
    mapid, xn, xs, yw, ye = int(mapid), float(xn), float(xs), float(yw), float(ye)
    g = road_grid(mapid, xs, xn, ye, yw, substr)
    img = Image.new("L", (len(g[0]), len(g)))
    px = img.load()
    for i in range(len(g)):
        for j in range(len(g[0])):
            px[len(g[0]) - 1 - j, len(g) - 1 - i] = int(255 * min(1.0, g[i][j]))
    img.save(out)
    print(f"{out}: {img.size}")


def cmd_trace_road(mapid, x0, y0, x1, y1, substr, pad=150, via=""):
    """Follow the PAINTED road (texture SUBSTR, e.g. DeadwindPassShale) from (x0,y0) to (x1,y1), optionally through
    VIA points "x,y;x,y": A* over the 1-yd road-weight grid, off-road costing 25x; prints the simplified route
    (kept on the road: every cut is checked against the weight). The minimap-colour `trace` cannot tell a grey
    road from grey rock; the texture layer can."""
    import heapq
    mapid, pad = int(mapid), float(pad)
    pts = [(float(x0), float(y0))] + [tuple(map(float, v.split(","))) for v in via.split(";") if v] + [(float(x1), float(y1))]
    gx0, gx1 = min(p[0] for p in pts) - pad, max(p[0] for p in pts) + pad
    gy0, gy1 = min(p[1] for p in pts) - pad, max(p[1] for p in pts) + pad
    g = road_grid(mapid, gx0, gx1, gy0, gy1, substr)
    H, W = len(g), len(g[0])

    def snap(x, y, r=25):
        i0, j0 = int(x - gx0), int(y - gy0)
        best = max(((g[i][j], -((i - i0) ** 2 + (j - j0) ** 2), (i, j)) for i in range(max(0, i0 - r), min(H, i0 + r + 1))
                    for j in range(max(0, j0 - r), min(W, j0 + r + 1))), default=None)
        return best[2]

    def cost(i, j):
        return 1.0 + 25.0 * (1.0 - min(1.0, g[i][j])) ** 2

    def astar(s, t):
        dist, prev, heap = {s: 0.0}, {}, [(0.0, s)]
        while heap:
            f, p = heapq.heappop(heap)
            if p == t:
                break
            if f - math.dist(p, t) > dist[p] + 1e-6:
                continue
            for di, dj, k in ((1, 0, 1), (-1, 0, 1), (0, 1, 1), (0, -1, 1), (1, 1, 1.414), (1, -1, 1.414), (-1, 1, 1.414), (-1, -1, 1.414)):
                q = (p[0] + di, p[1] + dj)
                if 0 <= q[0] < H and 0 <= q[1] < W:
                    nd = dist[p] + k * cost(*q)
                    if nd < dist.get(q, 1e18):
                        dist[q], prev[q] = nd, p
                        heapq.heappush(heap, (nd + math.dist(q, t), q))
        path = [t]
        while path[-1] != s:
            path.append(prev[path[-1]])
        return path[::-1]

    cells = [snap(*p) for p in pts]
    full = [cells[0]]
    for a, b in zip(cells, cells[1:]):
        full += astar(a, b)[1:]

    def on_road(a, b):
        n = int(math.dist(a, b)) + 1
        return all(g[int(round(a[0] + (b[0] - a[0]) * t / n))][int(round(a[1] + (b[1] - a[1]) * t / n))] >= 0.35 for t in range(n + 1))

    out, i = [full[0]], 0
    while i < len(full) - 1:
        j = min(len(full) - 1, i + 60)
        while j > i + 1 and not on_road(full[i], full[j]):
            j -= 1
        out.append(full[j])
        i = j
    world = [[round(gx0 + p[0] + 0.5), round(gy0 + p[1] + 0.5)] for p in out]
    off = sum(1 for p in full if g[p[0]][p[1]] < 0.35)
    print(json.dumps(world))
    print(f"# {len(world)} points, {length(world):.0f} yd, {off}/{len(full)} yd off the painted road")


def seg_dist(p, a, b):
    ax, ay, bx, by = a[0], a[1], b[0], b[1]
    dx, dy = bx - ax, by - ay
    L = dx * dx + dy * dy
    t = 0 if L == 0 else max(0, min(1, ((p[0] - ax) * dx + (p[1] - ay) * dy) / L))
    return math.dist(p, (ax + dx * t, ay + dy * t))


def cmd_props(course_path, radius=4):
    """Props (ADT doodads: trees, rocks, roots) standing within RADIUS yd of each leg's route - a kart stops dead
    on a trunk the terrain heights never show (2026-10-04, Deadwind). Clear them with a graded lane (path clear)
    or route around them."""
    radius = float(radius)
    course = json.load(open(course_path))
    for i, leg in enumerate(course["legs"]):
        route = leg["route"] + ([[leg["portal"]["x"], leg["portal"]["y"]]] if leg.get("portal") else [])
        tiles = {(math.floor(32 - y / TILE), math.floor(32 - x / TILE)) for x, y in route}
        tiles |= {(c + dc, r + dr) for c, r in tiles for dc in (-1, 0, 1) for dr in (-1, 0, 1)}
        hits = []
        for c, r in sorted(tiles):
            for name, x, y, z, sc in adt_doodads(leg["map"], c, r):
                d = min(seg_dist((x, y), route[k], route[k + 1]) for k in range(len(route) - 1))
                if d <= radius:
                    hits.append((d, name, x, y, z, sc))
        print(f"leg {i} {leg['name']}: {len(hits)} prop(s) within {radius:g} yd of the route")
        for d, name, x, y, z, sc in sorted(hits, key=lambda h: h[0]):
            print(f"   {d:4.1f} yd  ({x:.1f}, {y:.1f}, {z:.1f}) x{sc:.2f}  {name}")


def cmd_measure(course_path):
    course = json.load(open(course_path))
    speed = float(course.get("speed", 21))
    total = 0.0
    for i, leg in enumerate(course["legs"]):
        n = length(leg["route"])
        total += n
        print(f"leg {i} {leg['name']:<28} map {leg['map']}  {n:7.0f} yd  {n / speed:6.1f} s")
    laps = int(course.get("laps", 3))
    print(f"lap {total:.0f} yd = {total / speed:.1f} s at {speed} yd/s; race of {laps} laps = {laps * total / speed / 60:.1f} min")


def main():
    a = sys.argv[1:]
    if not a:
        print(__doc__ or open(__file__).read().split("import")[0])
        return
    {"view": cmd_view, "draw": cmd_draw, "measure": cmd_measure, "trace": cmd_trace, "props": cmd_props, "road": cmd_road, "trace-road": cmd_trace_road}[a[0]](*a[1:])


if __name__ == "__main__":
    main()
