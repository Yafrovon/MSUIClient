# The WoW Karting go-kart: a real vanilla M2 (MD20 v256) built from code, so the kart is reproducible and
# tunable (shared_docs/WOW_KARTING.md). Mario Kart 64's low go-kart, built out of goblin parts the 1.12
# archives already ship: the Mirage Raceway rocket-cart atlas (red planks, the shark-mouth nose, the plank
# with XXX, the skull hex, the green pennant), the TNT-wagon wheel and the Shadowfang riveted metal.
#
#   python tools/worldpack/examples/karting/kart-model.py <out.m2>
#
# Layout rules are the client's READERS (MSUIClient/Formats/M2Reader.cs; the same contract SketchWriter.cs
# follows): M2Array offsets absolute, flat 28-byte vanilla tracks with one ranges entry per sequence,
# rotation keys = four raw floats, timestamps absolute on the model timeline, view 0 embedded. The file
# frame is x forward, y left, z up.
#
# A MOUNT needs: sequences 0 Stand, 4 Walk, 5 Run (CreatureRenderer.SelectMountClip; rate = speed /
# MoveSpeed, clamped 0.25-3), attachment 0 = the rider's seat (MountSeatAttachment), the root at the
# origin (a baked root offset draws the car yards off its rider - see the stock rocket cars).
import math, os, struct, sys

TEX_ATLAS = r"World\Generic\Goblin\PassiveDoodads\GoblinRocketCarts\RocketCart01.blp"
TEX_WHEEL = r"World\Generic\Goblin\PassiveDoodads\GoblinTNTWagon\GoblinWagonWheel01.blp"
TEX_METAL = r"World\Generic\GOBLIN\PASSIVEDOODADS\GOBLINROCKETCARTS\BM_SHDWFANG_METALWALL_01_GRAY.BLP"
# The tyre tread is the one texture the archives have no fit for: generated here (tread_blp) and shipped
# next to the model as a pack asset.
TEX_TREAD = r"Creature\SUIKart\SUIKartTread.blp"
TEXTURES = [TEX_ATLAS, TEX_WHEEL, TEX_METAL, TEX_TREAD]
ATLAS, WHEEL, METAL, TREAD = 0, 1, 2, 3

# RocketCart01.blp regions (u0, v0, u1, v1), read off the decoded 256x256 image.
RED = (0.30, 0.02, 0.66, 0.48)        # red painted planks between the ropes
RED_ROPE = (0.0, 0.0, 1.0, 0.5)       # the whole red band with its two lashings
SHARK = (0.0, 0.5, 0.375, 1.0)        # the shark-mouth nose
PLANK = (0.38, 0.5, 0.62, 1.0)        # wood plank with the XXX brand
PORTHOLE = (0.63, 0.5, 1.0, 0.69)     # riveted porthole strip
SKULL = (0.63, 0.70, 0.84, 1.0)       # the skull hex plate
PENNANT = (0.85, 0.70, 1.0, 1.0)      # green pennant

# Colours (batch colorIndex): 0 = rubber (darkens the metal for tyres), 1 = brass trim.
COLOURS = [(0.22, 0.20, 0.19), (0.95, 0.78, 0.45)]
RUBBER, BRASS = 0, 1

# Bones: 0 root (static, origin), 1 body (engine shake, carries the seat), 2-5 wheels FL FR RL RR.
FRONT_X, REAR_X = 1.22, -1.0
FRONT_R, REAR_R = 0.30, 0.40
FRONT_W, REAR_W = 0.22, 0.30
FRONT_Y, REAR_Y = 0.80, 0.86
WHEELS = [(FRONT_X, FRONT_Y, FRONT_R, FRONT_W), (FRONT_X, -FRONT_Y, FRONT_R, FRONT_W),
          (REAR_X, REAR_Y, REAR_R, REAR_W), (REAR_X, -REAR_Y, REAR_R, REAR_W)]
BODY_PIVOT = (0.0, 0.0, 0.40)

# The rider's hips: on the seat cushion, legs inside the tub (side pods and cowl hide them).
SEAT = (-0.32, 0.0, 0.86)

