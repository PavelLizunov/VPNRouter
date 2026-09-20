# Independent Acceptance Review: Omarchy Component License Bundle

**Review Date**: 2026-09-20
**Target File**: `plans/omarchy-license-acceptance-review-2026-09-20.md`
**Approved Baseline**: `3b24e131` (`plans/phase-omarchy-license-bundle-2026-09-20.md`)
**Scope**: Task-owned packaging/arch changes (diff vs `3b24e131` plus untracked `license_evidence.py`, `notices/` catalog + 51 texts, and test suite).
**Reviewer Constraints**: Independent read-only review. No agents, no source code edits, no commits, no SSH, no network egress, no native binary execution.
**Operational Context**: Medium-security internal packaging driver. Input paths originate from build arguments; same-UID filesystem race immunity is non-adversarial. Packaging fails closed on unknown metadata; runtime distribution, privileges, and live VPN operations remain excluded.

---

## 1. Evaluated Scope & Cryptographic Hashes

The review evaluated exact task artifacts against baseline `3b24e131`:

| Component / File Path | SHA-256 | Status |
|---|---|---|
| `packaging/arch/README.md` | `6bc99c34b9120117b055dedc8999b02a921a511cf93c96b4289ec64a84fb204f` | Modified |
| `packaging/arch/build_package.py` | `a72274176cfcd664868c13a83366c014e9d754a6e45596bc0ae51523c26b484f` | Modified |
| `packaging/arch/license_evidence.py` | `5b72d9ee738d399513896e49f08116f9cdc7a8679e4e286d92fad9c63eb471ec` | Untracked |
| `packaging/arch/notices/catalog.json` | `c8e9c8d0708d169dc1f485458d12c0af12d6e79d1c49040378ab7228d1c26eb8` | Untracked |
| `packaging/arch/tests/test_build_package.py` | `bc566424c7e077b73d7d970003738a6bd3c86a4c60ddaa2bd88c087ebd4a95c2` | Modified |
| `packaging/arch/tests/test_license_evidence.py` | `426d86b271f1e84d1d5684841877e0242d565047b8797a546a839a20b5008699` | Untracked |
| `plans/phase-omarchy-license-bundle-2026-09-20.md` | `306936cd0a31c7c87cbe5313fc1a6280de2c65e089102e0deaec36b16bd1d252` | Approved plan |
| Notices Directory (`packaging/arch/notices/*`, 51 texts) | `d511a6f16bf6db001b323549b86b57d360cc83d69f82de12ccd4345ce6d671d7` (composite) | Untracked |

---

## 2. Executed Verification Commands

1. **Test Suite Execution**:
   - Command: `PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s packaging/arch/tests -v`
   - Result: 117 tests executed, 0 failures, 0 errors, exit code 0.
   - Suite growth: 117 active tests (up from 107 baseline tests: +35 in `test_license_evidence.py`, +2 in `test_build_package.py`).
2. **Catalog Integrity & Notice Assertion**:
   - `test_real_catalog_verification` confirmed all 5 components and 51 notice files exist, are non-empty, and match pinned SHA-256 hashes.
3. **Isolated Edge-Case Replicas**:
   - Benign Python execution verified `license_evidence.py:371` behavior when evaluating packages containing native-only assets (`target_assets` evaluates to `{}`).
   - Apphost template customization verified byte-for-byte replacement against Microsoft 1024-byte placeholder.

---

## 3. Acceptance Review & Contract Analysis

### A. Managed Dependencies (`Serilog`, `YamlDotNet`)
- **Metadata & Asset Join**: Join between `VPNRouter.Headless.deps.json`, `project.assets.json`, and `.nuspec` correctly validates package IDs (`Serilog` 4.4.0, `YamlDotNet` 18.1.0), exact versions, SPDX license expressions (`Apache-2.0`, `MIT`), and SHA-256 parity between package assets and published payload DLLs.
- **Notice Mapping**: Pinned GitHub repository commits (`497f80fd`, `748334a8`) provide verified texts (`Serilog/LICENSE`, `YamlDotNet/LICENSE.txt`, `YamlDotNet/LICENSE-libyaml`).

