#!/usr/bin/env python3
"""Draws the README's images (banner, badges, cards) into docs/assets/readme.

Run from the repository root after a version change: python tools/readme-assets.py
The release badge reads InformationalVersion from the plugin project."""
import math
import os
import re
import sys
from xml.sax.saxutils import escape

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "docs", "assets", "readme")
os.makedirs(OUT, exist_ok=True)
with open(os.path.join(ROOT, "NVEncVideoWriterPlugin", "NVEncVideoWriterPlugin.csproj"), encoding="utf-8") as project:
    VERSION = re.search(r"<InformationalVersion>([^<]+)</InformationalVersion>", project.read()).group(1)
FONT = "'Segoe UI','Hiragino Sans','Yu Gothic UI','Noto Sans JP',Meiryo,Verdana,sans-serif"


def width(text, size):
    """Rough advance width of text at a font size (Verdana-like for ASCII, full width for CJK)."""
    total = 0.0
    for ch in text:
        o = ord(ch)
        if o > 0x2E80:
            total += 1.0
        elif ch in " ":
            total += 0.33
        elif ch in ".,:;|!il1'·":
            total += 0.33
        elif ch.isupper() or ch in "mwMW%&@":
            total += 0.72
        elif ch.isdigit():
            total += 0.62
        else:
            total += 0.58
    return total * size


def write(name, svg):
    with open(os.path.join(OUT, name), "w", encoding="utf-8") as f:
        f.write(svg)


# ---- badges: label | value, flat, 26 px high ----
def badge(name, label, value, value_bg, value_fg="#0d1117"):
    size, height, pad = 14, 28, 11
    lw = max(round(width(label, size)), 18) + 2 * pad
    vw = max(round(width(value, size)), 18) + 2 * pad
    total = lw + vw
    svg = f'''<svg xmlns="http://www.w3.org/2000/svg" width="{total}" height="{height}" viewBox="0 0 {total} {height}" role="img" aria-label="{escape(label)}: {escape(value)}">
  <title>{escape(label)}: {escape(value)}</title>
  <rect width="{lw}" height="{height}" fill="#16191d"/>
  <rect x="{lw}" width="{vw}" height="{height}" fill="{value_bg}"/>
  <g font-family="{FONT}" font-size="{size}" text-anchor="middle" dominant-baseline="central">
    <text x="{lw / 2}" y="{height / 2 + 0.5}" fill="#f0f3f6" textLength="{lw - 2 * pad}" lengthAdjust="spacingAndGlyphs">{escape(label)}</text>
    <text x="{lw + vw / 2}" y="{height / 2 + 0.5}" fill="{value_fg}" textLength="{vw - 2 * pad}" lengthAdjust="spacingAndGlyphs">{escape(value)}</text>
  </g>
</svg>
'''
    write(name, svg)


badge("badge-release.svg", "release", "v" + VERSION, "#8fd3f4")
badge("badge-ymm4.svg", "YMM4 Lite", "4.56.1.0", "#8fd3f4")
badge("badge-nvenc.svg", "NVENC", "H.264 · HEVC · AV1", "#76b900", "#0d1117")
badge("badge-dotnet.svg", ".NET", "10", "#b9a6f5")
badge("badge-license.svg", "license", "MIT", "#c9ecfb")
badge("badge-download.svg", "download", ".ymme", "#7c8cff", "#ffffff")


# ---- cards: icon | small caps label / bold line ----
PURPLE = "#8250df"


