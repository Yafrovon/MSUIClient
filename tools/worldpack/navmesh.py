# The SERVER's navmesh, offline: where creatures and party bots can walk (vmangos .mmtile = MmapTileHeader + Detour v7
# tile data), for planning spawns, packs, patrols and boss rooms BEFORE a publish (shared_docs/WORLD_BUILDER.md §9).
# Two points on different walk-connected components have no path: ".mmap path" INCOMPLETE, a boss there evades
# "target unreachable", a group can never walk to it - map 801's first boss ward was such an island (2026-09-27).
# The verifier's G15 (MangosSuperUI WorldPackNavMesh) is the same rule after a publish; this is the planning half.
# Proven: every point's component here matched the live ".mmap path" answer (complete = same, incomplete = other).
#
#   python tools/worldpack/navmesh.py --map 801 [--mmaps DIR] check '[["boss",32,328],["pack",80,329,68]]'
#       component of each point (MAIN = the map's largest), its area, the poly height, and "open6" = the share of a
#       6-yd disc around it that is main walkable ground (a boss room wants ~0.9+, a doorway reads ~0.5)
#   python tools/worldpack/navmesh.py --map 801 draw OUT.png xNorth xSouth yWest yEast [ppy] ['{"marks":[[name,x,y,colour]],
#       "routes":[[[x,y],...]], "rings":[[x,y,r,colour]]}']
#       the main walkable ground (green) vs everything else (dark) with a 10-yd grid - pick spots on it, not on roofs
#   --mmaps DIR: a folder with the map's .mmtile files; default: fetch them from the box (ssh 192.168.0.2, the
#   server's run/data/mmaps) into scratch/mmaps-cache/ (git-ignored). A continent needs --cells gx,gy;...
#   (grid cells = 32 - x/533.33, 32 - y/533.33) - it has ~1000 tiles.
# WoW (x north, y west, z up) = recast (X = y, Y = z, Z = x).
import glob, json, math, os, struct, subprocess, sys

EXT, GROUND, WATER, STEEP = 0x8000, 1, 8, 16


def load_tile(data):
    o = 20
    h = struct.unpack_from('<15i3f3f3ff', data, o)
    if h[0] != (ord('D') << 24 | ord('N') << 16 | ord('A') << 8 | ord('V')) or h[1] != 7:
        return None
    t = {'tx': h[2], 'ty': h[3], 'climb': h[17]}
    npoly, nvert = h[6], h[7]
    o += 100
    t['verts'] = [struct.unpack_from('<3f', data, o + 12 * i) for i in range(nvert)]
    o += 12 * nvert
    polys = []
    for i in range(npoly):
        f = struct.unpack_from('<I6H6HHBB', data, o + 32 * i)
        vc = f[14]
        polys.append((f[1:1 + vc], f[7:7 + vc], f[13], f[15] >> 6))
    t['polys'] = polys
    return t


