# Review of License Security & Acceptance Fixes: Omarchy Arch Packaging

**Date**: 2026-09-20
**Verdict**: `scoped_verified`
**Evaluated Snapshot**: `aaa3965b66d97ee67d818b6bc0f9de13a52dc421` (baseline `3b24e131`)
**Worker Build Observation**: package SHA `fa435081ed94d611cdf8423ff6e86454f663b3e4f8d46b69be2651398556b51a` (parent-reported, under final metadata inspection)

## 1. Cryptographic File Hashes

| SHA-256 Digest | File Path |
|---|---|
| `968fd741ed51e03b53d6a7ecb10cfe440925f267aadef1dafe0e5d2a466e68dd` | `packaging/arch/license_evidence.py` |
| `8430c4c7de6eca938e2311c2206b045524a5920fd8a6e1f4a243517851167e94` | `packaging/arch/tests/test_license_evidence.py` |
| `ed8f1c9ed4556a4a466f420ff2e447ee1a0a69ed8af4a92579eefdd2d791d0ef` | `packaging/arch/README.md` |
| `1e05c80f4b23c42a530e3399432f6b94c44154fdeec9160b5773aeaa8df6866f` | `packaging/arch/.gitignore` |
| `c8e9c8d0708d169dc1f485458d12c0af12d6e79d1c49040378ab7228d1c26eb8` | `packaging/arch/notices/catalog.json` |
| `a72274176cfcd664868c13a83366c014e9d754a6e45596bc0ae51523c26b484f` | `packaging/arch/build_package.py` |
| `66b0302a0597b3eb53486aa443c518a5d8873665793c590a4b3ba085814e37bf` | `plans/omarchy-license-evidence-report-2026-09-20.md` |

## 2. Verification of Targeted Fixes

1. **Unreviewed ID / Version Fail-Closed Guard** (`license_evidence.py:703-706`):
   - In `verify_license_evidence`, all `comps_by_id` are checked against `catalog_comps`.
   - Any uncataloged package ID or mismatched version claiming non-`unresolved` status is rejected with `ValueError("Unreviewed component/version cannot be verified_texts")`.
   - Threat scope: Local packaging metadata integrity (P2/MEDIUM). Tampered/unreviewed inputs fail closed.
2. **Joined Runtime and Native NuGet Dictionaries** (`license_evidence.py:372, 692`):
   - Asset resolution merges `{**target_entry.get("runtime", {}), **target_entry.get("native", {})}`.
   - Native-only and mixed runtime/native packages map assets into `payload_files` without dropping bindings.
3. **4 Direct Regression Tests** (`test_license_evidence.py:33, 71, 135, 196`):
   - `test_regression_unknown_version_status_forged_verified_rejected`: confirms guard rejects unreviewed versions.
   - `test_regression_unknown_id_forged_verified_rejected`: confirms guard rejects uncataloged IDs.
   - `test_regression_native_only_nuget_fixture_maps_asset`: verifies native-only NuGet mapping.
   - `test_regression_mixed_runtime_and_native_validates_both_not_loses_one`: verifies multi-asset integrity.
   - Temporary RED loader removed; all 4 run cleanly without skips in fresh 121-test suite execution (121 passed, 0 failed).
4. **Notice Source Ingestion Fix** (`packaging/arch/.gitignore:15-16`):
   - Rules `!notices/**/src/` and `!notices/**/src/**` unignore vendored Chromium notice paths.
   - Exported Git snapshot `aaa3965b` and `git archive` contain all 52 files (catalog + 51 notice texts, including 43 under `src/`).
5. **Documentation & Provenance Alignment**:
   - `omarchy-license-evidence-report-2026-09-20.md` and `README.md` corrected: `sing-box-vpnctl` and `Cronet` marked `PARTIAL / UNRESOLVED`.
   - Documented exact 791-byte notice vs full GPL-3.0 text in `licenses/VPNRouter-LICENSE`.
   - Documented 45 Cronet notices as unverified candidate upstream source superset (`source_chain_unverified: true`), noting parent blob commit (`def9ff0f`) without claiming build provenance.

## 3. Remaining Known Exclusions

- Native components remain incomplete (`license_inventory_complete: false`); no legal release authorization is granted.
- Upstream source-build chain and ABI compatibility for `libcronet.so` remain unproven.
