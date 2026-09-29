#!/usr/bin/env python3
"""Show which trimmed lines a change removed or added, ignoring order and indentation.

usage: line-multiset.py <git-rev-before> <file> [<file> ...]

A pure "extract into methods" refactor removes nothing and adds only method headers, braces, parameter
lines, calls and returns. Anything else in the output is a statement that changed and needs a review.
"""
import collections, subprocess, sys

rev, files = sys.argv[1], sys.argv[2:]
for f in files:
    old = subprocess.check_output(['git', 'show', f'{rev}:{f}']).decode('utf-8').split('\n')
    new = open(f, encoding='utf-8').read().split('\n')
    co = collections.Counter(l.strip() for l in old if l.strip())
    cn = collections.Counter(l.strip() for l in new if l.strip())
    print(f'== {f}')
    print('removed:', dict(co - cn))
    print('added:  ', dict(cn - co))