### B. .NET Apphost (`Microsoft.NETCore.App.Host.linux-x64` 10.0.9)
- **Customization Check**: `verify_apphost_customization` enforces exact length match, 64-byte `APPHOST_PLACEHOLDER` detection, 1024-byte zero-padded replacement with `VPNRouter.Headless.dll`, and strict prefix/suffix byte identity against upstream template `a3f840d0`.
- **Limitation**: ELF byte inspection confirms template provenance, not runtime execution viability. Template lookup depends on local SDK pack paths; missing packs correctly fail closed to `unresolved`.

### C. Native Binaries & Provenance (`sing-box-vpnctl`, `Cronet`)
- **Cronet (1.13.14)**: Harvested 51 notices from `SagerNet/naiveproxy@888e1142` as candidate upstream source superset. Metadata explicitly records `source_chain_unverified: true`, `coverage: partial`, and unresolved items (`linked-dependency-selection`, `generated-Chromium-credits`, `fork-vs-donor-abi-mismatch`). Not legally complete.
- **sing-box-vpnctl (1.14.0-vpnctl.5)**: Embedded Go modules and corresponding source distribution remain missing. Documented incomplete; correctly fails `license_inventory_complete`.

---

## 4. Prioritized Findings

### Finding 1 (Important): Overbroad / Contradictory 'VERIFIED' Claim in Prior Report
- **Location**: `plans/omarchy-license-evidence-report-2026-09-20.md:95`
- **Evidence**: Summary table marks `sing-box-vpnctl` as **`VERIFIED`**. However, `packaging/arch/notices/catalog.json:77` marks `coverage: partial`, `license_evidence.py:568` marks status `unresolved`, and `manifest.json` sets `license_inventory_complete: false`.
- **Impact**: Falsely indicates legal/distribution readiness for an incomplete native binary.

### Finding 2 (Important): 791-Byte Preamble vs Full GPL-3.0 Text Copy
- **Location**: `packaging/arch/notices/catalog.json:72`, `packaging/arch/notices/sing-box-vpnctl/LICENSE:1-17`
- **Evidence**: Upstream `sing-box-vpnctl/LICENSE` is only the 791-byte copyright preamble stating: *"You should have received a copy of the GNU General Public License... If not, see <http://www.gnu.org/licenses/>"*. It omits the 675-line GPLv3 terms.
- **Context**: `build_package.py` copies repo root `LICENSE` (full GPLv3) to `licenses/VPNRouter-LICENSE`. However, `catalog.json` does not reference this relationship or bundle a dedicated GPL-3.0 text for `sing-box-vpnctl`.

### Finding 3 (Minor): NuGet Asset Join Disregards Native Asset Dictionary
- **Location**: `packaging/arch/license_evidence.py:371` vs `314, 689`
- **Evidence**: Line 314 detects dependencies having `(v.get("runtime") or v.get("native"))`, but line 371 inspects only `target_assets = matched_target.get(lib_key, {}).get("runtime", {})`. If a future NuGet package introduces native-only assets, `target_assets` yields `{}`, triggering an empty payload error during verification.
- **Repro**: Calling line 371 on a dictionary containing only a `"native"` key yields `{}`. Currently benign as active packages contain only `runtime` assemblies.

---

## 5. Verdict & Remediations

**Verdict**: `changes_required` (alternatively `scoped_verified` upon reconciling documentation and metadata).

### Required Actions (No Native Rebuild Required):
1. **Reconcile Report**: Correct `plans/omarchy-license-evidence-report-2026-09-20.md` line 95 from `VERIFIED` to `PARTIAL / UNRESOLVED`.
2. **Clarify GPL Full Text**: Note in `catalog.json` or `README.md` that `VPNRouter-LICENSE` provides the full GPL-3.0 text referenced by `sing-box-vpnctl/LICENSE`.
3. **Align Native Asset Join**: Update `license_evidence.py:371` to check `runtime` or `native` assets consistently with line 689.
