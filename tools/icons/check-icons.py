#!/usr/bin/env python3
"""Render the generated Android icon path data and check its geometry. Needs rsvg-convert and ImageMagick (magick).

It renders exactly the strings that the app draws (from lucide-to-cs.py --print), not the source SVGs:
  - every icon at 256 px, stroke 1.8 on the 24 grid with round caps and joins (as Controls/IconView.cs draws them);
  - a mirror test for the icons that must be symmetric: the image is flipped and compared pixel by pixel
    (magick compare -metric AE with a 10 % fuzz for anti-aliasing); any differing pixel fails;
  - a check that every icon stays inside the 24 x 24 grid including the stroke;
  - a contact sheet of all icons (light and dark) to look at.

Usage: python3 tools/icons/check-icons.py [out_dir]     (default: <system temp>/vpnrouter-icon-check)
Exit code 0 when all checks pass.
"""
import os
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
STROKE = 1.8

# Icons that must look the same after a left-right flip (h) or a top-bottom flip (v).
SYMMETRIC = {
    "power": "h", "x": "hv", "plus": "hv", "minus": "hv", "globe": "hv", "square": "hv", "circle-x": "hv",
    "ellipsis-vertical": "hv", "chevron-down": "h", "chevron-right": "v", "chevron-left": "v", "arrow-up": "h",
    "arrow-down": "h", "info": "h", "trash": "h", "circle-check": "", "search": "",
}


def icons():
    out = subprocess.run([sys.executable, os.path.join(HERE, "lucide-to-cs.py"), "--print"],
                         check=True, capture_output=True, text=True).stdout
    return [tuple(line.split("\t", 1)) for line in out.splitlines() if line]


def svg(data, color="#000", bg=None, size=256):
    rect = '<rect width="24" height="24" fill="%s"/>' % bg if bg else ""
    return ('<svg xmlns="http://www.w3.org/2000/svg" width="%d" height="%d" viewBox="0 0 24 24">%s'
            '<path d="%s" fill="none" stroke="%s" stroke-width="%s" stroke-linecap="round" stroke-linejoin="round"/>'
            '</svg>' % (size, size, rect, data, color, STROKE))


def render(text, png):
    subprocess.run(["rsvg-convert", "-o", png], input=text.encode(), check=True)


def mirror_diff(png, flag):
    flipped = png[:-4] + "-mirror.png"
    subprocess.run(["magick", png, "-flop" if flag == "h" else "-flip", flipped], check=True)
    r = subprocess.run(["magick", "compare", "-metric", "AE", "-fuzz", "10%", png, flipped, "null:"],
                       capture_output=True, text=True)
    os.remove(flipped)
    return int(float(r.stderr.split()[0]))


def inside_grid(data):
    """Render on a 32-unit canvas (10 px per unit) with the grid in the middle, mask the grid and look for ink."""
    t = ('<svg xmlns="http://www.w3.org/2000/svg" width="320" height="320" viewBox="-4 -4 32 32">'
         '<path d="%s" fill="none" stroke="#000" stroke-width="%s" stroke-linecap="round" stroke-linejoin="round"/>'
         '</svg>' % (data, STROKE))
    r = subprocess.run(["rsvg-convert"], input=t.encode(), capture_output=True, check=True)
    m = subprocess.run(["magick", "-", "-alpha", "extract", "-fill", "black", "-draw", "rectangle 40,40 279,279", "-alpha", "off",
                        "-format", "%[max]", "info:"], input=r.stdout, capture_output=True, check=True)
    return float(m.stdout or b"0") == 0.0


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(tempfile.gettempdir(), "vpnrouter-icon-check")
    os.makedirs(out, exist_ok=True)
    failures = []
    tiles = []
    for name, data in icons():
        png = os.path.join(out, name + ".png")
        render(svg(data), png)
        for flag in SYMMETRIC.get(name, ""):
            diff = mirror_diff(png, flag)
            status = "ok" if diff == 0 else "FAIL"
            print("%-18s mirror-%s %s (%d px differ)" % (name, flag, status, diff))
            if diff:
                failures.append("%s mirror-%s" % (name, flag))
        if not inside_grid(data):
            print("%-18s outside the 24 grid FAIL" % name)
            failures.append(name + " grid")
        for theme, fg, bg in (("l", "#1c1b1f", "#ffffff"), ("d", "#e6e1e5", "#1c1b1f")):
            tile = os.path.join(out, "tile-%s-%s.png" % (theme, name))
            render(svg(data, fg, bg, 96), tile)
            tiles.append((theme, name, tile))
    for theme in ("l", "d"):
        files = [t for th, _, t in tiles if th == theme]
        labels = [n for th, n, _ in tiles if th == theme]
        args = ["magick", "montage"]
        for f, label in zip(files, labels):
            args += ["-label", label, f]
        args += ["-tile", "7x", "-geometry", "96x96+12+12", "-pointsize", "11",
                 "-background", "#ffffff" if theme == "l" else "#1c1b1f",
                 "-fill", "#000000" if theme == "l" else "#e6e1e5",
                 os.path.join(out, "sheet-%s.png" % ("light" if theme == "l" else "dark"))]
        subprocess.run(args, check=True)
    print("contact sheets: %s/sheet-light.png %s/sheet-dark.png" % (out, out))
    if failures:
        print("FAILED: " + ", ".join(failures))
        sys.exit(1)
    print("all checks passed")


if __name__ == "__main__":
    main()
