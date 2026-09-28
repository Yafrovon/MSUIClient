# Contact sheets for World Builder surveys: the newest "<name>-{s,e,n,w}" shots of each subject in a
# 2x2 grid (labelled), so four eye-level sides are reviewed at once. The shots come from the World
# Builder script command "survey <x> <y> <distance> <name>" (shared_docs/WORLD_BUILDER.md §9).
#   usage: python tools/worldpack/contact.py [--out DIR] name [name...]
# The source shots (png + json) are DELETED once the sheet is written (owner: don't hoard screenshots);
# delete the sheet too once it has been reviewed.
import glob, os, sys
from PIL import Image, ImageDraw

DUMPS = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "dumps"))   # <repo>/dumps
args = sys.argv[1:]
OUT = args[args.index("--out") + 1] if "--out" in args else DUMPS
names = [a for i, a in enumerate(args) if a != "--out" and (i == 0 or args[i - 1] != "--out")]
for name in names:
    tiles, used = [], []
    for side in "senw":
        hits = sorted(glob.glob(os.path.join(DUMPS, f"gameplay-wb-{name}-{side}-*.png")))
        if hits:
            with Image.open(hits[-1]) as src:
                tiles.append((side, src.convert("RGB")))
            used += [h for h in hits] + [h[:-4] + ".json" for h in hits]
    if not tiles:
        print(f"{name}: no shots"); continue
    w, h = 960, 540
    sheet = Image.new("RGB", (w * 2, h * 2), "black")
    d = ImageDraw.Draw(sheet)
    for i, (side, im) in enumerate(tiles):
        im = im.crop((380, 60, im.width, im.height)).resize((w, h))   # drop the creator panel column
        x, y = (i % 2) * w, (i // 2) * h
        sheet.paste(im, (x, y))
        d.rectangle((x, y, x + 150, y + 34), fill="black")
        d.text((x + 8, y + 8), f"{name} from {dict(s='S', e='E', n='N', w='W')[side]}", fill="yellow")
    path = os.path.join(OUT, f"sheet-{name}.jpg")
    sheet.save(path, quality=80)
    for f in used:
        if os.path.exists(f):
            os.remove(f)
    print(path)
