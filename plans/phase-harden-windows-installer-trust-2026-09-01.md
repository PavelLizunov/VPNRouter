# Phase: Harden Windows installer trust boundary

Base: `origin/main` / `e58ac75d7239ecb9ca23b51ef81a5ccfc0f8d255`
Branch: `dsh/harden-windows-installer-trust`
Audit IDs: `SU-1-2`, `BR-2-6`

## 1. Intent & Invariants

- **What:** Prevent caller-controlled `-Version` text from becoming elevated PowerShell source, and refuse an administrative install when the matching SHA256 sidecar is absent.
- **Invariants:** UAC elevation preserves all supported flags and literal version data; only release versions matching the repository's stable/rolling grammar are accepted; every downloaded install ZIP is verified against a present, well-formed sidecar before extraction; no release/tag/merge/deploy/install is performed by this task.

## 2. Interface / Data Contract

```powershell
-Version <X.Y.Z | X.Y.Z-rN>
# invalid input => terminating validation error before elevation/network/install
# elevation => script path and every argument passed as distinct native arguments
# missing/malformed/unreadable .sha256 => terminating failure before extraction
```

## 3. Verification Checklist (Definition of Done)

- [ ] Happy path: source contract proves stable/rolling versions and all flags survive elevation as separate arguments.
- [ ] Edge: metacharacters/whitespace cannot enter an elevated command string.
- [ ] Failure: missing or malformed sidecar fails closed before extraction.
- [ ] Focused `ReleaseToolingContractTests` pass on the designated Windows CI runner.
- [ ] Full discovered suite and exact-head CI pass.
- [ ] Independent correctness/test/security bug-hunt has no surviving P0/P1.

## Risk / rollback

- Risk: quoting changes can break Windows PowerShell 5.1 self-elevation; overly strict version grammar can reject legitimate rolling versions.
- Control: use `Start-Process -ArgumentList` with a downloaded script file, fixed parameter tokens, and the existing version grammar; pin source shape in tests.
- Rollback: revert this task PR; no migration or persisted-data change exists.

## Six gates

1. **Scope:** only installer, source-contract tests, defect ledger, and this brief.
2. **Trust boundary:** no interpolated caller input in `-Command`; sidecar absence and malformed content terminate.
3. **Compatibility:** stable and `-rN` versions plus `Prerelease`, `Service`, and `NoLaunch` remain supported.
4. **Tests:** focused contracts then full CI test matrix.
5. **Review:** three independent lenses; lead reopens every claimed line.
6. **Handoff:** brief Outcome, scoped commits, pushed PR, exact-head green; merge/release remains owner-only.

## Outcome

Pending implementation and verification.
