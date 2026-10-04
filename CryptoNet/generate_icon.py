"""Generates the Crypto.Net NuGet/app icon (assets/icon.png 512x512 and assets/icon.svg).

The design follows the Gallery's security-printing identity: a guilloché rosette engraved in mint and
OVI-violet on intaglio green, around the quatrefoil brand mark.
Built by Gravicode Studios, led by Kang Fadhil.

    python generate_icon.py
"""
import math
from pathlib import Path

from PIL import Image, ImageDraw

INK_TOP = (18, 52, 44)
INK_BOTTOM = (8, 24, 20)
MINT = (127, 214, 180)
VIOLET = (185, 163, 255)
PAPER = (233, 238, 234)

# Rings: radius, lobes, amplitude, copies, colour, width (in 512-px units)
RINGS = [
    (196, 18, 10, 9, MINT, 2.0),
    (150, 11, 17, 7, VIOLET, 2.2),
    (104, 8, 12, 6, MINT, 2.0),
]


def ring_points(cx, cy, scale, radius, lobes, amp, phase, steps=900):
    pts = []
    for i in range(steps + 1):
        th = 2 * math.pi * i / steps
        r = radius + amp * math.sin(lobes * th + phase) + 2.5 * math.sin((2 * lobes + 1) * th - 2 * phase)
        pts.append((cx + r * scale * math.cos(th), cy + r * scale * math.sin(th)))
    return pts


def petal_path(cx, cy, s):
    """Quatrefoil brand mark: four lens-shaped petals (as in the Gallery logo)."""
    petals = []
    for angle in (0, 90, 180, 270):
        a = math.radians(angle)
        pts = []
        for i in range(61):
            t = i / 60
            # lens from centre to tip, bulging sideways
            along = t * 46
            side = math.sin(math.pi * t) * 17 * (1 if i <= 60 else -1)
            pts.append((along, side))
        pts += [(46 * (1 - i / 60), -math.sin(math.pi * (1 - i / 60)) * 17) for i in range(61)]
        rot = [(cx + s * (x * math.cos(a) - y * math.sin(a)), cy + s * (x * math.sin(a) + y * math.cos(a))) for x, y in pts]
        petals.append(rot)
    return petals


def render_png(path: Path, size=512, supersample=4):
    S = size * supersample
    s = S / 512
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    # vertical ink gradient inside a rounded square
    grad = Image.new("RGBA", (S, S))
    gd = ImageDraw.Draw(grad)
    for y in range(S):
        t = y / (S - 1)
        c = tuple(int(INK_TOP[i] + (INK_BOTTOM[i] - INK_TOP[i]) * t) for i in range(3)) + (255,)
        gd.line([(0, y), (S, y)], fill=c)
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, S - 1, S - 1], radius=int(112 * s), fill=255)
    img.paste(grad, (0, 0), mask)

    d = ImageDraw.Draw(img)
    cx = cy = S / 2
    for radius, lobes, amp, copies, colour, width in RINGS:
        for c in range(copies):
            phase = 2 * math.pi * c / (copies * lobes)
            d.line(ring_points(cx, cy, s, radius, lobes, amp, phase), fill=colour + (205,), width=max(1, int(width * s)), joint="curve")

    # centre seal
    d.ellipse([cx - 66 * s, cy - 66 * s, cx + 66 * s, cy + 66 * s], fill=PAPER + (255,))
    d.ellipse([cx - 66 * s, cy - 66 * s, cx + 66 * s, cy + 66 * s], outline=VIOLET + (255,), width=int(4 * s))
    for petal in petal_path(cx, cy, s):
        d.polygon(petal, fill=INK_TOP + (255,))
    d.ellipse([cx - 13 * s, cy - 13 * s, cx + 13 * s, cy + 13 * s], fill=VIOLET + (255,))

    img.resize((size, size), Image.LANCZOS).save(path)


def render_svg(path: Path):
    def poly(points):
        return " ".join(f"{x:.1f},{y:.1f}" for x, y in points)

    parts = [
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512">',
        '<defs><linearGradient id="ink" x1="0" y1="0" x2="0" y2="1">'
        f'<stop offset="0" stop-color="rgb{INK_TOP}"/><stop offset="1" stop-color="rgb{INK_BOTTOM}"/></linearGradient></defs>',
        '<rect width="512" height="512" rx="112" fill="url(#ink)"/>',
        '<g fill="none" stroke-linejoin="round" opacity="0.82">',
    ]
    for radius, lobes, amp, copies, colour, width in RINGS:
        for c in range(copies):
            phase = 2 * math.pi * c / (copies * lobes)
            parts.append(f'<polygon points="{poly(ring_points(256, 256, 1, radius, lobes, amp, phase, 600))}" stroke="rgb{colour}" stroke-width="{width}"/>')
    parts.append("</g>")
    parts.append(f'<circle cx="256" cy="256" r="66" fill="rgb{PAPER}" stroke="rgb{VIOLET}" stroke-width="4"/>')
    for petal in petal_path(256, 256, 1):
        parts.append(f'<polygon points="{poly(petal)}" fill="rgb{INK_TOP}"/>')
    parts.append(f'<circle cx="256" cy="256" r="13" fill="rgb{VIOLET}"/>')
    parts.append("</svg>")
    path.write_text("\n".join(parts), encoding="utf-8")


if __name__ == "__main__":
    out = Path(__file__).parent / "assets"
    out.mkdir(exist_ok=True)
    render_png(out / "icon.png")
    render_svg(out / "icon.svg")
    print("Wrote assets/icon.png and assets/icon.svg")
