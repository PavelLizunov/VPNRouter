#!/usr/bin/env python3
"""List the symbol characters (not letters, not ordinary punctuation) in the Android UI sources and in the Core strings.

Used to find text symbols and emoji that act as icons (U5). Comment lines are skipped; typographic punctuation such as
quotes, dashes and the ellipsis is not reported.

Usage: python3 tools/icons/scan-symbols.py [repo_root]          summary per file and per character
       python3 tools/icons/scan-symbols.py [repo_root] --list   every line with its symbols
"""
import collections
import os
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
                found = [c for c in line if is_symbol(c)]
                if found:
                    rows.append((os.path.join(d, f), no, found, text))
    return rows


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    root = args[0] if args else os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..")
    rows = scan(root)
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
