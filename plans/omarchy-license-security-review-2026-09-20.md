# Security Review: Omarchy Component License Evidence

**Date**: 2026-09-20
**Reviewer**: Independent Read-Only Reviewer
**Verdict**: `changes_required`
**Target Plan**: `plans/phase-omarchy-license-bundle-2026-09-20.md`
**Baseline Commit**: `3b24e131dde8587e409a58e6faf0ac5cb21eac28`

---

## 1. Scope and Cryptographic File Hashes

The review evaluated task-owned Arch packaging and license evidence collection/verification changes against baseline `3b24e131`:

| SHA-256 Digest | File Path |
|---|---|
| `5b72d9ee738d399513896e49f08116f9cdc7a8679e4e286d92fad9c63eb471ec` | `packaging/arch/license_evidence.py` |
| `a72274176cfcd664868c13a83366c014e9d754a6e45596bc0ae51523c26b484f` | `packaging/arch/build_package.py` |
| `94250aa75533d837c65872964a9f16250acb952643bf5a8c163eff0d2e8c222e` | `packaging/arch/staging_tools.py` |
| `4c3ce28d4743b1f11e6ff564435b300b58c432fa47ebca8bd17a195ebc4d6f66` | `packaging/arch/PKGBUILD` |
| `6bc99c34b9120117b055dedc8999b02a921a511cf93c96b4289ec64a84fb204f` | `packaging/arch/README.md` |
| `c8e9c8d0708d169dc1f485458d12c0af12d6e79d1c49040378ab7228d1c26eb8` | `packaging/arch/notices/catalog.json` |
| `426d86b271f1e84d1d5684841877e0242d565047b8797a546a839a20b5008699` | `packaging/arch/tests/test_license_evidence.py` |
| `bc566424c7e077b73d7d970003738a6bd3c86a4c60ddaa2bd88c087ebd4a95c2` | `packaging/arch/tests/test_build_package.py` |
| `f39254d044dd5f9e930d37d456fda2ad1e9624e35b014b15a94eebf7b5a5f6cb` | `packaging/arch/tests/test_staging_tools.py` |
| `306936cd0a31c7c87cbe5313fc1a6280de2c65e089102e0deaec36b16bd1d252` | `plans/phase-omarchy-license-bundle-2026-09-20.md` |

All 51 notice files under `packaging/arch/notices/` were verified to match the exact SHA-256 digests in `catalog.json`.

---

## 2. Threat Model and Operating Assumptions

- **Trust Boundary**: Internal non-root packaging pipeline on Arch Linux. User inputs are local build manifests and restored packages, not remote unauthenticated APIs. Same-UID filesystem concurrency is trusted; cryptographic integrity and fail-closed metadata validation are required.
- **Incomplete Native Licenses**: Per approved phase plan, native components (`Cronet`, `sing-box-vpnctl`) have partial coverage and remain unresolved. Setting `complete: false` and `license_inventory_complete: false` is expected behavior, not a defect.
- **Exclusions**: Live network VPN operation, Linux privilege escalation helpers, and public distribution authorization are out of scope.

---

## 3. Prior Parent Fix Verification

The four fixes implemented by the prior parent task were inspected and confirmed intact:
1. **Path Traversal**: `check_safe_path` and `safe_resolve_relative` strictly reject escaping paths, `..`, `.`, double slashes, backslashes, symlinks, and FIFOs.
2. **RID Selection**: Exact target `net10.0/linux-x64` is enforced in `project.assets.json` and `deps.json`.
3. **Native Payload Bindings**: `NATIVE_PAYLOAD_MAP` strictly verifies exact paths `runtime/libcronet.so` and `runtime/sing-box`.
4. **Known Notice Set**: Removed, renamed, or hash-mismatched notices are rejected by `verify_license_evidence`.

---

## 4. Prioritized Security Finding

### [HIGH] Verification Bypass on Uncataloged Packages and Version Mismatches
- **File**: `packaging/arch/license_evidence.py:700-759`
- **Class**: Verification Gate Bypass / Incomplete Fail-Closed Validation
- **Status**: Confirmed via benign reproduction

#### Evidence & Mechanism
In `verify_license_evidence`:
1. **Uncataloged Packages**: Lines 700–759 iterate over `for cid_k, cat_c in catalog_comps.items():`. If a newly added runtime dependency appears in `VPNRouter.Headless.deps.json` (`allowed_ids`), it exists in `comps_by_id` but is never checked in lines 700–759. The verifier never asserts that uncataloged packages must have `status == "unresolved"`. If an uncataloged package claims `status: "verified_texts"`, verification passes.
2. **Version Mismatches**: For cataloged components with `coverage: "verified_texts"` (e.g. `Serilog`, `YamlDotNet`, `Microsoft.NETCore.App.Host.linux-x64`), line 708 gates all integrity checks behind:
   ```python
   if ev_c.get("version") == cat_c.get("version"):
   ```
   When `ev_c.get("version") != cat_c.get("version")`, lines 709–759 are completely skipped. There is no `else` branch enforcing `ev_c.get("status") == "unresolved"`. Consequently, an unreviewed package version with fabricated repository URLs, mismatched revisions, or invalid license expressions passes verification as `verified_texts`.

#### Benign Reproduction
Under isolated test fixtures, synthetic packages were verified:
1. `Serilog` version updated to `4.5.0` (catalog pins `4.4.0`) with `source.repository_url = "https://attacker.com/serilog"`, `license_expression = "PROPRIETARY-OR-WRONG"`, and `status: "verified_texts"`. `verify_license_evidence` returned success (0).
2. Runtime dependency `UnknownPkg` (not in `catalog.json`) marked `status: "verified_texts"` with arbitrary notices. `verify_license_evidence` returned success (0).

#### Required Remediation
In `packaging/arch/license_evidence.py::verify_license_evidence`:
1. Iterate over all components in `comps_by_id`.
2. If `cid_k not in catalog_comps`: require `ev_c.get("status") == "unresolved"` and require `unresolved` to contain `"unknown package not in catalog"`.
3. If `cid_k in catalog_comps` and `ev_c.get("version") != cat_c.get("version")`: require `ev_c.get("status") == "unresolved"` and require `unresolved` to contain `"unknown version"`.

---

## 5. Scoped Verified Invariants

All other analyzed subsystems meet security standards:
- **XML Parsing**: `safe_read_xml` limits inputs to 256 KiB, blocks UTF-16 BOMs, NUL bytes, non-UTF-8 declarations, and forbids `<!DOCTYPE` and `<!ENTITY` (case-insensitive).
- **JSON Parsing**: `safe_read_json` bounds size to 2 MiB and rejects duplicate keys via `_reject_duplicate_keys`.
- **Apphost Customization**: `verify_apphost_customization` enforces exact byte-level identity against the verified template (`Microsoft.NETCore.App.Host.linux-x64` 10.0.9), confirming only the embedded application DLL name was modified.
- **Offline Assurance**: Zero network calls or HTTP imports in `license_evidence.py`. Catalog notices are resolved strictly from local filesystem paths.
- **Pipeline Integration**: `inspect_package` is called inside `build_makepkg` (`build_package.py:624`) and invokes `verify_license_evidence` on the final extracted package.

---

## 6. Conclusion

Verdict: **`changes_required`**. The collector logic in `collect_license_evidence` correctly flags unknown packages and versions as unresolved, but the independent verifier (`verify_license_evidence`) lacks matching fail-closed assertions, permitting forged or unreviewed dependencies to pass verification as `verified_texts`. Packaging release approval remains blocked until the verifier enforcement is corrected.
