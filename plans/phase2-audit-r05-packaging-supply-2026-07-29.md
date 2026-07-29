# Phase 2 — R05 — Packaging / supply-chain reproducibility

**Owner**: Qwen Code session (code-only)
**Branch**: `codex/qwen-audit-r05-packaging-supply-2026-07-29`
**Base**: `codex/qwen-audit-p08-appimagetool-pin-v2-2026-07-29` (MANDATED: SUP-2 shares `.github/workflows/build-linux.yml`; P08-v2 modified that file +16/-2 for the SUP-1 appimagetool pin. Verified via `git diff --stat origin/main...codex/qwen-audit-p08-appimagetool-pin-v2-2026-07-29`.)
**Roadmap ref**: `plans/qwen-remaining-remediation-index-2026-07-29.md` (R05); prompt pool P08
**IDs**: PKG-1, SUP-2, SUP-4
**Effort**: ~2 h
**Risk**: MEDIUM (PKG-1 is a guaranteed build-break when the wgturn branch is taken; SUP-2/SUP-4 are integrity/reproducibility gaps)
**Blast radius**: `build-mac.sh`, `.github/workflows/build-linux.yml` · ~+40 LOC · runtime: macOS wgturn build branch + Linux sing-box/libcronet bundling
**Rollback**: `git revert <commit>` / delete branch

---

## 1. Final P00 verdict / severity / confidence / corrected scope

| ID | Orig | Verdict | Final | Conf |
|---|---|---|---|---|
| PKG-1 | P1 | CONFIRMED | P2 | High (bug) / Med (reach) |
| SUP-2 | P1 | CONFIRMED | P2 | High |
| SUP-4 | P2 | CONFIRMED | P2 | High |

Corrected scope:

- **PKG-1**: real guaranteed-build-break but LATENT — the wgturn cache is absent
  from git and CI sets no `WGTURN_CORE_DIR`, so the branch fires only if `gh repo
  clone PavelLizunov/wgturn-core` succeeds on the runner, which current public CI
  does not exercise. P2, not P1. Shares the reachability gate with SUP-4.
- **SUP-2**: downgraded P1 → P2 — the sing-box version tag IS pinned (stable URL),
  so this is a missing-digest integrity gap, not a floating-ref exposure (contrast
  SUP-1). Attack requires GitHub release-asset compromise / CDN MITM, largely
  mitigated by the pinned tag + TLS.
- **SUP-4**: reproducibility/integrity gap, currently latent (same gate as PKG-1).

## 2. Verified current root cause (commit `b39a28c3`)

### PKG-1 — unbound `ARCH` aborts the macOS wgturn build

`build-mac.sh` (`set -euo pipefail` at `:17`):

- `:90` `echo "    wgturn-cli: building darwin-${ARCH} (sha $WGTURN_SHA)..."`
- `:93` `GOOS=darwin GOARCH="${ARCH}" CGO_ENABLED=0 go build ...`
- `ARCH` is referenced at `:90/:93` but NEVER assigned anywhere in the script.
- Gate `:87` `[ -n "$WGTURN_CORE" ] && [ -d "$WGTURN_CORE/cmd/wgturn-cli" ] && command -v go`.
- `.github/workflows/build-mac.yml` sets neither `ARCH` nor `WGTURN_CORE_DIR`
  (calls `./build-mac.sh "<ver>"`); go is installed `:41`; `tools/wgturn-cli-cache/`
  is not in git.
- Under `set -u`, the moment the gate passes, `${ARCH}` aborts the whole macOS
  build.

### SUP-2 — sing-box/libcronet archive bundled without digest

`.github/workflows/build-linux.yml` (verified `:106-110`):

```yaml
SINGBOX_VER="1.13.14"
curl -sSL -o /tmp/singbox.tar.gz \
  "https://github.com/SagerNet/sing-box/releases/download/v${SINGBOX_VER}/sing-box-${SINGBOX_VER}-linux-amd64.tar.gz"
tar -xzf /tmp/singbox.tar.gz -C /tmp
cp "/tmp/sing-box-${SINGBOX_VER}-linux-amd64/libcronet.so" publish/linux-x64/libcronet.so
```

The archive is downloaded and `libcronet.so` bundled into every Linux artifact
(distribution comment `:103-105`: deb/AppImage/tar.gz all `cp -R
publish/linux-x64/.`) with NO digest verification of the archive or `libcronet.so`.
`:283` `sha256sum` only emits sidecars for FINAL artifacts, not input verification.

