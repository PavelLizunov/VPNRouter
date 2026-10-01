#!/usr/bin/env python3
"""List the symbol characters (not letters, not ordinary punctuation) in the Android UI sources and in the Core strings.

Used to find text symbols and emoji that act as icons (U5). Comment lines are skipped; typographic punctuation such as
quotes, dashes and the ellipsis is not reported. \\uXXXX escapes in string literals are decoded first.

Usage: python3 tools/icons/scan-symbols.py [repo_root]          summary per file and per character
       python3 tools/icons/scan-symbols.py [repo_root] --list   every line with its symbols
       python3 tools/icons/scan-symbols.py [repo_root] --keys   string keys with symbols and where they are shown
                                                                 (android = files in VPNRouter.Android that use the key
                                                                 or its Localization wrapper, desktop = VPNRouter.App)
"""
import collections
import os
import re
import sys
import unicodedata

TEXT_OK = set("«»—–…’‘“”№·°×≈≥≤")
DIRS = ["VPNRouter.Android", "VPNRouter.Android/Controls", "VPNRouter.Core/Localization"]


def is_symbol(c):
    if ord(c) < 128 or c in TEXT_OK:
        return False
    cat = unicodedata.category(c)
    return not (cat.startswith("L") or cat.startswith("M") or cat == "Zs")


def scan(root):
    rows = []
    for d in DIRS:
        full = os.path.join(root, d)
        for f in sorted(os.listdir(full)):
            if not f.endswith(".cs"):
                continue
            path = os.path.join(full, f)
            for no, line in enumerate(open(path, encoding="utf-8"), 1):
                text = line.strip()
                if text.startswith(("//", "*")):
                    continue
                decoded = re.sub(r"\\u([0-9a-fA-F]{4})", lambda m: chr(int(m.group(1), 16)), line)
                found = [c for c in decoded if is_symbol(c)]
                if found:
                    rows.append((os.path.join(d, f), no, found, text))
    return rows


def key_usage(root, rows):
    seen = set()
    for f, no, found, _ in rows:
        if "Localization/" not in f:
            continue
        lines = open(os.path.join(root, f), encoding="utf-8").read().splitlines()
        key = None
        for i in range(no - 1, -1, -1):
            m = re.search(r"public static string (\w+)", lines[i])
            if m:
                key = m.group(1)
                break
        if key is None or key in seen:
            continue
        seen.add(key)
        android_dir = os.path.join(root, "VPNRouter.Android")
        wrapper = open(os.path.join(android_dir, "Localization.cs"), encoding="utf-8").read()
        names = set(re.findall(r"public static string (\w+) => [^;]*Strings\.%s\b" % key, wrapper))
        android = set()
        for name in names | {key}:
            for fn in os.listdir(android_dir):
                if fn.endswith(".cs") and fn != "Localization.cs":
                    text = open(os.path.join(android_dir, fn), encoding="utf-8").read()
                    if re.search(r"(Localization|Strings)\.%s\b" % name, text):
                        android.add(fn)
        desktop = set()
        for dirpath, _, files in os.walk(os.path.join(root, "VPNRouter.App")):
            for fn in files:
                if fn.endswith((".cs", ".axaml")):
                    if re.search(r"\b%s\b" % key, open(os.path.join(dirpath, fn), encoding="utf-8").read()):
                        desktop.add(fn)
        print("%-34s android=%d desktop=%d" % (key, len(android), len(desktop)))


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    root = args[0] if args else os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..")
    rows = scan(root)
    if "--keys" in sys.argv:
        key_usage(root, rows)
        return
    if "--list" in sys.argv:
        for f, no, found, text in rows:
            print("%s:%d [%s] %s" % (f, no, "".join(sorted(set(found))), text[:160]))
        return
    per_file = collections.Counter()
    per_char = collections.Counter()
    for f, _, found, _ in rows:
        per_file[f] += len(found)
        per_char.update(found)
    print("total %d symbols on %d lines" % (sum(per_file.values()), len(rows)))
    for f, n in per_file.most_common():
        print("%4d %s" % (n, f))
    for c, n in per_char.most_common():
        print("%4d %s U+%04X %s" % (n, c, ord(c), unicodedata.name(c, "?")))


if __name__ == "__main__":
    main()
