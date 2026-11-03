#!/usr/bin/env python3
"""Draw og-image.png (1200x630) for the project site.

Deterministic and measured: every text run is checked against the frame before it
is drawn, and the script fails rather than producing an image with a clipped word.
An Open Graph image is the only part of a shared link that most people ever read,
and it is easy to change the site's copy and forget that the picture still says
something else - which is why the claims here are passed in on the command line
rather than left as literals to drift.

    python3 tools/make-og-image.py --out og-image.png \
        --badges "Windows x64" "Windows ARM64" "Authenticode signed"

Fonts are resolved by name from fontconfig with a DejaVu fallback, because the
machine this runs on may not have the one the last image was made with.
"""
from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

W, H = 1200, 630
MARGIN = 90

BG = (2, 6, 23)          # slate-950
ACCENT = (99, 102, 241)  # indigo-500
TITLE = (255, 255, 255)
SUB = (203, 213, 225)    # slate-300
DIM = (148, 163, 184)    # slate-400
BADGE_BG = (30, 41, 59)  # slate-800


def find_font(pattern: str, size: int) -> ImageFont.FreeTypeFont:
    """Load the first font fontconfig knows about whose path matches a pattern."""
    out = subprocess.run(
        ["fc-list", "--format", "%{file}\\n"], capture_output=True, text=True, check=False
    )
    if out.returncode == 0:
        for line in out.stdout.splitlines():
            if pattern.lower() in line.lower() and Path(line).exists():
                return ImageFont.truetype(line, size)
    for name in ("DejaVuSans.ttf", "DejaVuSans-Bold.ttf", "LiberationSans-Regular.ttf"):
        found = shutil.which(name) or ""
        for cand in (found, f"/usr/share/fonts/truetype/dejavu/{name}"):
            if cand and Path(cand).exists():
                return ImageFont.truetype(cand, size)
    return ImageFont.load_default(size)


def draw_centered_run(
    d: ImageDraw.ImageDraw, text: str, font: ImageFont.FreeTypeFont,
    x: int, y: int, fill, frame_left: int, frame_right: int, label: str,
) -> int:
    """Draw one run of text at x, growing rightward, and return the new x.

    Raises instead of drawing if the run would cross the frame - a silently clipped
    badge reads as a rendering bug in the site, not as a bug in this script.
    """
    w = int(font.getlength(text))
    if x + w > frame_right:
        raise SystemExit(
            f"{label} '{text}' is {w}px wide and would end at x={x + w} "
            f"(frame ends at {frame_right}). Shorten it or use a smaller size."
        )
    d.text((x, y), text, font=font, fill=fill)
    return x + w


def build(badges: list[str], headline: str, subtitle: str, footer: str) -> Image.Image:
    img = Image.new("RGB", (W, H), BG)
    d = ImageDraw.Draw(img)

    f_title = find_font("bold", 128)
    f_sub = find_font("sans", 40)
    f_badge = find_font("sans", 27)
    f_foot = find_font("sans", 25)

    left, right = MARGIN, W - MARGIN

    # Accent bar: a thin rule so the image reads as deliberate at thumbnail size.
    d.rectangle([left, 108, left + 96, 116], fill=ACCENT)

    # Title, then check it fits rather than trusting the size.
    tw = int(f_title.getlength(headline))
    if tw > right - left:
        raise SystemExit(f"headline '{headline}' is {tw}px; frame is {right - left}px wide")
    d.text((left, 150), headline, font=f_title, fill=TITLE)

    # Subtitle may need two lines; wrapping is computed, not hardcoded.
    y = 320
    words = subtitle.split()
    line: list[str] = []
    for word in words:
        trial = " ".join(line + [word])
        if f_sub.getlength(trial) > right - left and line:
            d.text((left, y), " ".join(line), font=f_sub, fill=SUB)
            y += 56
            line = [word]
        else:
            line.append(word)
    if line:
        last = " ".join(line)
        if f_sub.getlength(last) > right - left:
            raise SystemExit(f"subtitle line '{last}' exceeds the frame")
        d.text((left, y), last, font=f_sub, fill=SUB)
        y += 56

    # Badges, evenly spaced and verified to stop inside the frame.
    by = y + 48
    x = left
    for i, text in enumerate(badges):
        pad_x, gap = 20, 22
        tw = int(f_badge.getlength(text))
        if x + pad_x * 2 + tw > right:
            raise SystemExit(
                f"badge '{text}' overflows at x={x}; frame ends at {right}. "
                "Drop a badge or shorten one."
            )
        d.rounded_rectangle(
            [x, by - 8, x + tw + pad_x * 2, by + 40], radius=8, fill=BADGE_BG, outline=ACCENT
        )
        d.text((x + pad_x, by), text, font=f_badge, fill=(226, 232, 240))
        x += tw + pad_x * 2
        if i < len(badges) - 1:
            x += 18

    d.text((left, H - 96), footer, font=f_foot, fill=DIM)
    return img


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="og-image.png")
    ap.add_argument("--headline", default="MsiForge")
    ap.add_argument("--sub", default="Build MSI packages that upgrade cleanly in Intune")
    ap.add_argument("--footer", default="MIT licensed - pure managed C# - no WiX, no native dependencies")
    ap.add_argument("--badges", nargs="*", default=["Windows x64", "Windows ARM64", "Authenticode signed"])
    a = ap.parse_args()

    img = build(a.badges, a.headline, a.sub, a.footer)
    img.save(a.out, optimize=True)
    print(f"{a.out}: {img.size[0]}x{img.size[1]}, {len(img.getcolors(maxcolors=1_000_000))} colours")
    return 0


if __name__ == "__main__":
    sys.exit(main())
