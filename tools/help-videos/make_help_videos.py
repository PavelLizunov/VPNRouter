#!/usr/bin/env python3
"""Builds the short "what to press" videos of the help page (packaging/help-page) from headless UI renders.

Each guide is a list of screens. For every screen the UI MCP server renders the app and reports control bounds;
a cursor glides to the control to press, clicks (a ring), and the video cross-fades to the next screen.

usage: make_help_videos.py --server PATH_TO_vpnrouter-ui-mcp [--out packaging/help-page] [--lang ru en] [--only games]

Needs python3, ImageMagick (magick) and ffmpeg. Re-run after UI changes and commit the .mp4 files.
"""
import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
MCPCALL = os.path.join(HERE, "..", "ui-mcp", "mcpcall.py")
WIDTH, HEIGHT = 520, 900
OUT_WIDTH = 360
FPS = 12

# Labels of the controls to press, per language (the visible text the UI tree reports).
L = {
    "ru": {"apps": "Приложения", "advanced": "Расширенные настройки", "settings": "Настройки",
           "updates": "Обновления", "versions": "Другие версии", "install": "Установить",
           "diagnostics": "Собрать диагностику", "fix": "Исправить", "undo": "Отменить",
           "fixed": "Готово. Проверьте, всё ли работает. Если стало хуже — нажмите «Отменить»."},
    "en": {"apps": "Selected apps", "advanced": "Advanced settings", "settings": "Settings",
           "updates": "Updates", "versions": "Other versions", "install": "Install",
           "diagnostics": "Export diagnostics", "fix": "Fix", "undo": "Undo",
           "fixed": "Done. Check that everything works. If it got worse, press Undo."},
}


def guides(t):
    """Screens of every guide: (surface, scenario, state, label to press or None)."""
    home = ("window-simple", "connected", {})
    adv = ("window-advanced", "connected", {})
    settings = ("window-advanced", "connected", {"SelectedTabIndex": 2})
    updates = ("window-advanced", "connected", {"SelectedTabIndex": 2, "SelectedSettingsIndex": 4})
    return {
        "games": [
            ("window-simple", "connected", {"IsSplitTunnel": False}, t["apps"]),
            ("window-simple", "connected", {"IsSplitTunnel": True}, None),
        ],
        "fix": [
            ("window-simple", "problems", {}, t["fix"]),
            ("window-simple", "connected", {"IsSplitTunnel": True, "ProblemFixStatus": t["fixed"],
                                            "CanUndoProblemFix": True}, None),
        ],
        "rollback": [
            home + (t["advanced"],),
            adv + (t["settings"],),
            settings + (t["updates"],),
            updates + (t["versions"],),
            ("window-advanced", "versions", {"SelectedTabIndex": 2, "SelectedSettingsIndex": 4}, t["install"]),
        ],
        "diagnostics": [
            home + (t["advanced"],),
            adv + (t["settings"],),
            settings + (t["updates"],),
            updates + (t["diagnostics"],),
        ],
    }


def mcp(server, out, *calls):
    args = [sys.executable, "-I", MCPCALL, "--out", out, "--server", server, "--"]
    for tool, payload in calls:
        args += [tool, json.dumps(payload, ensure_ascii=False)]
    res = subprocess.run(args, capture_output=True, text=True, timeout=600)
    if res.returncode != 0:
        sys.exit(f"mcpcall failed: {res.stderr[-2000:]}")
    return res.stdout


BOUNDS = re.compile(r'^\s*(?P<type>\w+) "(?P<text>[^"]*)" \[(?P<x>-?\d+),(?P<y>-?\d+) (?P<w>\d+)x(?P<h>\d+)\](?P<rest>.*)$')
CLICKABLE = ("Button", "RadioButton", "ListBoxItem", "ToggleButton", "TabItem")


def find_target(tree, label):
    """Center of the first enabled clickable control whose text or automation name is the label."""
    for line in tree.splitlines():
        m = BOUNDS.match(line)
        if not m or m["type"] not in CLICKABLE or m["text"] != label or "disabled" in m["rest"]:
            continue
        x, y, w, h = (int(m[k]) for k in ("x", "y", "w", "h"))
        return x + w // 2, y + h // 2, (x, y, w, h)
    return None


def render(server, work, idx, surface, scenario, state, lang):
    payload = {"surface": surface, "scenario": scenario, "language": lang, "theme": "light",
               "width": WIDTH, "height": HEIGHT, "state": state}
    out = os.path.join(work, f"r{idx}")
    os.makedirs(out, exist_ok=True)
    text = mcp(server, out, ("ui_render", payload), ("ui_tree", payload))
    images = [l.split(" ", 1)[1] for l in text.splitlines() if l.startswith("IMAGE ")]
    if not images:
        sys.exit(f"no image for {surface}/{scenario}: {text[-1500:]}")
    return images[0], text


