# Export performance evaluation

The fixed workload is a complete eight-style 854x480 batch, including locked
build/tests, all-frame inspection, contact sheets, source ZIP, checksums and
consumer decoding. There is no 1080p benchmark or automatic final selection.
Existing delivered films are not benchmark scratch.

`evaluator/` is immutable once its manifest is committed. It contains the original
consumer checker, protected Rust/media/license hashes, mutation tests, timing and
decoded video/PCM comparison. Use a new linked campaign for checker/policy fixes.
The evaluator runs heavy work only on a disposable GitHub Actions runner; its
`--negative-controls` path is a lightweight standard-library checker test.

A task-owned `request.json` selects baseline, search or fresh confirmation for the
render workflow. Comparison control requires an exact 40-character SHA; baseline control and an
omitted comparison candidate resolve to the exact Actions SHA. An explicitly
named candidate must have the same renderer/evaluator implementation as the
Actions head (request-only differences are allowed for fresh confirmation).
Benchmark artifacts are labelled `performance`, not delivered previews. The
request applies only to preview mode; explicit selected-final dispatch remains
available but is not authorized by this optimization. Remove the task request
when the campaign ends. Normal exports continue to use `export-styles.sh`.
The copied checker is loaded with an explicit consumer root by `benchmark.py`;
it is not a separate standalone CLI.

The primary measurement excludes dependency installation, cache transfer, warm-up,
profiling and extra evaluator decoding. These are recorded separately. Each timed
batch still runs its own complete existing consumer verifier. Peak RSS is the
Linux wait4 maximum child RSS, not aggregate concurrent system memory. Search
uses AB/BA pairs; confirmation reverses the starting order and uses fresh warm-up
and measurements. Three pairs are a screening minimum, not a statistical proof.

One part (`warm-export`) has at most 10 candidates and stops after 3 consecutive
non-KEEP outcomes. A candidate needs >=2% and >=1s improvement, above
max(0.5s, 2 * paired MAD), with <=10% peak RSS growth at confirmation. Do not add
per-stage percentages. Cache hits are observations, not candidate speedups.
The evaluator collects raw measurements and quality only. The existing
performance-autoresearch `research_state.py` helper owns paired MAD decisions,
resource confirmation, append-only failure counts and the 10/3 stopping budget;
there is no second statistical implementation in this runner. A CI success means
measurement collection passed, not that a candidate is automatically accepted.
Failed collection saves failure.json/logs and never creates a passed measurement.
