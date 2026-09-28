# Where a pack's land sits on Blizzard's PAINTED continent world map, at true scale - and what vanilla's hover map says
# there. Read-only: the client's archives (GameData/Data, patch-7 first) through tools/mpqpy.
#
#   python tools/worldpack/worldmap-probe.py OUT.png [--cols 27-30] [--rows 35-39] [--crop x0,y0,x1,y1] [--scale 4]
#
# OUT.png : the 1002x668 continent art (Interface\WorldMap\Azeroth\Azeroth1..12.blp) with ALL dry land of the given
#           ADT tiles as published (patch-7 over stock) in red and the ADT tile grid, cropped/zoomed. The defaults are
#           the Gilneas block; where the red leaves the painted landmass, the land is off the painted map.
# stdout  : the continent + zone WorldMapArea rows, and the ZMP (Interface\WorldMap\Azeroth.zmp) area ids of the window
#           with their AreaTable names. The ZMP is 128x128 in WORLD coordinates: cell = half an ADT tile, so ADT
#           tile (c, r) = ZMP cells (2c..2c+1, 2r..2r+1). Hover ownership and the zone name come from it.
# Projection: continent px = ((left - worldY) / (left - right) * 1002, (top - worldX) / (top - bottom) * 668) with the
# continent's WorldMapArea row (map 0: left 16000, right -19199.9, top 7466.6, bottom -16000).
import io, json, os, struct, sys
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'mpqpy'))
from mpq import MpqArchive
from PIL import Image, ImageDraw

REPO = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
DATA = os.path.join(REPO, 'GameData', 'Data')
ORDER = ['patch-7.MPQ', 'patch-4.MPQ', 'patch-2.MPQ', 'patch.MPQ', 'interface.MPQ', 'texture.MPQ', 'terrain.MPQ',
         'misc.MPQ', 'dbc.MPQ', 'base.MPQ']
_arch = {}
ADT_DIR = None


def read(path):
    if ADT_DIR and path.lower().endswith('.adt'):
        local = os.path.join(ADT_DIR, *path.replace('/', '\\').split('\\'))
        if os.path.isfile(local):
            with open(local, 'rb') as f:
                return f.read()
    order = [n for n in ORDER if n != 'patch-7.MPQ'] if ADT_DIR and path.lower().endswith('.adt') else ORDER
    for n in order:
        if n not in _arch:
            try: _arch[n] = MpqArchive(os.path.join(DATA, n))
            except Exception: _arch[n] = None
        a = _arch[n]
        if a and a.has_file(path):
            return a.read_file(path)
    return None


def dbc(data):
    _, n, _, size, _ = struct.unpack_from('<4s4i', data, 0)
    return [data[20 + i * size:20 + (i + 1) * size] for i in range(n)], data[20 + n * size:]


def text(strings, off):
    return strings[off:strings.index(b'\0', off)].decode('latin1')


def published_land(col, row):
    """World points of dry land in the published ADT (patch-7 over stock): outer grid, every 2nd vertex."""
    data = read(rf'World\Maps\Azeroth\Azeroth_{col}_{row}.adt')
    if not data:
        return []
    pos, mcin = 0, None
    while pos < len(data):
        if data[pos:pos + 4] == b'NICM': mcin = pos + 8
        pos += 8 + struct.unpack_from('<I', data, pos + 4)[0]
    out = []
    for i in range(256):
        off = struct.unpack_from('<I', data, mcin + i * 16)[0]
        h = off + 8
        flags, ix, iy = struct.unpack_from('<3I', data, h)
        mcvt = struct.unpack_from('<I', data, h + 0x14)[0]
        mclq = struct.unpack_from('<I', data, h + 0x60)[0]
        zb = struct.unpack_from('<3f', data, h + 0x68)[2]
        hs = struct.unpack_from('<145f', data, off + mcvt + 8)
        level = None
        if flags & 0x3C and mclq > 0:
            q = off + mclq + (8 if data[off + mclq:off + mclq + 4] == b'QLCM' else 0)
            level = struct.unpack_from('<f', data, q + 4)[0]
        for r in range(0, 9, 2):
            for c in range(0, 9, 2):
                z = zb + hs[r * 17 + c]
                if z > 0.5 and not (level is not None and level > z):
                    out.append(((32 - row) * 533.33333 - (iy * 8 + r) * 4.1666667, (32 - col) * 533.33333 - (ix * 8 + c) * 4.1666667))
    return out


