# H-53: keep the refactor equivalence harness in the repository

## Why

H-36 to H-51 were verified by a line-multiset comparison and by seeded corpus runs on `windows-worker` (hash of every
result or exception, old commit against new commit). The scripts and corpus tests lived only in a session scratch
directory, so the next refactor would have to rebuild them. The method is the difference between "the tests still
pass" and "the outputs are the same on tens of thousands of inputs the tests never had".

## What

- `tools/refactor-equivalence/`: `README.md` (method, what each corpus covers, how to run, known noise), `run-equivalence.ps1`
  (worker runner: exact commit, instrumentation of `Inject` and `Generate` in the worker copy only, corpora run twice,
  checkout removed), `compare-records.py` (per-record comparison with GUID, id, timestamp and path normalisation and a
  determinism check), `line-multiset.py`, and the corpus tests `EqCorpusTests.cs` and `EqCorpusVmTests.cs`.
- `.gitignore`: `tools/*` is ignored by default; the new directory is re-included like `tools/VpnRouterTestMcp/`.
- `tools/AGENTS.md`: one line pointing at the harness.

The `.cs` files are not part of any project: they are copied into a task-owned worker checkout by the runner.

## Verification

Both scripts were run against the real worker data of the H-48 check (`compare-records.py` reproduced the recorded
result; `line-multiset.py` reproduced the H-48 route/DNS/Inject comparison). No product code changed.

## Outcome

Pending CI.
