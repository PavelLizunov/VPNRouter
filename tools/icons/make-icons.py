#!/usr/bin/env python3
"""Builds the platform icons from the ORIGINAL mascot art in tools/icons/src (mascot-black.png, mascot-white.png).

The drawing itself is never changed: it is only scaled and placed on a rounded tile so it stays visible on dark and light
surfaces (a light tile for the black line art, a dark tile for the white variant). Needs ImageMagick (magick); the generated
files are committed, so a normal build does not need it. Run from the repository root: python3 tools/icons/make-icons.py
"""
import os, shutil, struct, subprocess, tempfile

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SRC = os.path.join(ROOT, "tools", "icons", "src")
APP = os.path.join(ROOT, "VPNRouter.App", "Assets")
RES = os.path.join(ROOT, "VPNRouter.Android", "Resources")
TMP = tempfile.mkdtemp(prefix="vpnr-icons-")
BLACK, WHITE = os.path.join(SRC, "mascot-black.png"), os.path.join(SRC, "mascot-white.png")
LIGHT = ("#FFFFFF", "#E6ECF4")      # tile for the black line art
DARK = ("#1E293B", "#0F1320")       # tile for the white line art

def run(*a):
    subprocess.run(a, check=True)

def tile(size, path, art, colors, scale=0.80, radius=0.22, disc=False):
    """rounded tile (or disc) with a vertical gradient and the art centred at `scale` of its size"""
    bg = os.path.join(TMP, f"bg{size}{colors[0]}{int(disc)}.png")
    run("magick", "-size", f"{size}x{size}", f"gradient:{colors[0]}-{colors[1]}", bg)
    mask = os.path.join(TMP, f"mask{size}{int(disc)}.png")
    if disc:
        run("magick", "-size", f"{size}x{size}", "xc:black", "-fill", "white", "-draw", f"circle {size/2},{size/2} {size/2},0", mask)
    else:
        r = size * radius
        run("magick", "-size", f"{size}x{size}", "xc:black", "-fill", "white", "-draw", f"roundrectangle 0,0 {size-1},{size-1} {r},{r}", mask)
    inner = int(round(size * scale))
    run("magick", bg, "(", art, "-resize", f"{inner}x{inner}", ")", "-gravity", "center", "-compose", "over", "-composite",
        mask, "-alpha", "off", "-compose", "CopyOpacity", "-composite", path)

def art_layer(size, path, art, pad):
    """the art alone on a transparent square canvas (Android adaptive layers)"""
    inner = int(round(size * (1 - 2 * pad)))
    run("magick", "-size", f"{size}x{size}", "xc:none", "(", art, "-resize", f"{inner}x{inner}", ")", "-gravity", "center", "-composite", path)

def ico(sizes, art, colors, path):
    frames = []
    for s in sizes:
        p = os.path.join(TMP, f"ico{s}{colors[0]}.png"); tile(s, p, art, colors, scale=0.84, radius=0.2); frames.append(p)
    run("magick", *frames, path)

def icns(pngs, path):
    chunks = b""
    for kind, file in pngs:
        data = open(file, "rb").read()
        chunks += kind.encode() + struct.pack(">I", len(data) + 8) + data
    open(path, "wb").write(b"icns" + struct.pack(">I", len(chunks) + 8) + chunks)

# in-app images: the original art files stay byte-identical (penguin_mascot.png, penguin_mascot_white.png);
# the tile and logo are the art on a light tile
tile(640, os.path.join(APP, "penguin_mascot_tile.png"), BLACK, LIGHT)
tile(640, os.path.join(APP, "penguin_logo.png"), BLACK, LIGHT)

# Windows: light tile with the black art (exe, taskbar, tray on a light theme), dark tile with the white art (tray on a dark theme)
SIZES = [16, 24, 32, 48, 64, 128, 256]
ico(SIZES, BLACK, LIGHT, os.path.join(APP, "penguin_mascot.ico"))
ico(SIZES, WHITE, DARK, os.path.join(APP, "penguin_mascot_white.ico"))

# macOS: 824 px tile centred on a 1024 canvas with a soft shadow
def mac_png(size, path):
    t = os.path.join(TMP, f"mac_t{size}.png")
    tile(int(round(size * 824 / 1024)), t, BLACK, LIGHT)
    run("magick", "-size", f"{size}x{size}", "xc:none", "(", t, "-background", "black", "-shadow", f"35x{max(1, size//60)}+0+{max(1, size//120)}", ")",
        "-gravity", "center", "-compose", "over", "-composite", t, "-gravity", "center", "-composite", path)
mac = []
for kind, size in (("icp4", 16), ("icp5", 32), ("icp6", 64), ("ic07", 128), ("ic08", 256), ("ic09", 512), ("ic10", 1024),
                   ("ic11", 32), ("ic12", 64), ("ic13", 256), ("ic14", 512)):
    p = os.path.join(TMP, f"mac{size}.png")
    if not os.path.exists(p): mac_png(size, p)
    mac.append((kind, p))
icns(mac, os.path.join(APP, "AppIcon.icns"))

# Android: adaptive layers (the art on transparent; background and themed layer use the same art), legacy square and round icons
for name, d in {"mdpi": 1, "hdpi": 1.5, "xhdpi": 2, "xxhdpi": 3, "xxxhdpi": 4}.items():
    folder = os.path.join(RES, f"mipmap-{name}"); os.makedirs(folder, exist_ok=True)
    canvas = int(round(108 * d))
    art_layer(canvas, os.path.join(folder, "ic_launcher_foreground.png"), BLACK, 0.19)
    art_layer(canvas, os.path.join(folder, "ic_launcher_monochrome.png"), BLACK, 0.19)
    legacy = int(round(48 * d))
    tile(legacy, os.path.join(folder, "ic_launcher.png"), BLACK, LIGHT, scale=0.84, radius=0.2)
    tile(legacy, os.path.join(folder, "ic_launcher_round.png"), BLACK, LIGHT, scale=0.74, disc=True)
shutil.rmtree(TMP, ignore_errors=True)
print("icons generated")
