#!/usr/bin/env python3
"""Generate VPNRouter.Core/Services/UiIcons.cs from the Lucide SVG files in tools/icons/lucide/.

The SVG files are copied unchanged from the Lucide repository (ISC licence, see tools/icons/lucide/LICENSE and
NOTICE.md); the tag is in tools/icons/lucide/VERSION. Each icon becomes one Avalonia path string on the 24 x 24 grid:
circles, rects and lines become path commands, all commands are made absolute and written with explicit separators,
so Avalonia's parser never sees the compact SVG forms (".5.5", arc flags without spaces).

Usage: python3 tools/icons/lucide-to-cs.py            (writes the C# file)
       python3 tools/icons/lucide-to-cs.py --print    (prints name and path data, used by check-icons.py)
To add an icon: copy <name>.svg from https://github.com/lucide-icons/lucide/tree/<tag>/icons into tools/icons/lucide/,
run this script, then python3 tools/icons/check-icons.py, and commit the SVG and the generated file.
"""
import os
import re
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "lucide")
OUT = os.path.join(HERE, "..", "..", "VPNRouter.Core", "Services", "UiIcons.cs")

# Measured corrections of the source, applied after conversion. Keep this list short and explain every entry.
# power: the arc in Lucide ends at (5.63, 6.64), which is not the mirror of its start (18.4, 6.6) about x = 12 by 0.03
# and 0.04 units. The ring of the status card makes that visible as a tilt, so the end point is snapped to (5.6, 6.6).
# triangle-alert: the left side starts 0.02 units further out than the mirror of the right side (10.25 and 2.25 against
# 13.73 and 21.73 about x = 12), and the dot is drawn from x = 12 to 12.01; both are made symmetric about x = 12.
FIXES = {
    "power": [("5.63,6.64", "5.6,6.6")],
    "triangle-alert": [("10.25,4", "10.27,4"), ("2.25,18", "2.27,18"), ("M12,17 L12.01,17", "M11.995,17 L12.005,17")],
}

NUMBER = re.compile(r"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?")


def fmt(v):
    v = round(v, 3)
    if v == 0:
        v = 0.0
    s = ("%.3f" % v).rstrip("0").rstrip(".")
    return s if s != "-0" else "0"


def pt(x, y):
    return fmt(x) + "," + fmt(y)


def tokenize_path(d):
    out = []
    i = 0
    n = len(d)
    while i < n:
        c = d[i]
        if c in " ,\t\r\n":
            i += 1
            continue
        if c.isalpha():
            out.append(c)
            i += 1
            continue
        m = NUMBER.match(d, i)
        if not m:
            raise ValueError("bad path data near %r" % d[i:i + 20])
        out.append(m.group(0))
        i = m.end()
    return out


def split_flags(tokens):
    """Within arc commands a token like '012' is three numbers (flag, flag, coordinate)."""
    res = []
    cmd = None
    argi = 0
    for t in tokens:
        if t.isalpha():
            cmd = t
            argi = 0
            res.append(t)
            continue
        if cmd in ("A", "a"):
            pos = argi % 7
            if pos in (3, 4) and len(t) > 1 and t[0] in "01" and not t.startswith(("0.", "1.")):
                res.append(t[0])
                argi += 1
                rest = t[1:]
                pos = argi % 7
                if pos == 4 and len(rest) > 1 and rest[0] in "01" and not rest.startswith(("0.", "1.")):
                    res.append(rest[0])
                    argi += 1
                    rest = rest[1:]
                res.append(rest)
                argi += 1
                continue
        res.append(t)
        argi += 1
    return res


