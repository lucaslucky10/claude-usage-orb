"""Generates src/ClaudeUsageOrb/Assets/orb.ico (three rings on a dark disk). Requires Pillow."""
import math
import pathlib

from PIL import Image, ImageDraw

RINGS = [((0xF0, 0x8A, 0x5D), 0.86), ((0x5A, 0xA9, 0xF8), 0.67), ((0xB4, 0x8C, 0xFF), 0.82)]


def render(size: int) -> Image.Image:
    scale = 8  # supersample for smooth edges
    s = size * scale
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.ellipse((0, 0, s - 1, s - 1), fill=(0x1C, 0x1C, 0x20, 255), outline=(255, 255, 255, 60), width=max(1, s // 64))

    thickness = s * 0.085
    gap = s * 0.035
    r = s / 2 - s * 0.07 - thickness / 2
    c = s / 2
    for color, frac in RINGS:
        box = (c - r - thickness / 2, c - r - thickness / 2, c + r + thickness / 2, c + r + thickness / 2)
        d.ellipse(box, outline=color + (70,), width=int(thickness))
        d.arc(box, -90, -90 + 360 * frac, fill=color + (255,), width=int(thickness))
        # round caps
        for ang in (-90, -90 + 360 * frac):
            a = math.radians(ang)
            px, py = c + r * math.cos(a), c + r * math.sin(a)
            d.ellipse((px - thickness / 2, py - thickness / 2, px + thickness / 2, py + thickness / 2), fill=color + (255,))
        r -= thickness + gap
    return img.resize((size, size), Image.LANCZOS)


def main() -> None:
    out = pathlib.Path(__file__).resolve().parent.parent / "src" / "ClaudeUsageOrb" / "Assets" / "orb.ico"
    out.parent.mkdir(parents=True, exist_ok=True)
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    big = render(256)
    big.save(out, format="ICO", sizes=[(n, n) for n in sizes], append_images=[render(n) for n in sizes[:-1]])
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