### SUP-4 — wgturn-core cloned from floating HEAD

`build-mac.sh`:

- `:82` `gh repo clone PavelLizunov/wgturn-core "$REPO_DIR/tools/wgturn-cli-cache/wgturn-core"` — default-branch HEAD, no pin (verified `:79-84`).
- `:88` `WGTURN_SHA=$(cd "$WGTURN_CORE" && git rev-parse --short=12 HEAD ...)` — captured SHA is mere `-ldflags` metadata (`:94`), never asserted, checked out, or verified.
- Same gate `:87` as PKG-1.

## 3. Why

PKG-1 guarantees a macOS build failure the first time anyone enables the wgturn
branch (a latent landmine). SUP-2 bundles an unverified third-party binary
(`libcronet.so`) into every Linux artifact. SUP-4 builds wgturn-cli from whatever
HEAD happens to be at clone time (non-reproducible, integrity gap). All three are
supply-chain hardening with existing fail-closed patterns to reuse.

## 4. What

1. **PKG-1**: derive `ARCH` once from the build target before the wgturn branch
   (e.g. `uname -m` → `arm64`/`amd64`, mapping `x86_64`→`amd64`,
   `arm64`/`aarch64`→`arm64`). Honor an explicit cross-build target if one is
   provided rather than guessing from the host.
2. **SUP-2**: pin AND verify the sing-box release archive SHA256 before
   extraction; fail closed on mismatch. Reuse the fail-closed checksum idiom
   introduced by P08-v2 for appimagetool in the same file.
3. **SUP-4**: pin wgturn-core to a commit/tag and assert it (checkout + verify
   HEAD matches the pin) before bundling; fail closed on mismatch.

```diff
+ # Derive Go GOARCH from the build target (not host guess on cross-build).
+ case "${WGTURN_GOARCH:-$(uname -m)}" in
+   x86_64|amd64)  ARCH="amd64" ;;
+   arm64|aarch64) ARCH="arm64" ;;
+   *) echo "unsupported arch: $(uname -m)" >&2; exit 1 ;;
+ esac
```

```diff
  SINGBOX_VER="1.13.14"
+ SINGBOX_SHA256="<pinned sha256 of sing-box-1.13.14-linux-amd64.tar.gz>"
  curl -sSL -o /tmp/singbox.tar.gz ".../v${SINGBOX_VER}/sing-box-${SINGBOX_VER}-linux-amd64.tar.gz"
+ echo "${SINGBOX_SHA256}  /tmp/singbox.tar.gz" | sha256sum -c -
  tar -xzf /tmp/singbox.tar.gz -C /tmp
```

## 5. How (ordered minimal steps)

1. Read the P08-v2 appimagetool pin block in `build-linux.yml` first — reuse its
   exact fail-closed checksum idiom and comment style for SUP-2.
2. SUP-2: capture the authoritative SHA256 of `sing-box-1.13.14-linux-amd64.tar.gz`
   from the upstream release; document the capture source in the commit message;
   add the `sha256sum -c` gate before `tar -xzf`.
3. PKG-1: add the `ARCH` derivation before the gate at `:87`; ensure both `:90`
   and `:93` consume it.
4. SUP-4: define a `WGTURN_CORE_REF` pin (commit/tag); after clone, `git -C
   "$WGTURN_CORE" checkout "$WGTURN_CORE_REF"` and assert `git rev-parse HEAD`
   matches; fail closed on mismatch. Keep the `-ldflags` version metadata.
5. Document the update procedure for each pin in the commit message.
6. Static review of shell/YAML syntax (NO local execution).

### Tests written

These are static/grep-style checks (no local shell execution; validated by
inspection and remote CI):

- Grep test: `build-mac.sh` assigns `ARCH` before any `${ARCH}` reference.
- Grep test: `build-linux.yml` contains a `sha256sum -c` gate on the sing-box
  archive before `tar -xzf`.
- Grep test: `build-mac.sh` checks out / asserts a pinned wgturn-core ref before
  `go build`.
- ARCH mapping cases (arm64/x64) validated by inspection of the `case` table.
- Existing build paths WITHOUT wgturn remain unchanged (assert the gate still
  guards the branch).

### Verification approach