def path_to_abs(d):
    toks = split_flags(tokenize_path(d))
    out = []
    i = 0
    cx = cy = 0.0
    sx = sy = 0.0
    cmd = None
    last_ctrl = None
    last_cmd = None

    def num():
        nonlocal i
        v = float(toks[i])
        i += 1
        return v

    while i < len(toks):
        t = toks[i]
        if t.isalpha():
            cmd = t
            i += 1
            if cmd in "Zz":
                out.append("Z")
                cx, cy = sx, sy
                last_cmd = "Z"
                last_ctrl = None
                continue
        rel = cmd.islower()
        C = cmd.upper()
        if C == "M":
            x, y = num(), num()
            if rel:
                x += cx
                y += cy
            out.append("M" + pt(x, y))
            cx, cy = sx, sy = x, y
            cmd = "l" if rel else "L"
            last_ctrl = None
        elif C == "L":
            x, y = num(), num()
            if rel:
                x += cx
                y += cy
            out.append("L" + pt(x, y))
            cx, cy = x, y
            last_ctrl = None
        elif C == "H":
            x = num()
            if rel:
                x += cx
            out.append("L" + pt(x, cy))
            cx = x
            last_ctrl = None
        elif C == "V":
            y = num()
            if rel:
                y += cy
            out.append("L" + pt(cx, y))
            cy = y
            last_ctrl = None
        elif C == "C":
            x1, y1, x2, y2, x, y = (num() for _ in range(6))
            if rel:
                x1 += cx; y1 += cy; x2 += cx; y2 += cy; x += cx; y += cy
            out.append("C" + " ".join([pt(x1, y1), pt(x2, y2), pt(x, y)]))
            last_ctrl = ("C", x2, y2)
            cx, cy = x, y
        elif C == "S":
            x2, y2, x, y = (num() for _ in range(4))
            if rel:
                x2 += cx; y2 += cy; x += cx; y += cy
            if last_ctrl and last_ctrl[0] == "C":
                x1, y1 = 2 * cx - last_ctrl[1], 2 * cy - last_ctrl[2]
            else:
                x1, y1 = cx, cy
            out.append("C" + " ".join([pt(x1, y1), pt(x2, y2), pt(x, y)]))
            last_ctrl = ("C", x2, y2)
            cx, cy = x, y
        elif C == "Q":
            x1, y1, x, y = (num() for _ in range(4))
            if rel:
                x1 += cx; y1 += cy; x += cx; y += cy
            out.append("Q" + " ".join([pt(x1, y1), pt(x, y)]))
            last_ctrl = ("Q", x1, y1)
            cx, cy = x, y
        elif C == "T":
            x, y = num(), num()
            if rel:
                x += cx; y += cy
            if last_ctrl and last_ctrl[0] == "Q":
                x1, y1 = 2 * cx - last_ctrl[1], 2 * cy - last_ctrl[2]
            else:
                x1, y1 = cx, cy
            out.append("Q" + " ".join([pt(x1, y1), pt(x, y)]))
            last_ctrl = ("Q", x1, y1)
            cx, cy = x, y
        elif C == "A":
            rx, ry, rot, large, sweep, x, y = (num() for _ in range(7))
            if rel:
                x += cx; y += cy
            out.append("A%s %s %d %d %s" % (pt(rx, ry), fmt(rot), int(large), int(sweep), pt(x, y)))
            cx, cy = x, y
            last_ctrl = None
        else:
            raise ValueError("unsupported command " + cmd)
    return " ".join(out)


def circle(cx, cy, r):
    return "M%s A%s 0 1 0 %s A%s 0 1 0 %s Z" % (pt(cx - r, cy), pt(r, r), pt(cx + r, cy), pt(r, r), pt(cx - r, cy))


def rect(x, y, w, h, rx, ry):
    if rx is None and ry is None:
        rx = ry = 0
    rx = ry if rx is None else rx
    ry = rx if ry is None else ry
    rx = min(rx, w / 2)
    ry = min(ry, h / 2)
    if rx == 0:
        return "M%s L%s L%s L%s Z" % (pt(x, y), pt(x + w, y), pt(x + w, y + h), pt(x, y + h))
    a = "A%s 0 0 1 " % pt(rx, ry)
    return " ".join([
        "M" + pt(x + rx, y), "L" + pt(x + w - rx, y), a + pt(x + w, y + ry),
        "L" + pt(x + w, y + h - ry), a + pt(x + w - rx, y + h),
        "L" + pt(x + rx, y + h), a + pt(x, y + h - ry),
        "L" + pt(x, y + ry), a + pt(x + rx, y), "Z",
    ])


