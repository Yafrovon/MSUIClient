# Contact sheets for frame bursts (GameLoop.FrameRecorder.cs): one PNG per burst directory, frames in a grid,
# each labelled with its index, time since the event and frame ms - so a transition can be LOOKED at.
#   python tools/worldpack/frame-sheet.py dumps/frames/<name>-*   [--cols 6] [--every 1]
import csv, glob, os, sys
from PIL import Image, ImageDraw


def arg(name, default):
    return sys.argv[sys.argv.index(name) + 1] if name in sys.argv else default


def sheet(d, cols, every):
    rows = {}
    event_t = None
    path = os.path.join(d, "frames.csv")
    if os.path.exists(path):
        for line in open(path, encoding="utf-8"):
            if line.startswith("# event") and event_t is None:
                event_t = float(line.rsplit(" at ", 1)[1])
            elif line[:1].isdigit():
                idx, t, ms, meta = line.rstrip("\n").split(",", 3)
                rows[int(idx)] = (float(t), float(ms), meta)
    frames = sorted(glob.glob(os.path.join(d, "f*.png")))[::every]
    if not frames:
        return None
    first = Image.open(frames[0])
    w, h = first.size
    n = len(frames)
    rcount = (n + cols - 1) // cols
    out = Image.new("RGB", (cols * w, rcount * (h + 18)), (20, 20, 20))
    draw = ImageDraw.Draw(out)
    for i, f in enumerate(frames):
        idx = int(os.path.basename(f)[1:4])
        x, y = (i % cols) * w, (i // cols) * (h + 18)
        out.paste(Image.open(f).convert("RGB"), (x, y + 18))
        t, ms, meta = rows.get(idx, (None, 0.0, ""))
        rel = f"{t - event_t:+.2f}s" if t is not None and event_t is not None else "?"
        colour = (255, 80, 80) if ms > 50 else (230, 230, 230)
        draw.text((x + 4, y + 3), f"#{idx} {rel} {ms:.0f}ms {meta.split(' pos=')[0]}", fill=colour)
    target = d.rstrip("/\\") + "-sheet.png"
    out.save(target)
    return target, n


def main():
    cols = int(arg("--cols", "6"))
    every = int(arg("--every", "1"))
    dirs = [a for a in sys.argv[1:] if os.path.isdir(a)]
    for d in dirs:
        r = sheet(d, cols, every)
        if r:
            print(f"{r[0]}: {r[1]} frame(s)")


if __name__ == "__main__":
    main()
