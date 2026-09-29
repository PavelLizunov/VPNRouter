# Refactor equivalence harness

Proves that a "same behaviour" change (splitting a long method, removing a duplicate) did not change what the code
computes. Used for H-36 to H-51. It does not replace the test suite; it covers inputs that no test has.

Nothing here is compiled into the product or the normal test run: the `.cs` files are copied into a task-owned worker
checkout by `run-equivalence.ps1` and never sit inside a project directory in the repository.

## Two checks

1. **Line multiset** (`line-multiset.py <git-rev-before> <file>...`): after a pure extraction nothing is removed and only
   method headers, braces, parameter lines, calls and returns are added. Every other line in the output is a statement
   that changed; review it by hand.
2. **Corpus run** (`run-equivalence.ps1`, then `compare-records.py`): seeded random inputs go through the old and the new
   commit; the output (or the exception type and message) of every input is hashed. The hash lists must be identical,
   and a second run of the *old* commit must reproduce its own hashes, otherwise the corpus proves nothing.

## What the corpora cover

| Record | Source | Inputs |
|---|---|---|
| `S` | `EqCorpusTests.StripCorpus` | 20,000 random sing-box configs through `CustomConfigInjector.StripUnsupportedFeatures` (reflection) |
| `O` | `OutboundsCorpus` | 6,000 random server sets through `ConfigGenerator.BuildOutbounds` (reflection) |
| `C`, `L`, `M` | `GenerateCorpus` | 6,000 full `ConfigGenerator.Generate` calls; `L` and `M` are `LeakProtection.ValidateConfig` on the result as generated and after one random mutation |
| `E` | `InjectCorpus` | 8,000 random configs through `CustomConfigInjector.Inject` |
| `H` | `HealthCorpus` | 60 seeded data directories through `HealthCheck.RunAll` |
| `V` | `EqCorpusVmTests.VmCorpus` | 40 seeded settings loaded into a real `MainWindowViewModel`, six properties changed, `SaveSettings` called (every public property and the settings YAML are dumped) |
| `I`, `G`, `IE`, `GE` | instrumentation of the normal suite | every `Inject` / `Generate` result (or exception) produced by the existing tests |

Add a corpus for the function you are about to change *before* changing it, at the parent commit, and keep it
deterministic (fixed `Random` seed, a fixed data directory, no clock or GUID in the hashed text).

## Running it

Follow `docs/test-workers.md` (load preflight, exact SHA, task-owned paths, one job at a time). On the worker:

1. Copy `EqCorpusTests.cs` and `EqCorpusVmTests.cs` from this directory to the worker root (`C:\android-build`).
2. `powershell -File run-equivalence.ps1 -Sha <parent sha> -Name before` and the same with `-Sha <new sha> -Name after`
   (`-CorpusOnly` skips the full-suite instrumentation and finishes in about three minutes after the build).
   The script fetches the exact commit, instruments `CustomConfigInjector.Inject` and `ConfigGenerator.Generate`
   in the worker copy only, runs the corpora twice and deletes its checkout.
3. `python3 compare-records.py <user@worker> before after`.

Known noise, already normalised by `compare-records.py`: random GUIDs and 32-hex ids in test data, the `Time:` line of the
health report, absolute paths. Any other difference is a real difference: find it before merging.

## Pitfalls seen

- The raw hash lists of two runs of the same commit differ in a few `Generate` records because some tests embed random
  UUIDs; compare the normalised dumps.
- `xUnit` parallelism makes tests that touch `AppPaths` or feature switches non-deterministic in the suite phase; the
  corpus tests run alone for that reason (`--filter EqCorpus`) and reset every static they change.
- Corpus tests must not toggle anything with side effects (services, autostart, DNS lockdown, Zapret, Telegram proxy).