# Sequences: (id, start, end, moveSpeed). MoveSpeed = the ground speed the clip is authored at; the kart
# races at 21 yd/s, so Run is authored at 14 (rate 1.5 at race speed, inside the 0.25-3 clamp).
SEQUENCES = [(0, 0, 1000, 0.0), (4, 1100, 2100, 2.5), (5, 2200, 3200, 14.0)]


def sub(a, b): return (a[0] - b[0], a[1] - b[1], a[2] - b[2])
def add(a, b): return (a[0] + b[0], a[1] + b[1], a[2] + b[2])
def mul(a, k): return (a[0] * k, a[1] * k, a[2] * k)
def cross(a, b): return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])
def dot(a, b): return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
def norm(a):
    l = math.sqrt(dot(a, a)) or 1.0
    return (a[0] / l, a[1] / l, a[2] / l)


class Mesh:
    """Triangles grouped by material (texture, colour, bone, two-sided) - one submesh + batch each."""

    def __init__(self):
        self.groups = {}

    def tri(self, key, a, b, c, ua, ub, uc, n=None):
        n = n or norm(cross(sub(b, a), sub(c, a)))
        self.groups.setdefault(key, []).extend([(a, n, ua), (b, n, ub), (c, n, uc)])

    def quad(self, key, p0, p1, p2, p3, rect, tile=None):
        """p0..p3 counter-clockwise seen from outside; rect maps p0=(u0,v1) p1=(u1,v1) p2=(u1,v0) p3=(u0,v0)."""
        u0, v0, u1, v1 = rect
        if tile:  # tile a repeating texture by face size (yards per repeat)
            w = math.dist(p0, p1) / tile
            h = math.dist(p1, p2) / tile
            u0, v0, u1, v1 = 0.0, 0.0, max(w, 0.25), max(h, 0.25)
        uv = [(u0, v1), (u1, v1), (u1, v0), (u0, v0)]
        self.tri(key, p0, p1, p2, uv[0], uv[1], uv[2])
        self.tri(key, p0, p2, p3, uv[0], uv[2], uv[3])

    def hull(self, key, c, faces):
        """c = 8 corners: bottom 0..3 (x-,y- / x+,y- / x+,y+ / x-,y+), top 4..7 same order.
        faces = {'front':rect,...}; a rect may be (rect, tile) for repeating textures."""
        sides = {
            'bottom': (0, 3, 2, 1), 'top': (4, 5, 6, 7),
            'front': (1, 2, 6, 5), 'back': (3, 0, 4, 7),
            'left': (2, 3, 7, 6), 'right': (0, 1, 5, 4),
        }
        for name, (a, b, cc, d) in sides.items():
            spec = faces.get(name, faces.get('*'))
            if spec is None:
                continue
            k = key
            if isinstance(spec, tuple) and len(spec) == 3 and isinstance(spec[0], tuple):
                rect, tile, k = spec
            elif isinstance(spec, tuple) and len(spec) == 2 and isinstance(spec[0], tuple):
                rect, tile = spec
            else:
                rect, tile = spec, None
            self.quad(k, c[a], c[b], c[cc], c[d], rect, tile)

    def box(self, key, lo, hi, faces):
        x0, y0, z0 = lo
        x1, y1, z1 = hi
        c = [(x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0),
             (x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1)]
        self.hull(key, c, faces)

    def cylinder(self, band_key, cap_key, centre, axis, radius, width, segs=20, band_rect=(0, 0, 1, 1),
                 band_tile=None, cap='wheel', cap_rect=None):
        axis = norm(axis)
        helper = (0, 0, 1) if abs(axis[2]) < 0.9 else (1, 0, 0)
        e1 = norm(cross(axis, helper))
        e2 = cross(axis, e1)
        h = mul(axis, width / 2)
        a_end, b_end = add(centre, h), sub(centre, h)
        ring = []
        for i in range(segs + 1):
            t = 2 * math.pi * i / segs
            d = add(mul(e1, math.cos(t)), mul(e2, math.sin(t)))
            ring.append((t, d))
        u0, v0, u1, v1 = band_rect
        for i in range(segs):
            t0, d0 = ring[i]
            t1, d1 = ring[i + 1]
            if band_tile:
                ua, ub = i / segs * band_tile, (i + 1) / segs * band_tile
                va, vb = 0.0, 1.0
            else:
                ua, ub = u0 + (u1 - u0) * i / segs, u0 + (u1 - u0) * (i + 1) / segs
                va, vb = v0, v1
            p0, p1 = add(b_end, mul(d0, radius)), add(b_end, mul(d1, radius))
            p2, p3 = add(a_end, mul(d1, radius)), add(a_end, mul(d0, radius))
            n0, n1 = d0, d1
            self.tri(band_key, p0, p1, p2, (ua, vb), (ub, vb), (ub, va), n=norm(add(n0, n1)))
            self.tri(band_key, p0, p2, p3, (ua, vb), (ub, va), (ua, va), n=norm(add(n0, n1)))
        for end, sign in ((a_end, 1), (b_end, -1)):
            n = mul(axis, sign)
            for i in range(segs):
                t0, d0 = ring[i]
                t1, d1 = ring[i + 1]
                pa, pb = add(end, mul(d0, radius)), add(end, mul(d1, radius))
                if cap == 'wheel':
                    # The wagon wheel texture is HALF a wheel (rim at v=0, hub at v=1): mirror it.
                    def wuv(t, r): return (0.5 + 0.5 * r * math.cos(t), 1.0 - r * abs(math.sin(t)))
                    uc, ua, ub = wuv(0, 0), wuv(t0, 1), wuv(t1, 1)
                else:
                    cu0, cv0, cu1, cv1 = cap_rect
                    cx, cy = (cu0 + cu1) / 2, (cv0 + cv1) / 2
                    rx, ry = (cu1 - cu0) / 2, (cv1 - cv0) / 2
                    uc = (cx, cy)
                    ua = (cx + rx * math.cos(t0), cy + ry * math.sin(t0))
                    ub = (cx + rx * math.cos(t1), cy + ry * math.sin(t1))
                if sign > 0:
                    self.tri(cap_key, end, pa, pb, uc, ua, ub, n=n)
                else:
                    self.tri(cap_key, end, pb, pa, uc, ub, ua, n=n)


