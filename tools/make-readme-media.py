"""Builds the README media from the app's own offscreen renders (demo data only).

    DeveloperIsland.exe --demo --snapshot=<dir>       # renders every state to PNG
    python tools/make-readme-media.py <dir>           # needs Pillow (pip install pillow)

Writes to docs/assets/:
  - the screenshots, copied under stable names (no editing, no mockups)
  - hero.gif: compact, live activity, Claude usage, Music, System and back, joined by short
    crossfades (the live app morphs with springs instead; see docs/RECORDING.md to record that)
  - hero.png: the expanded Claude usage view on the same background
  - social-preview.png: 1280 x 640 for the repository's social preview
"""

import shutil
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "docs" / "assets"
BACKGROUND = (242, 242, 245)
INK = (29, 29, 31)
MUTED = (110, 110, 115)

# Snapshot name -> asset name.
SCREENSHOTS = {
    "01-compact": "compact",
    "50-compact-claude": "compact-featured-claude",
    "50-compact-system": "compact-featured-system",
    "03-activity-music": "activity-music",
    "04-activity-claude": "activity-claude",
    "10-expanded-usage": "expanded-usage",
    "11-usage-tooltip": "usage-tooltip",
    "10-expanded-music": "expanded-music",
    "10-expanded-system": "expanded-system",
    "10-expanded-focus": "expanded-focus",
    "10-expanded-calendar": "expanded-calendar",
    "10-expanded-git": "expanded-git",
    "10-expanded-github": "expanded-github",
    "10-expanded-tasks": "expanded-tasks",
    "20-notch": "notch",
    "60-privacy-compact-mic": "privacy-compact",
    "30-settings-modules": "settings-modules",
    "30-settings-position": "settings-position",
    "30-settings-position-end": "settings-auto-hide",
}

# The hero animation: (snapshot, seconds on screen).
SEQUENCE = [
    ("50-compact-claude", 1.6),
    ("04-activity-claude", 1.5),
    ("10-expanded-usage", 2.8),
    ("10-expanded-music", 1.8),
    ("10-expanded-system", 2.0),
    ("50-compact-claude", 0.9),
]
FADE_FRAMES = 6
FADE_SECONDS = 0.22
CANVAS = (680, 950)
TOP = 30  # where the capsule's top edge sits, like the island under the screen edge


def capsule_box(image):
    """Bounding box of the opaque capsule, without its soft shadow."""
    alpha = image.getchannel("A").point(lambda a: 255 if a > 230 else 0)
    return alpha.getbbox()


def place(image, canvas_size=CANVAS, top=TOP):
    """The render on the background, capsule centered and its top at `top`."""
    left, upper, right, _ = capsule_box(image)
    canvas = Image.new("RGBA", canvas_size, BACKGROUND + (255,))
    x = canvas_size[0] // 2 - (left + right) // 2
    canvas.alpha_composite(image, (x, top - upper))
    return canvas.convert("RGB")


def hero_gif(snaps, out):
    frames, durations = [], []
    stills = [(place(Image.open(snaps / f"{name}.png").convert("RGBA")), seconds) for name, seconds in SEQUENCE]
    for i, (still, seconds) in enumerate(stills):
        frames.append(still)
        durations.append(int(seconds * 1000))
        if i + 1 < len(stills):
            nxt = stills[i + 1][0]
            for step in range(1, FADE_FRAMES + 1):
                frames.append(Image.blend(still, nxt, step / (FADE_FRAMES + 1)))
                durations.append(int(FADE_SECONDS * 1000 / FADE_FRAMES))

    # One shared palette keeps the file small and the colors stable between frames.
    palette = frames[2 * (FADE_FRAMES + 1)].quantize(colors=255, method=Image.Quantize.MEDIANCUT)
    quantized = [f.quantize(palette=palette, dither=Image.Dither.NONE) for f in frames]
    quantized[0].save(out, save_all=True, append_images=quantized[1:], duration=durations, loop=0, optimize=True, disposal=1)


def font(names, size):
    for name in names:
        path = Path("C:/Windows/Fonts") / name
        if path.exists():
            return ImageFont.truetype(str(path), size)
    return ImageFont.load_default()


def social_preview(snaps, out):
    canvas = Image.new("RGBA", (1280, 640), BACKGROUND + (255,))
    island = Image.open(snaps / "10-expanded-usage.png").convert("RGBA")
    scale = 600 / island.height
    island = island.resize((round(island.width * scale), 600), Image.LANCZOS)
    canvas.alpha_composite(island, (1280 - island.width - 70, 20))

    draw = ImageDraw.Draw(canvas)
    draw.text((90, 230), "Developer Island", font=font(["seguisb.ttf", "segoeuib.ttf"], 64), fill=INK)
    draw.text((92, 320), "The developer status bar Windows never had.", font=font(["segoeui.ttf"], 30), fill=MUTED)
    draw.text((92, 380), "AI usage, music, focus and system telemetry", font=font(["segoeui.ttf"], 24), fill=MUTED)
    draw.text((92, 414), "in one native capsule for Windows.", font=font(["segoeui.ttf"], 24), fill=MUTED)
    canvas.convert("RGB").save(out, optimize=True)


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    snaps = Path(sys.argv[1])
    ASSETS.mkdir(parents=True, exist_ok=True)
    for snapshot, asset in SCREENSHOTS.items():
        shutil.copyfile(snaps / f"{snapshot}.png", ASSETS / f"{asset}.png")

    hero_gif(snaps, ASSETS / "hero.gif")
    place(Image.open(snaps / "10-expanded-usage.png").convert("RGBA")).save(ASSETS / "hero.png", optimize=True)
    social_preview(snaps, ASSETS / "social-preview.png")
    for name in ("hero.gif", "hero.png", "social-preview.png"):
        print(f"{name}: {(ASSETS / name).stat().st_size / 1024:.0f} KB")


if __name__ == "__main__":
    main()