Static shell/YAML inspection + remote Linux CI (SUP-2) and remote Mac CI (PKG-1/
SUP-4) after the orchestrator pushes. No local `wget`/`curl`/`go build`/actionlint.

## 6. Affected callers / consumers + invariants

- PKG-1/SUP-4 consumers: `build-mac.sh` wgturn branch + `build-mac.yml`.
  Invariant: the non-wgturn macOS build path is byte-identical; the gate `:87`
  still guards the branch.
- SUP-2 consumers: `build-linux.yml` + every Linux artifact (`cp -R
  publish/linux-x64/.`). Invariant: `SINGBOX_VER` stays `1.13.14`; the
  `with_awg` sanity check (`:115-116`) still runs; final-artifact sidecar
  emission (`:283`) unchanged.
- P08-v2 appimagetool pin block must remain intact (R05 builds on it).

## 7. Exact expected file list

- `build-mac.sh` (PKG-1 ARCH derivation; SUP-4 wgturn-core pin/assert)
- `.github/workflows/build-linux.yml` (SUP-2 sing-box archive digest gate)

## 8. Non-goals

- Do NOT change `SINGBOX_VER` or the sing-box feature sanity checks.
- Do NOT add a new dependency-manifest abstraction; reuse the P08-v2 idiom.
- Do NOT download or execute any unverified binary during the change (code-only).
- Do NOT touch the appimagetool pin (that is SUP-1 / P08-v2, already done — R05
  builds on it).
- Do NOT create a tag/release.

## 9. Security / concurrency / data-loss / platform review

- **Security**: SUP-2/SUP-4 are supply-chain integrity fixes (fail-closed digest/
  pin). The digest for SUP-2 MUST be captured from the authoritative upstream
  release and recorded in the commit message (primary-source provenance).
- **Platform**: macOS `uname -m` mapping must cover both Apple Silicon (`arm64`)
  and Intel (`x86_64`). Linux sing-box archive is `linux-amd64` (matches the
  `linux-x64` publish dir).
- **Concurrency**: none (build scripts).
- **Data-loss**: none.

## 10. Dependencies / overlaps

- **Base is P08-v2** (SUP-1 appimagetool pin) because both edit `build-linux.yml`.
  Add the SUP-2 digest step NEAR but NOT ON TOP of the appimagetool pin block.
- PKG-1 and SUP-4 share `build-mac.sh` and the same reachability gate; keep them
  in one commit/PR.
- No other R-package touches these files. R10 (SUP-3) touches `sign-windows.yml`
  (different file) — independent.

## 11. Remote-only verification gates

- [ ] Gate 1 — Build clean (remote CI): Linux CI (SUP-2) and Mac CI (PKG-1/SUP-4) pass after push.
- [ ] Gate 2 — Tests green (remote CI): grep/static checks pass; existing build paths without wgturn unchanged.
- [ ] Gate 3 — Docs: brief Outcome filled; commit message documents each pin's capture source + update procedure.
- [ ] Gate 4 — Self-review: static shell/YAML review; confirm no mutable `continuous` executable URL introduced.
- [ ] Gate 5 — MCP verify: N/A (build infra only).
- [ ] Gate 6 — Characterization diff: N/A.

## 12. Outcome

**Status**: PASS (implementable / remote-only scope) — with wgturn
runtime-branch caveat (see below).

**Branch base**: `63a4856b` (P08-v2 / PR #61).
**PR**: #75 (draft, intentionally stacked on P08-v2 PR #61 / base branch
`codex/qwen-audit-p08-appimagetool-pin-v2-2026-07-29`).

**Commits on branch**:
- `d767586d` — docs: add R05 brief.
- `0d5ee149` — provisional implementation + remote-only digest capture.
- `c0febe02` — final fail-closed code (ci: verify sing-box archive input).

**Pushed**: `d767586d`, `0d5ee149`, and `c0febe02` pushed to `origin` branch.

**Files changed** (cumulative diff vs base `63a4856b`): 3 files, +314/-0;
0 new test files.
- brief (`plans/phase2-audit-r05-packaging-supply-2026-07-29.md`) +298
- `build-mac.sh` +14
- `.github/workflows/build-linux.yml` +2

