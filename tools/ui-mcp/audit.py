#!/usr/bin/env python3
"""Renders every surface of the desktop UI in every size, theme, language and scenario through the UI MCP server and tabulates the lint.

usage: audit.py --out DIR --server CMD [ARG ...] [--widths 360,520,720,1000] [--themes light,dark] [--languages en,ru]
                [--scenarios default] [--surfaces a,b,c] [--height 900] [--clicks N]

Writes DIR/img/<cell>.png for every cell, DIR/cells.tsv (one row per cell: counts per severity) and DIR/findings.txt (every warn grouped by rule and
element, with the cells it appears in). Exit code 1 if any cell has a warn. --clicks N also runs a no-delay monkey sweep of N clicks per surface.
"""
import argparse
import base64
import collections
import itertools
import json
import os
import re
import subprocess
import sys


class Server:
    def __init__(self, cmd):
        self.p = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, bufsize=1)
        self.n = 0
        self.rpc("initialize", {"protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": {"name": "audit", "version": "0"}})
        self.p.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
        self.p.stdin.flush()

    def rpc(self, method, params=None):
        self.n += 1
        ident = self.n
        self.p.stdin.write(json.dumps({"jsonrpc": "2.0", "id": ident, "method": method, "params": params or {}}) + "\n")
        self.p.stdin.flush()
        while True:
            line = self.p.stdout.readline()
            if not line:
                sys.exit("server closed the pipe: " + self.p.stderr.read()[:2000])
            try:
                msg = json.loads(line)
            except ValueError:
                continue
            if msg.get("id") == ident:
                return msg

    def call(self, tool, args):
        reply = self.rpc("tools/call", {"name": tool, "arguments": args})
        if "error" in reply:
            raise RuntimeError(reply["error"])
        res = reply["result"]
        text = "\n".join(c["text"] for c in res["content"] if c["type"] == "text")
        images = [base64.b64decode(c["data"]) for c in res["content"] if c["type"] == "image"]
        return bool(res.get("isError")), text, images

    def close(self):
        try:
            self.p.stdin.close()
        except Exception:
            pass


FINDING = re.compile(r"^\[(warn|info)\] (\S+): (.*?) - (.*)$")


