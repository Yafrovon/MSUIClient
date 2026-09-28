"""Trace continent art once; use the same world polygon for land, hover and glow.

New outline (pixels are in the assembled 1002 x 668 visible continent image):
  python tools/worldpack/worldmap-outline.py OUT.json --map 0 --area 7001 \
    --directory Gilneas --pixels '[[388,243],[434,243],...]' --preview OUT.png

Reproduce an existing outline and its review sheet from the installed stock art:
  python tools/worldpack/worldmap-outline.py OUT.json --input OUT.json --preview OUT.png

The review sheet shows original art, the traced outline, ADT grid and every vertex
in pixels/world/grid coordinates. No image or geometry is inferred by flood fill.
Only explicitly traced pixels become the polygon. The stock continent bounds
come from WorldMapArea.dbc; patch-7 (our generated content) is deliberately absent
from the source stack. This command only reads game files and writes its outputs.
"""

import argparse
import hashlib
import io
import json
import math
from pathlib import Path
import struct
import sys

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "mpqpy"))
from mpq import MpqArchive
from PIL import Image, ImageDraw, ImageFont

VISIBLE_SIZE = (1002, 668)
ADT_SIZE = 533.3333333333334
SOURCE_ARCHIVES = ("patch-4.MPQ", "patch-2.MPQ", "patch.MPQ", "interface.MPQ",
                   "texture.MPQ", "terrain.MPQ", "misc.MPQ", "dbc.MPQ", "base.MPQ")


class Source:
    def __init__(self, data_dir):
        self.archives = []
        for name in SOURCE_ARCHIVES:
            path = data_dir / name
            if path.exists():
                self.archives.append((name, MpqArchive(str(path))))

    def read(self, name):
        for archive_name, archive in self.archives:
            if archive.has_file(name):
                blob = archive.read_file(name)
                return blob, {"path": name, "archive": archive_name,
                              "sha256": hashlib.sha256(blob).hexdigest()}
        raise ValueError(f"No source archive contains {name}")


def continent(source, map_id):
    blob, dbc_source = source.read(r"DBFilesClient\WorldMapArea.dbc")
    magic, count, fields, size, string_size = struct.unpack_from("<4s4I", blob)
    if magic != b"WDBC" or fields < 8 or size < 32:
        raise ValueError("Invalid WorldMapArea.dbc")
    strings = blob[20 + count * size:]
    for index in range(count):
        record = blob[20 + index * size:20 + (index + 1) * size]
        ident, record_map, area, name_offset = struct.unpack_from("<4I", record)
        if record_map == map_id and area == 0:
            directory = strings[name_offset:strings.index(b"\0", name_offset)].decode("ascii")
            return directory, struct.unpack_from("<4f", record, 16), ident, dbc_source
    raise ValueError(f"No continent WorldMapArea row for map {map_id}")