def main():
    global ADT_DIR
    a = sys.argv[1:]
    out = a[0] if a and not a[0].startswith('--') else 'worldmap-probe.png'
    opt = lambda k, d: a[a.index(k) + 1] if k in a else d
    ADT_DIR = opt('--adt-dir', None)
    c0, c1 = map(int, opt('--cols', '27-30').split('-'))
    r0, r1 = map(int, opt('--rows', '35-39').split('-'))
    crop = tuple(map(int, opt('--crop', '320,180,560,400').split(',')))
    scale = int(opt('--scale', '4'))
    recs, strs = dbc(read(r'DBFilesClient\WorldMapArea.dbc'))
    cont = None
    for r in recs:
        wid, mapid, area, name = struct.unpack_from('<4i', r, 0)
        l, rr, t, b = struct.unpack_from('<4f', r, 16)
        if mapid == 0:
            print(f'WorldMapArea {wid:5d} area {area:5d} {text(strs, name):16s} left {l:9.1f} right {rr:9.1f} top {t:9.1f} bottom {b:9.1f}')
            if area == 0: cont = (l, rr, t, b)
    l, rr, t, b = cont
    W, H = 1002, 668
    px = lambda x, y: ((l - y) / (l - rr) * W, (t - x) / (t - b) * H)
    img = Image.new('RGB', (1024, 768))
    for i in range(12):
        img.paste(Image.open(io.BytesIO(read(rf'Interface\WorldMap\Azeroth\Azeroth{i + 1}.blp'))).convert('RGB'), ((i % 4) * 256, (i // 4) * 256))
    img = img.crop((0, 0, W, H))
    ov = img.copy(); d = ImageDraw.Draw(ov)
    for col in range(c0, c1 + 1):
        for row in range(r0, r1 + 1):
            for x, y in published_land(col, row): d.point(px(x, y), fill=(220, 0, 0))
    img = Image.blend(img, ov, 0.5)
    outline = opt('--outline', None)
    if outline:
        with open(outline, encoding='utf-8-sig') as f:
            polygon = json.load(f)['polygon']
        points = [px(x, y) for x, y in polygon]
        ImageDraw.Draw(img).line(points + points[:1], fill=(0, 255, 230), width=1)
    z = img.crop(crop).resize(((crop[2] - crop[0]) * scale, (crop[3] - crop[1]) * scale), Image.NEAREST)
    d = ImageDraw.Draw(z)
    for col in range(0, 64):
        X = (px(0, (32 - col) * 533.3333)[0] - crop[0]) * scale
        if 0 <= X <= z.width: d.line([(X, 0), (X, z.height)], fill=(255, 255, 255)); d.text((X + 2, 2), str(col), fill='white')
    for row in range(0, 64):
        Y = (px((32 - row) * 533.3333, 0)[1] - crop[1]) * scale
        if 0 <= Y <= z.height: d.line([(0, Y), (z.width, Y)], fill=(255, 255, 255)); d.text((2, Y + 2), str(row), fill='white')
    z.save(out)
    print('saved', out, '(delete it once read)')
    # ZMP window
    ids = struct.unpack('<16384I', read(r'Interface\WorldMap\Azeroth.zmp'))
    recs, strs = dbc(read(r'DBFilesClient\AreaTable.dbc'))
    names = {}
    for r in recs:
        try: names[struct.unpack_from('<I', r, 0)[0]] = text(strs, struct.unpack_from('<I', r, 44)[0])
        except Exception: pass
    legend = {}
    sym = lambda v: '.' if v == 0 else legend.setdefault(v, 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'[len(legend) % 62])
    print('ZMP  ADT col ' + ''.join(f'{c:<2d}' for c in range(c0, c1 + 1)))
    for zr in range(2 * r0, 2 * r1 + 2):
        print(f'row {zr / 2:5.1f}   ' + ''.join(sym(ids[zr * 128 + zc]) for zc in range(2 * c0, 2 * c1 + 2)))
    for v, s in legend.items(): print(f'  {s} = area {v} {names.get(v, "?")}')


if __name__ == '__main__':
    main()
