# Render a roof-scan CSV (live command `roof-scan`, GameLoop.LiveRun.Karting.cs) as a height map for laying kart
# track on rooftops: colour = height above the street (blue low -> green -> yellow -> white high), red = too steep
# to drive (normal z < --flat), black = no collision hit. North (+x) is up, west (+y) is left, grid every --grid yd.
#   python tools/worldpack/roofmap.py dumps/roofscan-<name>.csv [--street 95] [--flat 0.8] [--grid 20] [--scale 3]
import csv, sys
from PIL import Image, ImageDraw


def arg(name, default):
    return sys.argv[sys.argv.index(name) + 1] if name in sys.argv else default


def colour(h):
    stops = [(0, (20, 40, 120)), (6, (30, 140, 200)), (12, (40, 200, 80)), (20, (230, 220, 40)), (35, (255, 255, 255))]
    if h <= stops[0][0]:
        return stops[0][1]
    for (a, ca), (b, cb) in zip(stops, stops[1:]):
        if h <= b:
            t = (h - a) / (b - a)
            return tuple(int(ca[i] + (cb[i] - ca[i]) * t) for i in range(3))
    return stops[-1][1]


def main():
    path = sys.argv[1]
    rows = [(float(r["x"]), float(r["y"]), float(r["z"]), float(r["nz"])) for r in csv.DictReader(open(path))]
    flat = float(arg("--flat", "0.8"))
    grid = int(arg("--grid", "20"))
    scale = int(arg("--scale", "3"))
    zs = sorted(r[2] for r in rows)
    street = float(arg("--street", str(zs[len(zs) // 10])))
    xs = sorted({r[0] for r in rows}); ys = sorted({r[1] for r in rows})
    step = min(b - a for a, b in zip(xs, xs[1:]) if b > a)
    x0, x1, y0, y1 = xs[0], xs[-1], ys[0], ys[-1]
    W, H = int((y1 - y0) / step) + 1, int((x1 - x0) / step) + 1
    im = Image.new("RGB", (W, H))
    for x, y, z, nz in rows:
        px, py = int(round((y1 - y) / step)), int(round((x1 - x) / step))
        im.putpixel((px, py), colour(z - street) if nz >= flat else (200, 0, 0))
    im = im.resize((W * scale, H * scale), Image.NEAREST)
    d = ImageDraw.Draw(im)
    gx = int(x0 // grid * grid)
    while gx <= x1:
        py = int((x1 - gx) / step * scale)
        d.line([(0, py), (im.width, py)], fill=(60, 60, 60)); d.text((2, py + 1), str(gx), fill=(255, 128, 0))
        gx += grid
    gy = int(y0 // grid * grid)
    while gy <= y1:
        px = int((y1 - gy) / step * scale)
        d.line([(px, 0), (px, im.height)], fill=(60, 60, 60)); d.text((px + 1, 2), str(gy), fill=(255, 128, 0))
        gy += grid
    out = path.rsplit(".", 1)[0] + ".png"
    im.save(out)
    print(f"{out}: street z {street:.1f}, {len(rows)} hit(s), {W}x{H} cells of {step} yd")


if __name__ == "__main__":
    main()
