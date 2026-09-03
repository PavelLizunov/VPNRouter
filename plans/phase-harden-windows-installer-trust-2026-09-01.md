# Phase: Harden Windows installer trust boundary

Base: `origin/main` / `e58ac75d7239ecb9ca23b51ef81a5ccfc0f8d255`
Branch: `dsh/harden-windows-installer-trust`
Audit IDs: `SU-1-2`, `BR-2-6`

## 1. Intent & Invariants

- **What:** Prevent caller-controlled `-Version` text from becoming elevated PowerShell source, bind the archive to the resolved tag, and refuse any unverified or replaceable administrative install payload.
- **Invariants:** UAC elevation preserves supported flags as data; only stable/rolling versions are accepted; exactly one version-matching ZIP and sidecar are required; download/hash/extraction uses an administrator-only staging directory; no release/tag/merge/deploy/install is performed.

## 2. Interface / Data Contract

```powershell
-Version <X.Y.Z | X.Y.Z-rN>
# invalid input => terminating validation error before elevation/network/install
# elevation => fixed encoded bootstrap; Base64 version + switch bit mask decoded to a named hashtable splat
# missing/duplicate/mismatched asset or sidecar => terminating failure before extraction
# verified ZIP => administrator/System-only staging until extraction
```

## 3. Verification Checklist (Definition of Done)

- [x] Happy path: stable/rolling versions and every flag combination survive the named bootstrap binding.
- [x] Edge: metacharacters/whitespace cannot enter elevated syntax; exact asset names match the resolved tag.
- [x] Failure: missing/duplicate/malformed/mismatched assets fail closed; medium-integrity processes cannot replace the verified ZIP.
- [x] Focused installer contracts passed inside the designated Windows CI suite.
- [x] Full discovered suite and implementation-head CI passed.
- [x] Independent correctness/test/security bug-hunt has no surviving P0/P1.

## Risk / rollback

- Risk: quoting changes can break Windows PowerShell 5.1 self-elevation; overly strict version grammar can reject legitimate rolling versions.
- Control: use a fixed `-EncodedCommand`, Base64 version + numeric switch mask reconstructed as a named splat, exact assets, known-folder APIs, and ACL-restricted staging; pin behavior/order in tests.
- Rollback: revert this task PR; no migration or persisted-data change exists.

## Six gates

1. **Scope:** only installer, source-contract tests, defect ledger, and this brief.
2. **Trust boundary:** no interpolated caller input in `-Command`; sidecar absence and malformed content terminate.
3. **Compatibility:** stable and `-rN` versions plus `Prerelease`, `Service`, and `NoLaunch` remain supported.
4. **Tests:** focused contracts then full CI test matrix.
5. **Review:** three independent lenses; lead reopens every claimed line.
6. **Handoff:** brief Outcome, scoped commits, pushed PR, exact-head green; merge/release remains owner-only.

## Outcome

- Implementation commit: `7d4e2ad24205f8ccb38043b8f013c309be85bd46`; PR: #205.
- Files: installer trust flow, four release-tooling contracts plus shared assertions, defect ledger, and this brief (`+297/-77` at implementation head).
- The repair rounds additionally closed six source-verified attack paths: pre-UAC temp-script replacement, post-hash ZIP replacement, stale asset selection, environment-spoofed privileged paths, positional rather than named splatting, and path-searchable elevation executable. Staging leaks were closed with `finally`.
- Exact implementation-head CI: `test` green — 2,835 total, 2,778 passed, 57 skipped; `characterization-windows`, `go-test-windows`, and `grep` green.
- Three independent final reviewers returned CLEAN. Ouroboros QA session `qa-95ac0d4b` passed the six approved ACs at `0.95`; its mechanical evaluator could not run locally because this control plane has no `dotnet`, so GitHub Actions remained the build/test oracle.
- Surprises/follow-ups: generic download-timeout, stress-campaign, and fully transactional power-interruption behavior were deliberately not folded into this trust-boundary PR; they remain later availability/recovery triage topics.
- Rollback is a plain PR revert. No migration, release, tag, merge, deploy, or install occurred.