def magick(*args):
    subprocess.run(["magick", *args], check=True)


def cursor_draw(x, y, scale=1.0):
    pts = [(0, 0), (0, 22), (6, 17), (10, 26), (14, 24), (10, 15), (17, 15)]
    poly = " ".join(f"{x + px * scale:.1f},{y + py * scale:.1f}" for px, py in pts)
    return ["-fill", "white", "-stroke", "black", "-strokewidth", "1.5", "-draw", f"polygon {poly}"]


def ease(t):
    return t * t * (3 - 2 * t)


def build_video(name, lang, screens, server, out_dir, work):
    frames = os.path.join(work, f"frames-{name}-{lang}")
    os.makedirs(frames, exist_ok=True)
    rendered = []
    for i, (surface, scenario, state, label) in enumerate(screens):
        png, tree = render(server, work, f"{name}-{lang}-{i}", surface, scenario, state, lang)
        target = find_target(tree, label) if label else None
        if label and not target:
            sys.exit(f"{name}/{lang}: no clickable control '{label}' on screen {i} ({surface}/{scenario})")
        rendered.append((png, target))

    n = 0

    def frame(base, *draw):
        nonlocal n
        magick(base, *draw, os.path.join(frames, f"f{n:05d}.png"))
        n += 1

    cx, cy = WIDTH * 0.62, HEIGHT * 0.92
    for i, (png, target) in enumerate(rendered):
        for _ in range(int(FPS * (1.2 if i == 0 else 0.6))):
            frame(png, *cursor_draw(cx, cy))
        if target:
            tx, ty, (bx, by, bw, bh) = target
            box = ["-fill", "none", "-stroke", "#e00000", "-strokewidth", "4",
                   "-draw", f"roundrectangle {bx - 4},{by - 4} {bx + bw + 4},{by + bh + 4} 12,12"]
            steps = int(FPS * 0.8)
            for k in range(1, steps + 1):
                e = ease(k / steps)
                frame(png, *box, *cursor_draw(cx + (tx - cx) * e, cy + (ty - cy) * e))
            cx, cy = tx, ty
            for k in range(int(FPS * 0.5)):
                r = 8 + k * 5
                ring = ["-fill", "none", "-stroke", "#e00000", "-strokewidth", "3",
                        "-draw", f"circle {tx},{ty} {tx + r},{ty}"]
                frame(png, *box, *ring, *cursor_draw(cx, cy, 0.85 if k < 2 else 1.0))
        if i + 1 < len(rendered):
            nxt = rendered[i + 1][0]
            steps = int(FPS * 0.35)
            for k in range(1, steps + 1):
                blend = os.path.join(work, "blend.png")
                magick(png, nxt, "-compose", "blend", "-define", f"compose:args={int(100 * k / steps)}",
                       "-composite", blend)
                frame(blend, *cursor_draw(cx, cy))
        else:
            for _ in range(int(FPS * 2.5)):
                frame(png, *cursor_draw(cx, cy))

    out = os.path.join(out_dir, f"{name}-{lang}.mp4")
    subprocess.run(["ffmpeg", "-loglevel", "error", "-y", "-framerate", str(FPS),
                    "-i", os.path.join(frames, "f%05d.png"),
                    "-vf", f"scale={OUT_WIDTH}:-2:flags=lanczos", "-c:v", "libx264", "-preset", "slow",
                    "-crf", "30", "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-an", out], check=True)
    print(f"{out} {os.path.getsize(out) // 1024} KB, {n} frames")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--server", required=True)
    ap.add_argument("--out", default=os.path.join(HERE, "..", "..", "packaging", "help-page"))
    ap.add_argument("--lang", nargs="+", default=["ru", "en"])
    ap.add_argument("--only", nargs="*")
    ap.add_argument("--keep", help="keep intermediate frames in this directory")
    a = ap.parse_args()
    for tool in ("magick", "ffmpeg"):
        if not shutil.which(tool):
            sys.exit(f"{tool} not found")
    work = a.keep or tempfile.mkdtemp(prefix="help-videos-")
    os.makedirs(work, exist_ok=True)
    try:
        for lang in a.lang:
            for name, screens in guides(L[lang]).items():
                if a.only and name not in a.only:
                    continue
                build_video(name, lang, screens, a.server, a.out, work)
    finally:
        if not a.keep:
            shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    main()