class Nav:
    def __init__(self, blobs):
        self.tiles = [t for t in (load_tile(b) for b in blobs) if t]
        n = 0
        for t in self.tiles:
            t['base'] = n
            n += len(t['polys'])
        self.parent = list(range(n))
        ext = {}
        for t in self.tiles:
            for i, (vs, ns, flags, typ) in enumerate(t['polys']):
                if typ:
                    continue
                for j, nb in enumerate(ns):
                    if not nb:
                        continue
                    if nb & EXT:
                        ext.setdefault((t['tx'], t['ty'], nb & 0xff), []).append(
                            (t['base'] + i, t['verts'][vs[j]], t['verts'][vs[(j + 1) % len(vs)]], t['climb']))
                    else:
                        self.union(t['base'] + i, t['base'] + nb - 1)
        off = {0: (1, 0), 2: (0, 1), 4: (-1, 0), 6: (0, -1)}
        for (tx, ty, side), edges in ext.items():
            dx, dy = off.get(side, (0, 0))
            other = ext.get((tx + dx, ty + dy, (side + 4) & 7))
            if not other:
                continue
            ax = 2 if side in (0, 4) else 0
            for g, a0, a1, climb in edges:
                lo, hi = sorted((a0[ax], a1[ax]))
                for g2, b0, b1, _ in other:
                    olo, ohi = max(lo, min(b0[ax], b1[ax])), min(hi, max(b0[ax], b1[ax]))
                    if ohi - olo < 0.01:
                        continue
                    def hat(p, q, s):
                        d = q[ax] - p[ax]
                        return p[1] + (q[1] - p[1]) * (0 if abs(d) < 1e-6 else (s - p[ax]) / d)
                    if all(abs(hat(a0, a1, s) - hat(b0, b1, s)) <= climb + 0.5 for s in (olo, ohi)):
                        self.union(g, g2)
        self.area = {}
        for t in self.tiles:
            for i, p in enumerate(t['polys']):
                r = self.find(t['base'] + i)
                self.area[r] = self.area.get(r, 0.0) + self.poly_area(t, p)
        self.main = max(self.area, key=self.area.get) if self.area else None

    def find(self, a):
        p = self.parent
        while p[a] != a:
            p[a] = p[p[a]]
            a = p[a]
        return a

    def union(self, a, b):
        a, b = self.find(a), self.find(b)
        if a != b:
            self.parent[a] = b

    @staticmethod
    def poly_area(t, p):
        vs = [t['verts'][v] for v in p[0]]
        return abs(sum(vs[k][0] * vs[(k + 1) % len(vs)][2] - vs[(k + 1) % len(vs)][0] * vs[k][2] for k in range(len(vs)))) / 2

    def polys(self):
        for t in self.tiles:
            for i, p in enumerate(t['polys']):
                if p[3] == 0 and p[2] & (GROUND | WATER):
                    yield t, i, p

    def at(self, x, y, z=None):
        """(component, height, flags) of the walkable poly under WoW (x, y) nearest z (or the highest)."""
        X, Z = y, x
        hits = []
        for t, i, p in self.polys():
            vs = [t['verts'][v] for v in p[0]]
            if not vs or X < min(v[0] for v in vs) or X > max(v[0] for v in vs) or Z < min(v[2] for v in vs) or Z > max(v[2] for v in vs):
                continue
            inside = False
            for k in range(len(vs)):
                a, b = vs[k], vs[k - 1]
                if (a[2] > Z) != (b[2] > Z) and X < (b[0] - a[0]) * (Z - a[2]) / (b[2] - a[2]) + a[0]:
                    inside = not inside
            if inside:
                hits.append((self.find(t['base'] + i), sum(v[1] for v in vs) / len(vs), p[2]))
        if not hits:
            return None
        return min(hits, key=lambda h: abs(h[1] - z)) if z is not None else max(hits, key=lambda h: h[1])

    def mask(self, x0, x1, y0, y1):
        """1-yd raster of the main component's walkable, non-steep, dry ground over x in [x0,x1), y in [y0,y1)."""
        W, H = int(y1 - y0), int(x1 - x0)
        m = [[False] * W for _ in range(H)]
        for t, i, p in self.polys():
            if self.find(t['base'] + i) != self.main or p[2] & (STEEP | WATER):
                continue
            vs = [t['verts'][v] for v in p[0]]
            xs, ys = [v[2] for v in vs], [v[0] for v in vs]
            for gx in range(max(int(math.floor(min(xs))), int(x0)), min(int(math.ceil(max(xs))), int(x1) - 1) + 1):
                for gy in range(max(int(math.floor(min(ys))), int(y0)), min(int(math.ceil(max(ys))), int(y1) - 1) + 1):
                    cx, cy, inside = gx + 0.5, gy + 0.5, False
                    for k in range(len(vs)):
                        if (xs[k] > cx) != (xs[k - 1] > cx) and cy < (ys[k - 1] - ys[k]) * (cx - xs[k]) / (xs[k - 1] - xs[k]) + ys[k]:
                            inside = not inside
                    if inside:
                        m[gx - int(x0)][gy - int(y0)] = True
        return m


def fetch(mapid, cells):
    cache = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'scratch', 'mmaps-cache', str(mapid))   # git-ignored
    os.makedirs(cache, exist_ok=True)
    names = f'{mapid:03d}*.mmtile' if not cells else ' '.join(
        f'{mapid:03d}{gx + dx:02d}{gy + dy:02d}.mmtile' for gx, gy in cells for dx in (-1, 0, 1) for dy in (-1, 0, 1))
    tar = subprocess.run(['ssh', '192.168.0.2', f'cd ~/vmangos/run/data/mmaps && tar cf - {names} 2>/dev/null'], capture_output=True).stdout
    subprocess.run(['tar', 'xf', '-', '-C', cache], input=tar)
    return cache