def mat(tex, bone, colour=-1, two_sided=False):
    return (tex, bone, colour, two_sided)


def build_mesh():
    m = Mesh()
    BODY = 1
    red = mat(ATLAS, BODY)
    metal = mat(METAL, BODY)
    # Floor pan: riveted metal tub floor.
    m.box(metal, (-1.25, -0.62, 0.22), (1.05, 0.62, 0.34), {'*': ((0, 0, 1, 1), 1.0)})
    # Side pods, red planks with the rope lashings, tall enough to hide the rider's legs.
    for s in (1, -1):
        y0, y1 = sorted((0.62 * s, 0.84 * s))
        m.box(red, (-0.50, y0, 0.04), (0.78, y1, 0.74),          # skirts to 0.04: the rider's boots stay inside
              {'left': RED_ROPE, 'right': RED_ROPE, 'top': RED, 'front': RED, 'back': RED, 'bottom': RED})
    # Cowl / dashboard in front of the rider, its top sloping down to the nose.
    m.hull(red, [(0.55, -0.60, 0.30), (1.05, -0.55, 0.30), (1.05, 0.55, 0.30), (0.55, 0.60, 0.30),
                 (0.55, -0.60, 0.86), (1.05, -0.55, 0.66), (1.05, 0.55, 0.66), (0.55, 0.60, 0.86)],
           {'top': RED, 'left': RED, 'right': RED, 'back': PLANK})
    # The shark nose: a wedge from the cowl to the bumper, the shark mouth on its face.
    m.hull(red, [(1.05, -0.55, 0.14), (1.78, -0.42, 0.14), (1.78, 0.42, 0.14), (1.05, 0.55, 0.14),
                 (1.05, -0.55, 0.66), (1.78, -0.42, 0.52), (1.78, 0.42, 0.52), (1.05, 0.55, 0.66)],
           {'front': SHARK, 'top': RED, 'left': RED, 'right': RED, 'bottom': RED})
    # Bumpers front and rear (metal).
    m.box(metal, (1.76, -0.74, 0.16), (1.90, 0.74, 0.30), {'*': ((0, 0, 1, 1), 0.8)})
    m.box(metal, (-1.62, -0.70, 0.20), (-1.50, 0.70, 0.36), {'*': ((0, 0, 1, 1), 0.8)})
    # Axles.
    m.box(metal, (FRONT_X - 0.04, -FRONT_Y, FRONT_R - 0.04), (FRONT_X + 0.04, FRONT_Y, FRONT_R + 0.04), {'*': ((0, 0, 1, 1), 1.0)})
    m.box(metal, (REAR_X - 0.05, -REAR_Y, REAR_R - 0.05), (REAR_X + 0.05, REAR_Y, REAR_R + 0.05), {'*': ((0, 0, 1, 1), 1.0)})
    # Seat: a plank block with a reclined back.
    plank = mat(ATLAS, BODY)
    m.box(plank, (-0.62, -0.34, 0.34), (-0.05, 0.34, 0.74), {'*': PLANK})
    m.hull(plank, [(-0.78, -0.34, 0.70), (-0.62, -0.34, 0.70), (-0.62, 0.34, 0.70), (-0.78, 0.34, 0.70),
                   (-0.98, -0.32, 1.42), (-0.84, -0.32, 1.42), (-0.84, 0.32, 1.42), (-0.98, 0.32, 1.42)],
           {'*': PLANK})
    # Engine block behind the seat, the porthole on its back, the skull plate on top.
    m.box(metal, (-1.50, -0.44, 0.30), (-0.80, 0.44, 0.86),
          {'back': ((0, 0, 1, 1), 0.9), 'front': ((0, 0, 1, 1), 0.9), 'left': ((0, 0, 1, 1), 0.9),
           'right': ((0, 0, 1, 1), 0.9), 'top': ((0, 0, 1, 1), 0.9), 'bottom': ((0, 0, 1, 1), 0.9)})
    m.quad(mat(ATLAS, BODY), (-1.505, 0.30, 0.42), (-1.505, -0.30, 0.42), (-1.505, -0.30, 0.74), (-1.505, 0.30, 0.74), PORTHOLE)
    # Two brass exhausts angled up and back.
    brass = mat(METAL, BODY, BRASS)
    for s in (1, -1):
        m.cylinder(brass, brass, (-1.42, 0.30 * s, 1.02), (-0.45, 0, 1), 0.075, 0.46, segs=10,
                   band_tile=1.0, cap='disc', cap_rect=(0.4, 0.4, 0.6, 0.6))
    # The goblin touch: a red rocket strapped on top of the engine, a skull hex on its nose.
    m.cylinder(red, mat(ATLAS, BODY), (-1.12, 0.0, 1.06), (1, 0, 0), 0.16, 0.78, segs=14,
               band_rect=RED_ROPE, cap='disc', cap_rect=SKULL)
    # Steering column and wheel.
    m.hull(metal, [(0.60, -0.03, 0.80), (0.66, -0.03, 0.80), (0.66, 0.03, 0.80), (0.60, 0.03, 0.80),
                   (0.36, -0.03, 1.14), (0.42, -0.03, 1.14), (0.42, 0.03, 1.14), (0.36, 0.03, 1.14)],
           {'*': ((0, 0, 1, 1), 1.0)})
    m.cylinder(mat(WHEEL, BODY, RUBBER), mat(WHEEL, BODY), (0.36, 0.0, 1.16), (-0.75, 0, 0.66), 0.20, 0.04, segs=16,
               band_rect=(0.0, 0.0, 1.0, 0.1))
    # Pennant on a whip antenna (two-sided).
    m.box(metal, (-1.34, 0.30, 0.86), (-1.30, 0.34, 2.05), {'*': ((0, 0, 1, 1), 1.0)})
    m.quad(mat(ATLAS, BODY, -1, True), (-1.32, 0.32, 1.62), (-1.80, 0.32, 1.80), (-1.80, 0.32, 1.86), (-1.32, 0.32, 2.04),
           PENNANT)
    # Wheels: wagon-wheel faces, dark rubber treads.
    for i, (x, y, r, w) in enumerate(WHEELS):
        bone = 2 + i
        # Tread blocks around the band: the texture is 128 x 64 (u around, v across), repeated so a block is
        # about 0.12 yd long on any tyre size.
        reps = max(2, round(2 * math.pi * r / 0.5))
        m.cylinder(mat(TREAD, bone), mat(WHEEL, bone), (x, y, r), (0, 1, 0), r, w, segs=24, band_tile=reps)
    return m