def card(name, label, line, icon):
    w, h = 520, 100
    icon_svg = {
        "seal": f'''<g transform="translate(58 50)" fill="none" stroke="{PURPLE}" stroke-width="2.5" stroke-linejoin="round" stroke-linecap="round">
      <path d="{star_path(0, 0, 27, 22, 12)}"/>
      <path d="M-9 0 l6 7 l12 -14"/>
    </g>''',
        "pixels": f'''<g transform="translate(36 28)" fill="{PURPLE}">
      {''.join(f'<rect x="{x * 11}" y="{y * 11}" width="9" height="9" rx="1.5" opacity="{0.35 if (x + y) % 3 else 1}"/>' for x in range(4) for y in range(4))}
    </g>
    <g transform="translate(58 50)" fill="none" stroke="#ffffff" stroke-width="5" stroke-linecap="round" stroke-linejoin="round"><path d="M-7 2 l5 6 l11 -14"/></g>
    <g transform="translate(58 50)" fill="none" stroke="{PURPLE}" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><path d="M-7 2 l5 6 l11 -14"/></g>''',
        "chip": f'''<g transform="translate(58 50)" fill="none" stroke="{PURPLE}" stroke-width="2.5" stroke-linecap="round">
      <rect x="-17" y="-17" width="34" height="34" rx="5"/>
      <rect x="-8" y="-8" width="16" height="16" rx="2" fill="{PURPLE}" fill-opacity="0.25"/>
      {''.join(f'<path d="M{p} -17 v-7 M{p} 17 v7 M-17 {p} h-7 M17 {p} h7"/>' for p in (-9, 0, 9))}
    </g>''',
    }[icon]
    svg = f'''<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="{h}" viewBox="0 0 {w} {h}" role="img" aria-label="{escape(label)} — {escape(line)}">
  <title>{escape(label)} — {escape(line)}</title>
  <rect x="1.5" y="1.5" width="{w - 3}" height="{h - 3}" rx="16" fill="none" stroke="{PURPLE}" stroke-width="2.5"/>
  {icon_svg}
  <text x="104" y="40" font-family="{FONT}" font-size="15" letter-spacing="2.2" font-weight="600" fill="{PURPLE}">{escape(label)}</text>
  <text x="104" y="72" font-family="{FONT}" font-size="25" font-weight="700" fill="{PURPLE}">{escape(line)}</text>
</svg>
'''
    write(name, svg)


def star_path(cx, cy, outer, inner, points):
    pts = []
    for i in range(points * 2):
        r = outer if i % 2 == 0 else inner
        a = math.pi * i / points - math.pi / 2
        pts.append(f"{cx + r * math.cos(a):.2f} {cy + r * math.sin(a):.2f}")
    return "M" + " L".join(pts) + " Z"


card("card-real-host.svg", "TESTED ON THE REAL HOST", "実物の YMM4 Lite 4.56.1.0 で検査", "seal")
card("card-pixel-parity.svg", "PIXEL PARITY", "YMM4 自身の描画と画素一致", "pixels")
card("card-rtx3060.svg", "NVIDIA GEFORCE RTX 3060", "実機で NVENC 出力を確認", "chip")