def main():
    a = sys.argv[1:]
    def opt(k, d=None):
        return a[a.index(k) + 1] if k in a else d
    mapid = int(opt('--map', '801'))
    cells = [tuple(int(v) for v in c.split(',')) for c in opt('--cells', '').split(';') if c]
    folder = opt('--mmaps') or fetch(mapid, cells)
    blobs = [open(f, 'rb').read() for f in sorted(glob.glob(os.path.join(folder, f'{mapid:03d}*.mmtile')))]
    nav = Nav(blobs)
    rest = [x for i, x in enumerate(a) if not x.startswith('--') and (i == 0 or not a[i - 1].startswith('--'))]
    cmd = rest[0] if rest else 'check'
    print(f'map {mapid}: {len(nav.tiles)} tile(s), main component {nav.area.get(nav.main, 0):.0f} sq yd')
    if cmd == 'check':
        pts = json.loads(rest[1]) if len(rest) > 1 else []
        for name, x, y, *z in pts:
            c = nav.at(x, y, z[0] if z else None)
            if c is None:
                print(f'{name:>20} ({x},{y}): NO NAVMESH'); continue
            m = nav.mask(x - 7, x + 7, y - 7, y + 7)
            cells6 = [(dx, dy) for dx in range(-6, 7) for dy in range(-6, 7) if dx * dx + dy * dy <= 36]
            open6 = sum(m[dx + 7][dy + 7] for dx, dy in cells6) / len(cells6)
            where = 'MAIN' if c[0] == nav.main else f'ISLAND {nav.area[c[0]]:.0f} sq yd'
            print(f'{name:>20} ({x},{y}): {where:<22} h={c[1]:.1f} open6={open6:.2f}')
    elif cmd == 'draw':
        from PIL import Image, ImageDraw
        out, xN, xS, yW, yE = rest[1], *map(float, rest[2:6])
        ppy = float(rest[6]) if len(rest) > 6 else 3
        spec = json.loads(rest[7]) if len(rest) > 7 else {}
        m = nav.mask(xS, xN, yE, yW)
        W, H = int((yW - yE) * ppy), int((xN - xS) * ppy)
        img = Image.new('RGB', (W + 50, H + 30), (20, 20, 20))
        px = img.load()
        for iy in range(H):
            gx = int(math.floor(xN - iy / ppy - xS))
            for ix in range(W):
                gy = int(math.floor(yW - ix / ppy - yE))
                on = 0 <= gx < len(m) and 0 <= gy < len(m[0]) and m[gx][gy]
                px[ix + 50, iy + 30] = (60, 150, 70) if on else (70, 40, 40)
        d = ImageDraw.Draw(img)
        P = lambda x, y: (50 + (yW - y) * ppy, 30 + (xN - x) * ppy)
        for x in range(int(xS // 10 * 10), int(xN) + 1, 10):
            yy = P(x, 0)[1]
            if 30 <= yy <= H + 30:
                d.line([(50, yy), (W + 50, yy)], fill=(200, 200, 200) if x % 50 == 0 else (90, 90, 90))
                if x % 50 == 0: d.text((2, yy - 6), f'x{x}', fill='white')
        for y in range(int(yE // 10 * 10), int(yW) + 1, 10):
            xx = P(0, y)[0]
            if 50 <= xx <= W + 50:
                d.line([(xx, 30), (xx, H + 30)], fill=(200, 200, 200) if y % 50 == 0 else (90, 90, 90))
                if y % 50 == 0: d.text((xx - 10, 2), f'y{y}', fill='white')
        for r in spec.get('routes', []):
            d.line([P(*p) for p in r] + [P(*r[0])], fill='yellow', width=2)
        for x, y, rad, col in spec.get('rings', []):
            cx, cy = P(x, y)
            d.ellipse([cx - rad * ppy, cy - rad * ppy, cx + rad * ppy, cy + rad * ppy], outline=col)
        from PIL import ImageFont
        try:
            font = ImageFont.truetype('arial.ttf', 13)
        except OSError:
            font = None
        for name, x, y, col in spec.get('marks', []):
            cx, cy = P(x, y)
            d.ellipse([cx - 4, cy - 4, cx + 4, cy + 4], fill=col, outline='black')
            if name:
                d.text((cx + 7, cy - 8), name, fill=col, font=font, stroke_width=2, stroke_fill='black')
        img.save(out)
        print('saved', out, img.size, '(delete it once read)')


if __name__ == '__main__':
    main()