def art(source, directory):
    image = Image.new("RGB", (1024, 768))
    files = []
    for i in range(12):
        blob, metadata = source.read(rf"Interface\WorldMap\{directory}\{directory}{i + 1}.blp")
        tile = Image.open(io.BytesIO(blob)).convert("RGB")
        image.paste(tile, ((i % 4) * 256, (i // 4) * 256))
        files.append(metadata)
    return image.crop((0, 0, *VISIBLE_SIZE)), files


def pixels_to_world(point, bounds):
    left, right, top, bottom = bounds
    u, v = point
    return [top - v / VISIBLE_SIZE[1] * (top - bottom),
            left - u / VISIBLE_SIZE[0] * (left - right)]


def world_to_pixels(point, bounds):
    left, right, top, bottom = bounds
    x, y = point
    return [(left - y) / (left - right) * VISIBLE_SIZE[0],
            (top - x) / (top - bottom) * VISIBLE_SIZE[1]]


def validate_polygon(points):
    if len(points) < 3 or any(len(p) != 2 or not all(math.isfinite(v) for v in p) for p in points):
        raise ValueError("Polygon needs at least three finite pairs")
    if len({tuple(p) for p in points}) != len(points):
        raise ValueError("Polygon repeats a vertex; do not repeat the first vertex at the end")
    if any(not 0 <= p[0] <= VISIBLE_SIZE[0] or not 0 <= p[1] <= VISIBLE_SIZE[1] for p in points):
        raise ValueError("Pixel vertex is outside the visible 1002 x 668 continent image")
    def cross(a, b, c):
        return (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])
    def on_segment(a, b, p):
        return (min(a[0], b[0]) <= p[0] <= max(a[0], b[0]) and
                min(a[1], b[1]) <= p[1] <= max(a[1], b[1]))
    for i, a in enumerate(points):
        b = points[(i + 1) % len(points)]
        for j in range(i + 2, len(points)):
            if i == 0 and j == len(points) - 1:
                continue
            c, d = points[j], points[(j + 1) % len(points)]
            ca, cb, cc, cd = cross(a, b, c), cross(a, b, d), cross(c, d, a), cross(c, d, b)
            if ((ca * cb < 0 and cc * cd < 0) or
                    (ca == 0 and on_segment(a, b, c)) or (cb == 0 and on_segment(a, b, d)) or
                    (cc == 0 and on_segment(c, d, a)) or (cd == 0 and on_segment(c, d, b))):
                raise ValueError(f"Polygon edges {i} and {j} intersect")
    area = sum(a[0] * points[(i + 1) % len(points)][1] -
               points[(i + 1) % len(points)][0] * a[1] for i, a in enumerate(points)) / 2
    if abs(area) < 1:
        raise ValueError("Polygon area is less than one continent pixel")


def inside(point, polygon):
    x, y = point
    hit = False
    for i, a in enumerate(polygon):
        b = polygon[(i + 1) % len(polygon)]
        if (a[1] > y) != (b[1] > y) and x < (b[0] - a[0]) * (y - a[1]) / (b[1] - a[1]) + a[0]:
            hit = not hit
    return hit


def font(size):
    for path in ("C:/Windows/Fonts/consola.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf"):
        if Path(path).exists():
            return ImageFont.truetype(path, size)
    return ImageFont.load_default()


def preview(path, image, document, crop, scale):
    points = document["source"]["pixelPolygon"]
    bounds = [document["source"]["worldMapAreaBounds"][name]
              for name in ("left", "right", "top", "bottom")]
    if crop is None:
        crop = (math.floor(min(p[0] for p in points)) - 8,
                math.floor(min(p[1] for p in points)) - 8,
                math.ceil(max(p[0] for p in points)) + 8,
                math.ceil(max(p[1] for p in points)) + 8)
    x0, y0, x1, y1 = crop
    width, height = (x1 - x0) * scale, (y1 - y0) * scale
    original = image.crop(crop).resize((width, height), Image.Resampling.NEAREST)
    traced = original.copy().convert("RGBA")
    overlay = Image.new("RGBA", traced.size)
    draw = ImageDraw.Draw(overlay)
    to_screen = lambda p: ((p[0] - x0) * scale, (p[1] - y0) * scale)
    line = [to_screen(p) for p in points]
    draw.polygon(line, fill=(0, 205, 230, 36))
    draw.line(line + [line[0]], fill=(0, 255, 240, 255), width=3)
    for col in range(65):
        u = world_to_pixels([0, (32 - col) * ADT_SIZE], bounds)[0]
        screen_x = (u - x0) * scale
        if 0 <= screen_x < width:
            draw.line((screen_x, 0, screen_x, height), fill=(255, 255, 255, 90))
            draw.text((screen_x + 3, 3), f"c{col}", font=font(14), fill="white")
    for row in range(65):
        v = world_to_pixels([(32 - row) * ADT_SIZE, 0], bounds)[1]
        screen_y = (v - y0) * scale
        if 0 <= screen_y < height:
            draw.line((0, screen_y, width, screen_y), fill=(255, 255, 255, 90))
            draw.text((3, screen_y + 3), f"r{row}", font=font(14), fill="white")
    for i, p in enumerate(line):
        draw.ellipse((p[0] - 2, p[1] - 2, p[0] + 2, p[1] + 2), fill="white")
        if i % 4 == 0:
            draw.text((p[0] + 4, p[1] - 13), str(i), font=font(12), fill="white")
    traced = Image.alpha_composite(traced, overlay).convert("RGB")
    table_rows = math.ceil(len(points) / 3)
    total_width = max(width * 2 + 48, 1320)
    sheet = Image.new("RGB", (total_width, height + 154 + table_rows * 19), "#151e26")
    sheet.paste(original, (16, 63))
    sheet.paste(traced, (width + 32, 63))
    draw = ImageDraw.Draw(sheet)
    draw.text((16, 12), f"{document['directory']}: painted continent boundary / map {document['map']}, area {document['area']}", font=font(20), fill="white")
    draw.text((16, 40), "Original Blizzard art", font=font(16), fill="white")
    draw.text((width + 32, 40), "Trace (cyan), ADT columns/rows (white)", font=font(16), fill="white")
    table_top = height + 81
    draw.text((16, table_top), "Vertex: continent pixel (u,v) -> world (X north,Y west) -> ADT (column,row)", font=font(16), fill="white")
    for i, (p, world) in enumerate(zip(points, document["polygon"])):
        col, row = 32 - world[1] / ADT_SIZE, 32 - world[0] / ADT_SIZE
        label = f"{i:2}: {p[0]:5.1f},{p[1]:5.1f}  {world[0]:7.1f},{world[1]:6.1f}  {col:5.2f},{row:5.2f}"
        draw.text((16 + (i // table_rows) * (total_width // 3), table_top + 29 + (i % table_rows) * 19), label, font=font(12), fill="#d7e6f0")
    draw.text((16, sheet.height - 25), "Sea stays outside this polygon. North closure is a chosen zone boundary; coast follows the landward edge of pale wash.", font=font(13), fill="#a5c4ce")
    path.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(path)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("output", type=Path)
    parser.add_argument("--input", type=Path, help="Existing outline; reuse pixelPolygon and metadata")
    parser.add_argument("--pixels", help="Pixel polygon JSON, or @path to a JSON array")
    parser.add_argument("--map", type=int)
    parser.add_argument("--area", type=int)
    parser.add_argument("--directory")
    parser.add_argument("--note", help="Describe tracing choices and intentional non-coastal boundaries")
    parser.add_argument("--preview", type=Path)
    parser.add_argument("--crop", help="Continent pixel crop x0,y0,x1,y1; defaults to polygon plus margin")
    parser.add_argument("--scale", type=int, default=9)
    parser.add_argument("--data", type=Path, default=REPO / "GameData" / "Data")
    args = parser.parse_args()
    old = json.loads(args.input.read_text(encoding="utf-8-sig")) if args.input else {}
    map_id = args.map if args.map is not None else old.get("map")
    area = args.area if args.area is not None else old.get("area")
    directory = args.directory or old.get("directory")
    if map_id is None or area is None or not directory:
        parser.error("--map, --area, --directory are required for a new outline")
    if args.scale < 1 or args.scale > 32:
        parser.error("--scale must be between 1 and 32")
    if args.pixels:
        pixel_text = Path(args.pixels[1:]).read_text() if args.pixels.startswith("@") else args.pixels
        points = json.loads(pixel_text)
    else:
        points = old.get("source", {}).get("pixelPolygon")
    if not points:
        parser.error("--pixels or an --input with source.pixelPolygon is required")
    validate_polygon(points)
    source = Source(args.data)
    continent_directory, bounds, continent_row, dbc_source = continent(source, map_id)
    image, art_sources = art(source, continent_directory)
    polygon = [[round(v, 5) for v in pixels_to_world(p, bounds)] for p in points]
    for p, world in zip(points, polygon):
        if max(abs(a - b) for a, b in zip(p, world_to_pixels(world, bounds))) > 0.00001:
            raise ValueError("Projection round trip failed")
    xs, ys = [p[0] for p in polygon], [p[1] for p in polygon]
    cells = [[col, row] for row in range(128) for col in range(128)
             if inside([(32 - (row + 0.5) / 2) * ADT_SIZE,
                        (32 - (col + 0.5) / 2) * ADT_SIZE], polygon)]
    # Keep pack-specific properties (for example coast shaping settings) when a
    # saved source trace is regenerated after changes to its pixel vertices.
    document = {**old,
        "schema": 1, "map": map_id, "area": area, "directory": directory,
        "polygon": polygon,
        "bounds": {"left": max(ys), "right": min(ys), "top": max(xs), "bottom": min(xs)},
        "source": {"method": "manual coastline trace",
                   "note": args.note if args.note is not None else old.get("source", {}).get("note", ""),
                   "continentDirectory": continent_directory,
                   "visiblePixels": list(VISIBLE_SIZE), "pixelPolygon": points,
                   "worldMapAreaRow": continent_row,
                   "worldMapAreaBounds": dict(zip(("left", "right", "top", "bottom"), bounds)),
                   "projection": "X=top-v/668*(top-bottom); Y=left-u/1002*(left-right)",
                   "dbc": dbc_source, "art": art_sources},
        "checks": {"simplePolygon": True, "projectionRoundTrip": True,
                   "zmpCellRule": "cell center inside polygon; column + row * 128",
                   "zmpCells": cells}}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
    if args.preview:
        crop = tuple(map(int, args.crop.split(","))) if args.crop else None
        if crop and (len(crop) != 4 or crop[2] <= crop[0] or crop[3] <= crop[1]):
            parser.error("--crop must be x0,y0,x1,y1 with positive extent")
        preview(args.preview, image, document, crop, args.scale)
    print(json.dumps({"output": str(args.output), "vertices": len(points), "bounds": document["bounds"],
                      "zmpCells": len(cells), "preview": str(args.preview) if args.preview else None}, indent=2))


if __name__ == "__main__":
    main()
