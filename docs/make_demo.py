"""Rebuilds docs/demo.gif.

The frames are drawn by the buddy's own renderer (ClaudeBuddy.exe --frames), so the
animation is exactly what appears on the desktop. This script only packs them into a GIF.

Needs Python 3 with Pillow. Run build.ps1 first, then:

    python docs/make_demo.py
"""
import glob
import os
import subprocess
import tempfile

from PIL import Image

TICK_MS = 80  # one animation tick

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
exe = os.path.join(root, "bin", "ClaudeBuddy.exe")
out = os.path.join(root, "docs", "demo.gif")

frames = []
with tempfile.TemporaryDirectory() as tmp:
    subprocess.run([exe, "--frames", tmp], check=True)
    for name in sorted(glob.glob(os.path.join(tmp, "frame-*.png"))):
        with Image.open(name) as image:
            frames.append(image.convert("RGB"))
if not frames:
    raise SystemExit("ClaudeBuddy.exe --frames wrote no frames")

# One palette for the whole animation, taken from a sample of the frames, so a flat
# colour is the same in every frame (no flicker) and nothing is dithered.
sample = frames[::4]
sheet = Image.new("RGB", (frames[0].width * len(sample), frames[0].height))
for i, frame in enumerate(sample):
    sheet.paste(frame, (i * frame.width, 0))
palette = sheet.quantize(colors=256, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE)
packed = [frame.quantize(palette=palette, dither=Image.Dither.NONE) for frame in frames]

packed[0].save(out, save_all=True, append_images=packed[1:], duration=TICK_MS, loop=0)
print("%s: %d frames, %.1f s, %d KB" % (
    os.path.relpath(out, root), len(packed), len(packed) * TICK_MS / 1000.0, os.path.getsize(out) // 1024))