def tread_blp():
    """A rubber tyre tread, 128 x 64 (u = around the tyre, v = across it): offset chevron blocks with dark grooves
    and a little grain - BLP2, 8-bit palette, full mip chain (the layout MSUIClient's BlpDecoder reads)."""
    import random
    rnd = random.Random(71000)
    W, H = 128, 64
    palette = []
    for i in range(256):                       # warm near-black rubber ramp
        g = 14 + i * 0.62
        palette.append((int(g * 0.95), int(g * 0.97), int(g)))      # B, G, R
    def shade(u, v):
        # four chevron blocks per tile; the groove between blocks and a centre rib groove
        cv = abs(v - 0.5) * 2.0                # 0 at the centre line, 1 at the shoulders
        phase = (u * 4.0 + cv * 0.55) % 1.0
        groove = phase < 0.16 or (0.47 < v < 0.53 and phase > 0.5)
        shoulder = cv > 0.88
        base = 120 if not groove else 16
        if shoulder:
            base = 92
        # bevel: brighter leading edge of each block, worn top
        if not groove and phase < 0.24:
            base += 45
        return max(0, min(255, base + rnd.randint(-10, 10)))
    level0 = bytes(shade((x + 0.5) / W, (y + 0.5) / H) for y in range(H) for x in range(W))
    mips = [(level0, W, H)]
    while mips[-1][1] > 1 or mips[-1][2] > 1:
        src, sw, sh = mips[-1]
        dw, dh = max(1, sw // 2), max(1, sh // 2)
        out = bytearray(dw * dh)
        for y in range(dh):
            for x in range(dw):
                acc = n = 0
                for oy in (0, 1):
                    for ox in (0, 1):
                        sx, sy = min(sw - 1, x * 2 + ox), min(sh - 1, y * 2 + oy)
                        acc += src[sy * sw + sx]; n += 1
                out[y * dw + x] = acc // n
        mips.append((bytes(out), dw, dh))
    mips = mips[:16]
    header = bytearray(b"BLP2") + struct.pack("<I", 1) + bytes((1, 0, 0, 1)) + struct.pack("<II", W, H)
    offsets, sizes = [], []
    data_start = 148 + 1024
    at = data_start
    for m_, _w, _h in mips:
        offsets.append(at); sizes.append(len(m_)); at += len(m_)
    offsets += [0] * (16 - len(offsets)); sizes += [0] * (16 - len(sizes))
    header += struct.pack("<16I", *offsets) + struct.pack("<16I", *sizes)
    for b_, g_, r_ in palette:
        header += bytes((b_, g_, r_, 255))
    assert len(header) == data_start
    return bytes(header) + b"".join(m_ for m_, _w, _h in mips)


# ── binary writer ─────────────────────────────────────────────────────────────────────────────────────

class Blob:
    def __init__(self):
        self.b = bytearray()

    def align(self):
        while len(self.b) % 16:
            self.b.append(0)
        return len(self.b)

    def put(self, fmt, *v):
        self.b += struct.pack('<' + fmt, *v)

    def array(self, at, count, offset):
        struct.pack_into('<II', self.b, at, count, offset if count else 0)


def track_bytes(b, interp, times, keys, fmt, ranges):
    """Write ranges/times/keys; return the 28-byte block. keys are tuples for fmt."""
    if not times:
        return struct.pack('<Hh6I', 0, -1, 0, 0, 0, 0, 0, 0)
    ro = b.align()
    for r in ranges:
        b.put('II', *r)
    to = b.align()
    for t in times:
        b.put('I', t)
    ko = b.align()
    for k in keys:
        b.put(fmt, *k)
    return struct.pack('<Hh6I', interp, -1, len(ranges), ro, len(times), to, len(keys), ko)


def const_track(b, fmt, value):
    """A constant track: one key at each sequence's start, so every window holds its own key."""
    times = [start for _sid, start, _end, _speed in SEQUENCES]
    return track_bytes(b, 0, times, [value] * len(times), fmt, [(i, i) for i in range(len(times))])


def quat_y(angle):
    return (0.0, math.sin(angle / 2), 0.0, math.cos(angle / 2))


def quat_axis(axis, angle):
    ax = norm(axis)
    s = math.sin(angle / 2)
    return (ax[0] * s, ax[1] * s, ax[2] * s, math.cos(angle / 2))


def wheel_track(radius):
    """Rotation about +y (rolling forward) per sequence: Stand still, Walk and Run at the clip speed.
    Whole turns per loop so the clip loops seamlessly; 8 keys per turn (linear slerp)."""
    times, keys, ranges = [], [], []
    for sid, start, end, speed in SEQUENCES:
        first = len(times)
        dur = (end - start) / 1000.0
        turns = round(speed * dur / (2 * math.pi * radius)) if speed else 0
        steps = max(turns * 8, 1)
        for k in range(steps + 1):
            t = start + round((end - start) * k / steps)
            times.append(t)
            keys.append(quat_y(2 * math.pi * turns * k / steps))
        ranges.append((first, len(times) - 1))
    return times, keys, ranges


def body_tracks():
    """Engine shake: a fast small bob idling, a bigger one and a slight nose-up at speed."""
    tt, tk, tr, rt, rk, rr = [], [], [], [], [], []
    for sid, start, end, speed in SEQUENCES:
        amp, hz, pitch = {0: (0.012, 12, 0.0), 4: (0.016, 8, 0.01), 5: (0.022, 10, 0.03)}[sid]
        n = int((end - start) / 1000.0 * hz * 2)
        f0 = len(tt)
        for k in range(n + 1):
            t = start + round((end - start) * k / n)
            tt.append(t)
            tk.append((0.0, 0.0, amp if k % 2 else 0.0))
        tr.append((f0, len(tt) - 1))
        f1 = len(rt)
        for k in range(3):
            t = start + round((end - start) * k / 2)
            rt.append(t)
            wob = pitch * (1.0 if k == 1 else 0.6)
            rk.append(quat_y(-wob))
        rr.append((f1, len(rt) - 1))
    return (tt, tk, tr), (rt, rk, rr)


def write(path):
    mesh = build_mesh()
    keys = list(mesh.groups.keys())
    b = Blob()
    b.b += bytes(0x150)

    name = b"SUIKart\0"
    no = b.align(); b.b += name
    b.array(0x008, len(name), no)

    # Bounds.
    allp = [v[0] for g in mesh.groups.values() for v in g]
    lo = tuple(min(p[i] for p in allp) for i in range(3))
    hi = tuple(max(p[i] for p in allp) for i in range(3))
    radius = math.sqrt(sum(max(abs(lo[i]), abs(hi[i])) ** 2 for i in range(3)))

    # Sequences.
    so = b.align()
    for sid, start, end, speed in SEQUENCES:
        b.put('HHIIfIhHIII', sid, 0, start, end, speed, 0, 32767, 0, 0, 0, 150)
        b.put('3f3ff', *lo, *hi, radius)
        b.put('hH', -1, 0)
    b.array(0x01C, len(SEQUENCES), so)
    lookup = [-1] * 6
    for i, (sid, *_r) in enumerate(SEQUENCES):
        lookup[sid] = i
    lo_ = b.align()
    for v in lookup:
        b.put('h', v)
    b.array(0x024, len(lookup), lo_)

    # Bones.
    (bt, bk, br), (brt, brk, brr) = body_tracks()
    body_t = track_bytes(b, 1, bt, bk, '3f', br)
    body_r = track_bytes(b, 1, brt, brk, '4f', brr)
    none = track_bytes(b, 0, [], [], '', [])
    bones = [(-1, (0, 0, 0), none, none, 0), (0, BODY_PIVOT, body_t, body_r, 0x200)]
    for x, y, r, w in WHEELS:
        t, k, rg = wheel_track(r)
        bones.append((0, (x, y, r), none, track_bytes(b, 1, t, k, '4f', rg), 0x200))
    bo = b.align()
    for parent, pivot, tr, rot, flags in bones:
        b.put('iIhH', -1, flags, parent, 0)
        b.b += tr + rot + none
        b.put('3f', *pivot)
    b.array(0x034, len(bones), bo)

    # Vertices (one copy per triangle corner - flat shaded, simple and exact).
    vo = b.align()
    verts = []
    for key in keys:
        for p, n, uv in mesh.groups[key]:
            verts.append((p, n, uv, key[1]))
    for p, n, uv, bone in verts:
        b.put('3f', *p)
        b.put('4B4B', 255, 0, 0, 0, bone, 0, 0, 0)
        b.put('3f2f2f', *n, *uv, 0.0, 0.0)
    b.array(0x044, len(verts), vo)

    # View 0.
    lk = b.align()
    for i in range(len(verts)):
        b.put('H', i)
    to = b.align()
    for i in range(len(verts)):
        b.put('H', i)
    smo = b.align()
    start = 0
    for key in keys:
        n = len(mesh.groups[key])
        b.put('10H', 0, 0, start, n, start, n, len(bones), 0, 1, key[1])
        cx = sum(v[0][0] for v in mesh.groups[key]) / n
        cy = sum(v[0][1] for v in mesh.groups[key]) / n
        cz = sum(v[0][2] for v in mesh.groups[key]) / n
        b.put('3f', cx, cy, cz)
        start += n
    bao = b.align()
    for i, key in enumerate(keys):
        tex, bone, colour, two = key
        b.put('BbHHHhHHHHHHH', 0, 0, 0, i, i, colour, 1 if two else 0, 0, 1, tex, 0, 0, 0)
    vho = b.align()
    b.put('11I', len(verts), lk, len(verts), to, 0, 0, len(keys), smo, len(keys), bao, 1)
    struct.pack_into('<II', b.b, 0x04C, 1, vho)

    # Colours: rubber, brass (static colour, opaque alpha).
    cols = []
    for c in COLOURS:
        ct = const_track(b, '3f', c)
        at = const_track(b, 'h', (32767,))
        cols.append(ct + at)
    co = b.align()
    for c in cols:
        b.b += c
    b.array(0x054, len(cols), co)

    # Textures (by filename, wrap both ways - the metal tiles).
    names = []
    for t in TEXTURES:
        nb = (t + "\0").encode('ascii')
        names.append((b.align(), len(nb)))
        b.b += nb
    txo = b.align()
    for off, cnt in names:
        b.put('4I', 0, 3, cnt, off)
    b.array(0x05C, len(TEXTURES), txo)

    wt = const_track(b, 'h', (32767,))
    tro = b.align(); b.b += wt
    b.array(0x064, 1, tro)

    rfo = b.align()
    b.put('HH', 0x04, 0)     # 0: opaque, two-sided (closed hulls hide their insides; no winding to get wrong)
    b.put('HH', 0x04, 0)     # 1: the pennant (two-sided by nature)
    b.array(0x084, 2, rfo)

    blo = b.align()
    for i in range(len(bones)):
        b.put('H', i)
    b.array(0x08C, len(bones), blo)
    tlo = b.align()
    for i in range(len(TEXTURES)):
        b.put('H', i)
    b.array(0x094, len(TEXTURES), tlo)
    tuo = b.align(); b.put('h', 0)
    b.array(0x09C, 1, tuo)
    tpo = b.align(); b.put('H', 0)
    b.array(0x0A4, 1, tpo)
    uvo = b.align(); b.put('h', -1)
    b.array(0x0AC, 1, uvo)

    struct.pack_into('<3f3ff', b.b, 0x0B4, *lo, *hi, radius)
    struct.pack_into('<3f3ff', b.b, 0x0D0, *lo, *hi, radius)

    # Attachment 0: the seat, on the body bone so the rider shakes with the kart.
    ao = b.align()
    b.put('IHH3f', 0, 1, 0, *SEAT)
    b.b += none
    b.array(0x104, 1, ao)
    alo = b.align(); b.put('h', 0)
    b.array(0x10C, 1, alo)

    b.b[0:4] = b"MD20"
    struct.pack_into('<I', b.b, 4, 256)
    open(path, 'wb').write(bytes(b.b))
    tread = os.path.join(os.path.dirname(os.path.abspath(path)), "SUIKartTread.blp")
    open(tread, 'wb').write(tread_blp())
    tris = len(verts) // 3
    print(f"{path}: {len(b.b)} bytes, {len(verts)} vertices, {tris} triangles, {len(keys)} batches, {len(bones)} bones, "
          f"bounds {tuple(round(v, 2) for v in lo)}..{tuple(round(v, 2) for v in hi)}")


if __name__ == "__main__":
    write(sys.argv[1] if len(sys.argv) > 1 else "SUIKart.m2")