def fnum(el, name, default=None):
    v = el.get(name)
    return default if v is None else float(v)


def convert(svg_path):
    root = ET.parse(svg_path).getroot()
    if root.get("viewBox") != "0 0 24 24":
        raise ValueError(svg_path + ": viewBox is not 0 0 24 24")
    parts = []
    for el in root.iter():
        tag = el.tag.split("}")[-1]
        if tag == "path":
            parts.append(path_to_abs(el.get("d")))
        elif tag == "circle":
            parts.append(circle(fnum(el, "cx", 0), fnum(el, "cy", 0), fnum(el, "r")))
        elif tag == "ellipse":
            cx, cy, rx, ry = fnum(el, "cx", 0), fnum(el, "cy", 0), fnum(el, "rx"), fnum(el, "ry")
            parts.append("M%s A%s 0 1 0 %s A%s 0 1 0 %s Z" % (pt(cx - rx, cy), pt(rx, ry), pt(cx + rx, cy), pt(rx, ry), pt(cx - rx, cy)))
        elif tag == "rect":
            parts.append(rect(fnum(el, "x", 0), fnum(el, "y", 0), fnum(el, "width"), fnum(el, "height"),
                              fnum(el, "rx"), fnum(el, "ry")))
        elif tag == "line":
            parts.append("M%s L%s" % (pt(fnum(el, "x1", 0), fnum(el, "y1", 0)), pt(fnum(el, "x2", 0), fnum(el, "y2", 0))))
        elif tag in ("polyline", "polygon"):
            nums = [float(v) for v in re.split(r"[\s,]+", el.get("points").strip())]
            pts = [pt(nums[k], nums[k + 1]) for k in range(0, len(nums), 2)]
            parts.append("M" + pts[0] + "".join(" L" + p for p in pts[1:]) + (" Z" if tag == "polygon" else ""))
        elif tag in ("svg", "title", "desc", "g"):
            continue
        else:
            raise ValueError("%s: unsupported element %s" % (svg_path, tag))
    return " ".join(parts)


def load():
    icons = {}
    for f in sorted(os.listdir(SRC)):
        if not f.endswith(".svg"):
            continue
        name = f[:-4]
        data = convert(os.path.join(SRC, f))
        for old, new in FIXES.get(name, []):
            if old not in data:
                raise ValueError("fix for %s does not apply: %s" % (name, old))
            data = data.replace(old, new)
        icons[name] = data
    return icons


def pascal(name):
    return "".join(p[:1].upper() + p[1:] for p in name.split("-"))


def main():
    icons = load()
    if "--print" in sys.argv:
        for k, v in icons.items():
            print(k + "\t" + v)
        return
    version = open(os.path.join(SRC, "VERSION")).read().strip()
    lines = [
        "// <auto-generated> by tools/icons/lucide-to-cs.py from tools/icons/lucide/*.svg (Lucide %s). Do not edit by hand. </auto-generated>" % version,
        "namespace VPNRouter.Core.Services;",
        "",
        "/// <summary>",
        "/// Path data of the Lucide icons (ISC licence, see NOTICE.md) used by the Android and desktop UI, on the 24 x 24 grid of the",
        "/// source files. Draw them as strokes with round caps and joins, no fill.",
        "/// </summary>",
        "public static partial class UiIcons",
        "{",
    ]
    for k in icons:
        lines.append("    public const string %s = \"%s\";" % (pascal(k), k))
    lines.append("")
    lines.append("    public static readonly IReadOnlyDictionary<string, string> PathData = new Dictionary<string, string>(StringComparer.Ordinal)")
    lines.append("    {")
    for k, v in icons.items():
        lines.append("        [%s] = \"%s\"," % (pascal(k), v))
    lines.append("    };")
    lines.append("}")
    with open(OUT, "w", newline="\n") as fh:
        fh.write("\n".join(lines) + "\n")
    print("wrote %d icons to %s" % (len(icons), os.path.relpath(OUT)))


if __name__ == "__main__":
    main()