def main():
    argv = sys.argv[1:]
    if "--server" not in argv:
        sys.exit(__doc__)
    k = argv.index("--server")
    head, cmd = argv[:k], argv[k + 1:]
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--widths", default="360,520,720,1000")
    ap.add_argument("--themes", default="light,dark")
    ap.add_argument("--languages", default="en,ru")
    ap.add_argument("--scenarios", default="default")
    ap.add_argument("--surfaces", default="")
    ap.add_argument("--height", type=int, default=900)
    ap.add_argument("--clicks", type=int, default=0)
    ap.add_argument("--tabs", type=int, default=0, help="also visit the inner tabs: 1 = first level, 2 = two levels of tab-like controls")
    opts = ap.parse_args(head)

    os.makedirs(os.path.join(opts.out, "img"), exist_ok=True)
    srv = Server(cmd)
    _, catalog, _ = srv.call("ui_catalog", {})
    surfaces = [m.group(1) for m in re.finditer(r"^\s{2}(\S+) \((?:page|window)\)", catalog, re.M)]
    if opts.surfaces:
        surfaces = [s for s in opts.surfaces.split(",") if s in surfaces]

    TAB = re.compile(r'^\s*(?:ListBoxItem|TabItem) "([^"]+)"')

    def tab_labels(surface, steps, width):
        """Labels of the tab-like controls visible after `steps`, found through ui_tree."""
        args = {"surface": surface, "width": width, "height": opts.height, "max_lines": 1500}
        if steps:
            args["steps"] = steps
        _, tree, _ = srv.call("ui_tree", args)
        labels = []
        for line in tree.splitlines():
            m = TAB.match(line)
            if m and m.group(1) not in labels and not m.group(1).isdigit():
                labels.append(m.group(1))
        return labels

    def variants_for(surface):
        """Step lists that reach each inner tab (depth set by --tabs)."""
        found = [[]]
        if not opts.tabs:
            return found
        first = tab_labels(surface, [], 520)
        for a in first:
            found.append([a])
            if opts.tabs > 1:
                for b in tab_labels(surface, [a], 520):
                    if b not in first and b != a:
                        found.append([a, b])
        return found

    cells = []
    findings = collections.defaultdict(lambda: collections.defaultdict(set))
    variants = {surface: variants_for(surface) for surface in surfaces}
    combos = []
    for surface in surfaces:
        for steps in variants[surface]:
            for scenario, theme, lang, width in itertools.product(opts.scenarios.split(","), opts.themes.split(","), opts.languages.split(","), [int(w) for w in opts.widths.split(",")]):
                combos.append((surface, steps, scenario, theme, lang, width))
    for index, (surface, steps, scenario, theme, lang, width) in enumerate(combos, 1):
        tag = ("+" + "+".join(re.sub(r"[^A-Za-z0-9]+", "", x) for x in steps)) if steps else ""
        label = f"{surface}{tag}-{scenario}-{theme}-{lang}-{width}"
        try:
            call_args = {"surface": surface, "scenario": scenario, "theme": theme, "language": lang, "width": width, "height": opts.height}
            if steps:
                call_args["steps"] = steps
            err, text, images = srv.call("ui_render", call_args)
        except Exception as ex:  # a crash of the server or the tool is itself a finding
            cells.append((label, -1, -1, f"EXCEPTION {ex}"))
            print(f"[{index}/{len(combos)}] {label}: EXCEPTION {ex}", flush=True)
            continue
        if err:
            cells.append((label, -1, -1, text.strip().splitlines()[0] if text else "error"))
            print(f"[{index}/{len(combos)}] {label}: ERROR {text[:200]}", flush=True)
            continue
        warns = infos = 0
        for line in text.splitlines():
            m = FINDING.match(line.strip())
            if not m:
                continue
            sev, rule, element, message = m.groups()
            message = re.sub(r"\(\+\d+ more like it\)", "", message).strip()
            if sev == "warn":
                warns += 1
                findings[(rule, element)][message].add(f"{theme}/{lang}/{width}/{surface}{tag}")
            else:
                infos += 1
        if images:
            with open(os.path.join(opts.out, "img", label + ".png"), "wb") as f:
                f.write(images[0])
        cells.append((label, warns, infos, ""))
        print(f"[{index}/{len(combos)}] {label}: warn={warns} info={infos}", flush=True)

    sweep_failures = 0
    if opts.clicks:
        for surface in surfaces:
            err, text, _ = srv.call("ui_sweep", {"surface": surface, "speed_ms": 0, "mode": "monkey", "max_steps": opts.clicks, "render_every": 7, "seed": 7, "return_image": False, "height": 1200})
            ok = "failures: 0" in text
            sweep_failures += 0 if ok else 1
            print(f"sweep {surface}: {'ok' if ok else 'FAILURE'}\n" + ("" if ok else text[:1500]), flush=True)
    srv.close()

    with open(os.path.join(opts.out, "cells.tsv"), "w", encoding="utf-8") as f:
        f.write("cell\twarn\tinfo\tnote\n")
        for label, w, i, note in cells:
            f.write(f"{label}\t{w}\t{i}\t{note}\n")
    with open(os.path.join(opts.out, "findings.txt"), "w", encoding="utf-8") as f:
        for (rule, element), messages in sorted(findings.items(), key=lambda kv: (kv[0][0], kv[0][1])):
            where = sorted({c for cs in messages.values() for c in cs})
            f.write(f"{rule}: {element}\n")
            for message, cs in sorted(messages.items()):
                f.write(f"    {message}   [{len(cs)} cell(s)]\n")
            f.write(f"    in: {', '.join(where[:8])}{' ...' if len(where) > 8 else ''}\n")
    total_warn = sum(w for _, w, _, _ in cells if w > 0)
    broken = sum(1 for _, w, _, _ in cells if w < 0)
    print(f"\ncells={len(cells)} cells_with_warn={sum(1 for _, w, _, _ in cells if w > 0)} warns={total_warn} broken={broken} sweep_failures={sweep_failures}")
    sys.exit(1 if total_warn or broken or sweep_failures else 0)


if __name__ == "__main__":
    main()