# ---- hero: a film strip of frames, cached in RAM / on disk / on the GPU, and a playhead ----
def hero():
    W, H = 1280, 400
    parts = [f'''<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" viewBox="0 0 {W} {H}" role="img" aria-label="フレームのフィルム。描いたフレームが RAM・ディスク・GPU に保存され、再生ヘッドが進む">
  <title>YMM4 NVENC・描画キャッシュ</title>
  <defs>
    <linearGradient id="bg" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#0a0f1c"/><stop offset="1" stop-color="#020306"/>
    </linearGradient>
    <radialGradient id="glow" cx="640" cy="-20" r="560" gradientUnits="userSpaceOnUse">
      <stop offset="0" stop-color="#7fdfff" stop-opacity="0.75"/>
      <stop offset="0.25" stop-color="#2f81f7" stop-opacity="0.32"/>
      <stop offset="1" stop-color="#2f81f7" stop-opacity="0"/>
    </radialGradient>
    <linearGradient id="beam" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#bfefff" stop-opacity="0.9"/>
      <stop offset="1" stop-color="#bfefff" stop-opacity="0"/>
    </linearGradient>
    <linearGradient id="fade" x1="0" y1="0" x2="1" y2="0">
      <stop offset="0" stop-color="#020306" stop-opacity="1"/>
      <stop offset="0.05" stop-color="#020306" stop-opacity="0"/>
      <stop offset="0.95" stop-color="#020306" stop-opacity="0"/>
      <stop offset="1" stop-color="#020306" stop-opacity="1"/>
    </linearGradient>
    <filter id="soft" x="-50%" y="-50%" width="200%" height="200%"><feGaussianBlur stdDeviation="6"/></filter>
  </defs>
  <rect width="{W}" height="{H}" fill="url(#bg)"/>
  <rect width="{W}" height="{H}" fill="url(#glow)"/>''']
    # perspective floor
    horizon, floor = 150, H
    lines = []
    for i in range(-14, 15):
        x = 640 + i * 95
        lines.append(f'<path d="M640 {horizon} L{x} {floor}"/>')
    for j in range(1, 9):
        t = (j / 9) ** 2
        y = horizon + (floor - horizon) * t
        lines.append(f'<path d="M0 {y:.1f} H{W}"/>')
    parts.append(f'  <g stroke="#2f81f7" stroke-opacity="0.13" stroke-width="1">{"".join(lines)}</g>')
    # light beam from the top
    parts.append('  <path d="M610 0 L670 0 L700 260 L580 260 Z" fill="url(#beam)" opacity="0.22"/>')
    # film strip
    sx, sy, sw, sh = 70, 186, 1140, 150
    parts.append(f'  <rect x="{sx}" y="{sy}" width="{sw}" height="{sh}" rx="10" fill="#0b0f16" stroke="#2b3240" stroke-width="2"/>')
    holes = []
    for k in range(38):
        hx = sx + 14 + k * 29.6
        holes.append(f'<rect x="{hx:.1f}" y="{sy + 9}" width="14" height="10" rx="2.5"/><rect x="{hx:.1f}" y="{sy + sh - 19}" width="14" height="10" rx="2.5"/>')
    parts.append(f'  <g fill="#1c2230">{"".join(holes)}</g>')
    n = 12
    gap = 10
    fw = (sw - 28 - gap * (n - 1)) / n
    fh = sh - 2 * 30
    states = ["gpu", "gpu", "ram", "ram", "ram", "ram", "disk", "disk", "now", "none", "none", "none"]
    colors = {
        "gpu": ("#2dd4bf", "#5eead4"),
        "ram": ("#2ea043", "#56d364"),
        "disk": ("#1f6feb", "#58a6ff"),
        "now": ("#d29922", "#f2cc60"),
        "none": ("#161b22", "#30363d"),
    }
    frames = []
    for i, state in enumerate(states):
        fx = sx + 14 + i * (fw + gap)
        fy = sy + 30
        fill, stroke = colors[state]
        opacity = 0.28 if state != "none" else 1
        frames.append(f'<rect x="{fx:.1f}" y="{fy}" width="{fw:.1f}" height="{fh}" rx="6" fill="{fill}" fill-opacity="{opacity}" stroke="{stroke}" stroke-width="2"/>')
        # the animated object: a ball that crosses the frame along an arc
        t = i / (n - 1)
        bx = fx + 14 + t * (fw - 28)
        by = fy + fh - 14 - math.sin(t * math.pi) * (fh - 34)
        ball = "#e6edf3" if state != "none" else "#3d4450"
        frames.append(f'<circle cx="{bx:.1f}" cy="{by:.1f}" r="8" fill="{ball}"/>')
    parts.append("  " + "".join(frames))
    # cache bar under the strip, like the tool's band
    by = sy + sh + 22
    seg = []
    bw = sw - 28
    spans = [("gpu", 0, 2), ("ram", 2, 6), ("disk", 6, 8), ("now", 8, 9)]
    for state, a, b in spans:
        x0 = sx + 14 + a * (fw + gap)
        x1 = sx + 14 + b * (fw + gap) - gap
        seg.append(f'<rect x="{x0:.1f}" y="{by}" width="{x1 - x0:.1f}" height="8" rx="4" fill="{colors[state][1]}"/>')
    parts.append(f'  <rect x="{sx + 14}" y="{by}" width="{bw}" height="8" rx="4" fill="#1c2230"/>' + "".join(seg))
    # playhead
    px = sx + 14 + 8 * (fw + gap) + fw / 2
    parts.append(f'  <g><path d="M{px:.1f} {sy - 26} V{by + 26}" stroke="#f2cc60" stroke-width="8" opacity="0.35" filter="url(#soft)"/>'
                 f'<path d="M{px:.1f} {sy - 26} V{by + 26}" stroke="#f2cc60" stroke-width="2.5"/>'
                 f'<path d="M{px - 11:.1f} {sy - 38} H{px + 11:.1f} L{px:.1f} {sy - 22} Z" fill="#f2cc60"/></g>')
    parts.append(f'  <rect width="{W}" height="{H}" fill="url(#fade)"/>')
    parts.append("</svg>\n")
    write("hero.svg", "\n".join(parts))


hero()
print("ok")