**Remote CI runs**:
- `30457261962` for `d767586d`: success (test + characterization-windows).
- `30457593302` for `0d5ee149` (build-linux capture): success;
  `upload_to_release=false`, release upload skipped; captured sing-box SHA256
  `f48703461a15476951ac4967cdad339d986f4b8096b4eb3ff0829a500502d697`.
- `30457608906` for `0d5ee149`: success (test + characterization-windows).
- `30458580034` for `c0febe02`: success (test + characterization-windows).
- `30458582745` for `c0febe02` (build-linux): success; digest gate logged
  `/tmp/singbox.tar.gz: OK`; AppImage/deb/tar built; Upload to GitHub Release
  skipped (`upload_to_release=false`).
- `30458585894` for `c0febe02` (build-mac): success; package build + smoke
  success; Upload to GitHub Release and Homebrew trigger skipped
  (`upload_to_release=false` / non-stable).

**wgturn runtime-branch caveat**: the macOS log shows the cross-repo wgturn
clone was inaccessible and the existing gated branch printed
`wgturn-cli: SKIPPED`. PKG-1 (ARCH derivation) and SUP-4 (wgturn-core
pin/assert) were therefore statically reviewed by Qwen but NOT dynamically
exercised on a runner. This is a verification limitation of the
private/inaccessible wgturn branch, not a code defect.

**Self-review**: Qwen final static Ponytail review PASS — no unnecessary lines,
no shell bug. No local build/test/script/binary/app/installer/VM/MCP/
network/download was run.

**Gate results:**
- [x] Gate 1: PASS — remote Linux CI (`30458582745`) and Mac CI
  (`30458585894`) success after final commit `c0febe02`
- [x] Gate 2: PASS — remote test + characterization-windows (`30458580034`)
  success; existing build paths without wgturn unchanged
- [x] Gate 3: PASS — brief Outcome filled; pins' capture source + update
  procedure documented in commit message
- [x] Gate 4: PASS — static shell/YAML review; no mutable `continuous`
  executable URL introduced
- [-] Gate 5: N/A — build infra only
- [-] Gate 6: N/A

**Surprises encountered**:
- Upstream sing-box release metadata/body/assets and local git history carried
  no sing-box archive digest, so the SHA256 was captured on a GitHub-hosted
  runner (remote-only).
- Local repo had no wgturn pin, so the GitHub metadata main HEAD
  `416991d2633b497fd37169782f2ef2eab003fa6b` was pinned.

**Follow-ups spawned**: none within R05. The private/inaccessible wgturn branch
is a verification limitation (PKG-1/SUP-4 not dynamically exercised), not a new
code task.

**No merge/tag/release/deploy performed.**

## 13. Rollback

`git revert c0febe02 0d5ee149` on the R05 branch (revert the two code commits;
`d767586d` is docs-only), or delete
`codex/qwen-audit-r05-packaging-supply-2026-07-29`. Because R05 is based on
P08-v2, reverting R05 leaves the P08-v2 appimagetool pin intact. Build scripts
revert to the prior (unpinned/unverified) behavior; no release state is touched.

## 14. Self-contained copyable Qwen prompt

```text
Выполни brief plans/phase2-audit-r05-packaging-supply-2026-07-29.md через Qwen
Code. IDs: PKG-1, SUP-2, SUP-4 (все P2). Base branch:
codex/qwen-audit-p08-appimagetool-pin-v2-2026-07-29 (SUP-2 делит
.github/workflows/build-linux.yml с P08-v2). Сначала прочитай brief целиком,
AGENTS.md, plans/CLAUDE.md, packaging/CLAUDE.md и .github/workflows/CLAUDE.md.
PKG-1: вычисли ARCH из build target (uname -m -> arm64/amd64) до wgturn branch в
build-mac.sh. SUP-2: pin + verify SHA256 sing-box/libcronet archive до extraction
в build-linux.yml (fail-closed). SUP-4: pin wgturn-core на commit/tag и assert до
bundling в build-mac.sh. Переиспользуй существующий native dependency manifest /
fail-closed checksum паттерн из P08-v2. НЕ скачивай и не исполняй непроверенные
binary, НЕ запускай локальные build/shell validation scripts. Только
чтение/поиск/редактирование; shell/YAML синтаксис проверяй статическим осмотром.
Commit/push/CI делает orchestrator (Linux+Mac CI после push). Без release/merge/
tag/deploy. Без emoji. Заполни Outcome шаблоном PENDING.
```
