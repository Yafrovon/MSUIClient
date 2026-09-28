"""Read-only live QA points: authored polygon ownership inside, vanilla ownership outside.

The expectation comes from authoring data and STOCK archives; the protocol checks the
client's mounted result independently. Every point is the centre of a vanilla ZMP cell.
"""
import importlib.util
from pathlib import Path
import struct


def samples(documents):
    spec = importlib.util.spec_from_file_location("worldmap_outline", Path(__file__).with_name("worldmap-outline.py"))
    outline = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(outline)
    source = outline.Source(outline.REPO / "GameData" / "Data")
    regions = [d["body"] for d in documents if d["kind"] == "worldmap" and d["body"]]

    def records(path):
        blob, _ = source.read(path)
        _, count, _, size, _ = struct.unpack_from("<4s4I", blob)
        return [blob[20 + i * size:20 + (i + 1) * size] for i in range(count)]

    parents = {struct.unpack_from("<I", r)[0]: struct.unpack_from("<I", r, 8)[0]
               for r in records(r"DBFilesClient\AreaTable.dbc")}
    zone_maps = {(struct.unpack_from("<I", r, 4)[0], struct.unpack_from("<I", r, 8)[0])
                 for r in records(r"DBFilesClient\WorldMapArea.dbc") if struct.unpack_from("<I", r, 8)[0] != 0}
    # The float constant is the client's world-to-ZMP law. Samples are deliberately well away from grid lines.
    centres = [((.5 - (i // 128 + .5) / 128) / 2.9296876e-5,
                (.5 - (i % 128 + .5) / 128) / 2.9296876e-5) for i in range(128 * 128)]

    def spread(indices, maximum):
        ordered = sorted(indices)
        if len(ordered) <= maximum:
            return ordered
        return [ordered[round(i * (len(ordered) - 1) / (maximum - 1))] for i in range(maximum)]

    output = []
    for region in regions:
        map_id, area = int(region["map"]), int(region["area"])
        directory, bounds, _, _ = outline.continent(source, map_id)
        stock, _ = source.read(rf"Interface\WorldMap\{directory}.zmp")
        stock_ids = struct.unpack("<16384I", stock)
        def stock_owner(index):
            raw = stock_ids[index]
            resolved = parents.get(raw, 0) or raw
            return resolved if (map_id, resolved) in zone_maps else 0
        owned = {i for i, point in enumerate(centres) if outline.inside(point, region["polygon"])}
        if not owned:
            raise ValueError(f"worldmap {map_id}:{area} has no hover cells")
        outside = set()
        for i in owned:
            col, row = i % 128, i // 128
            for dx, dy in ((-1, 0), (1, 0), (0, -1), (0, 1)):
                c, r = col + dx, row + dy
                if 0 <= c < 128 and 0 <= r < 128:
                    j = r * 128 + c
                    if not any(int(other["map"]) == map_id and outline.inside(centres[j], other["polygon"]) for other in regions):
                        outside.add(j)
        for i in spread(owned, 12):
            output.append((map_id, *centres[i], area, "inside"))
        for i in spread(outside, 12):
            output.append((map_id, *centres[i], stock_owner(i), "outside-stock"))
        # Include a nearby truly unowned sea point when the adjacent cells happen to belong to coastal zones.
        # Its expectation is read from vanilla data; open sea is never inferred merely from polygon exclusion.
        centre = tuple(sum(centres[i][axis] for i in owned) / len(owned) for axis in (0, 1))
        left, right, top, bottom = bounds
        unowned = [i for i in range(128 * 128) if stock_owner(i) == 0 and i not in owned
                   and bottom <= centres[i][0] <= top and right <= centres[i][1] <= left
                   and not any(int(other["map"]) == map_id and outline.inside(centres[i], other["polygon"]) for other in regions)]
        if unowned:
            i = min(unowned, key=lambda i: sum((centres[i][axis] - centre[axis]) ** 2 for axis in (0, 1)))
            output.append((map_id, *centres[i], 0, "outside-unowned-stock"))
    return output
