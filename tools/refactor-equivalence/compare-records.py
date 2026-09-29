#!/usr/bin/env python3
"""Compare the record files two run-equivalence.ps1 runs left on a worker.

usage: compare-records.py <user@host> <name-before> <name-after> [remote-root]

Prints, per record type, how many hashes exist only before or only after, and whether a rerun of the
"before" commit reproduces its own hashes (a corpus that is not deterministic proves nothing).
Records that embed random ids or timestamps are compared through the .full dumps with those parts normalised.
"""
import collections, hashlib, os, re, subprocess, sys, tempfile

host, a, b = sys.argv[1:4]
root = sys.argv[4] if len(sys.argv) > 4 else 'C:/android-build'
tmp = tempfile.mkdtemp(prefix='eqcmp-')

def fetch(f):
    dst = os.path.join(tmp, f)
    subprocess.run(['scp', '-q', '-o', 'BatchMode=yes', f'{host}:{root}/{f}', dst], capture_output=True)
    if not os.path.exists(dst):
        return None
    return open(dst, encoding='utf-8-sig', errors='ignore').read().splitlines()

GUID = re.compile(r'[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}')
HEX32 = re.compile(r'\b[0-9a-f]{32}\b')
TIME = re.compile(r'Time:\s+[\d-]+ [\d:]+')

def normalised_full(name, kind):
    lines = fetch(f'{kind}-{name}.txt.full') or []
    out = collections.Counter()
    for l in lines:
        k, _, t = l.partition('\t')
        t = HEX32.sub('<ID>', TIME.sub('Time: <T>', GUID.sub('<GUID>', t)))
        out[(k.rsplit(' ', 1)[0], hashlib.sha256(t.encode()).hexdigest()[:12])] += 1
    return out

def report(label, x, y, x_rerun=None):
    cx, cy = collections.Counter(x), collections.Counter(y)
    det = ''
    if x_rerun is not None:
        det = ' | rerun ' + ('identical' if collections.Counter(x_rerun) == cx else 'DIFFERS (not deterministic)')
    print(f'{label}: {sum(cx.values())} vs {sum(cy.values())} | only-before {sum((cx-cy).values())} '
          f'only-after {sum((cy-cx).values())}{det}')

A, B = fetch(f'corpus-{a}.txt') or [], fetch(f'corpus-{b}.txt') or []
A2 = fetch(f'corpus2-{a}.txt') or []
for p in sorted({l.split()[0] for l in A + B}):
    if p in ('V', 'H'):
        continue  # dumps below
    report(p, [l for l in A if l.split()[0] == p], [l for l in B if l.split()[0] == p],
           [l for l in A2 if l.split()[0] == p] or None)
for kind in ('corpus',):
    x, y = normalised_full(a, kind), normalised_full(b, kind)
    if x or y:
        print(f'dumps (V/H, normalised): {sum(x.values())} vs {sum(y.values())} | only-before {sum((x-y).values())} only-after {sum((y-x).values())}')
S, T = fetch(f'rec-{a}.txt'), fetch(f'rec-{b}.txt')
if S and T:
    fx, fy = normalised_full(a, 'rec'), normalised_full(b, 'rec')
    print(f'suite Inject/Generate dumps (normalised): {sum(fx.values())} vs {sum(fy.values())} | '
          f'only-before {sum((fx-fy).values())} only-after {sum((fy-fx).values())}')
    ex = lambda L: [l for l in L if l.split()[0] in ('IE', 'GE')]
    report('suite exceptions (IE/GE)', ex(S), ex(T))
