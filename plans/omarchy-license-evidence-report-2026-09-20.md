# Omarchy Component License Evidence Report (2026-09-20)

**Scope & Disclaimer**: Technical research into software provenance, nuspec metadata, upstream repository commits, and license texts for components shipped in Arch Linux staging package (`vpnrouter-headless-0.1.0-1-x86_64.pkg.tar.zst`). Baseline packaging `d5a8ee70`, backend `05c0bcae`, SDK `10.0.301`. This report distinguishes verified text mappings (`verified_texts`) from unresolved components (`partial`); 'VERIFIED' denotes exact package identity, commit, and upstream notice text or template hash verification against catalog pins, NOT overall legal release clearance or distribution authorization.

---

## 1. Backend Project Dependencies (Commit 05c0bcae)

Inspection of `VPNRouter.Headless/VPNRouter.Headless.csproj` and `VPNRouter.Core/VPNRouter.Core.csproj` at commit `05c0bcaee08a5defb1ceeb1cdaab83b3bc1a2a8a`:
- `VPNRouter.Headless`: `<OutputType>Exe</OutputType>`, `<TargetFramework>net10.0</TargetFramework>`, references `VPNRouter.Core`.
- `VPNRouter.Core`: `<TargetFrameworks>net10.0</TargetFrameworks>` (default Linux build).
  - Runtime NuGet packages: `Serilog` (4.4.0), `YamlDotNet` (18.1.0).
  - Build-time analyzer: `Vecc.YamlDotNet.Analyzers.StaticGenerator` (18.1.0) with `<PrivateAssets>all</PrivateAssets>` (excluded from runtime payload).
  - Windows-only packages (`TraceEvent`, `System.Management`, `SystemEvents`) gated behind `PLATFORM_WINDOWS` (excluded from Linux build).

---

## 2. NuGet Package Metadata & Upstream Sources

Official NuGet Flat Container API inspected:

### A. Serilog 4.4.0
- **Nuspec**: `https://api.nuget.org/v3-flatcontainer/serilog/4.4.0/serilog.nuspec`
- **Nupkg SHA-256**: `b4fe17d3a4e170354be975ed60885d272c67e2e2fd8cb2f4b370d13ba1cd424f`
- **Authors**: Serilog Contributors | **Copyright**: `Copyright © Serilog Contributors`
- **License Metadata**: `<license type="expression">Apache-2.0</license>` (URL: `https://licenses.nuget.org/Apache-2.0`)
- **Package Archive Contents**: No license file is packaged in `.nupkg`.
- **Repository**: `https://github.com/serilog/serilog`
- **Commit SHA**: `497f80fda4f9e8f98b9c13ba34b1f0530f8c4449`
- **Upstream License File**: `LICENSE` (Apache-2.0 text, 10,273 bytes)
  - URL: `https://raw.githubusercontent.com/serilog/serilog/497f80fda4f9e8f98b9c13ba34b1f0530f8c4449/LICENSE`
  - SHA-256: `73ba74dfaa520b49a401b5d21459a8523a146f3b7518a833eea5efa85130bf68`
- **NOTICE**: No Apache 2.0 §4(d) `NOTICE` file exists in repo; `INFLUENCES.md` acknowledges inspiration only.
- **Verification State**: **VERIFIED (Mapped Text Only)** — Verifies exact upstream commit `LICENSE` text match against catalog pin; does not denote overall legal release clearance.

### B. YamlDotNet 18.1.0
- **Nuspec**: `https://api.nuget.org/v3-flatcontainer/yamldotnet/18.1.0/yamldotnet.nuspec`
- **Nupkg SHA-256**: `59ffe65ade67ad9d886267f877b634a450363cb81b94e19db9ca4c36461416f6`
- **Authors**: Antoine Aubry | **Copyright**: `Copyright (c) Antoine Aubry and contributors`
- **License Metadata**: `<license type="expression">MIT</license>` (URL: `https://licenses.nuget.org/MIT`)
- **Package Archive Contents**: No license file is packaged in `.nupkg`.
- **Repository**: `https://github.com/aaubry/YamlDotNet`
- **Commit SHA**: `748334a8fa7c227740018b284b71ad95cc6b7fc7`
- **Upstream License Files**:
  1. `LICENSE.txt` (MIT text with copyright notice, 1,110 bytes)
     - URL: `https://raw.githubusercontent.com/aaubry/YamlDotNet/748334a8fa7c227740018b284b71ad95cc6b7fc7/LICENSE.txt`
     - SHA-256: `daea8b7f1b2bab48ea12ad50b0ce91634669d99d7dd0cb64aed8dffeeef69ed3`
  2. `LICENSE-libyaml` (Embedded Kirill Simonov libyaml MIT license, 1,077 bytes)
     - URL: `https://raw.githubusercontent.com/aaubry/YamlDotNet/748334a8fa7c227740018b284b71ad95cc6b7fc7/LICENSE-libyaml`
     - SHA-256: `cb65f46df0d9125ba60862117200ec6631fef3a8169cd0b4513ffcad4ef5cb9b`
- **Verification State**: **VERIFIED (Mapped Text Only)** — Verifies exact upstream commit `LICENSE.txt` and `LICENSE-libyaml` text matches against catalog pins; does not denote overall legal release clearance.

---

## 3. .NET Apphost & Microsoft.NETCore.App.Host.linux-x64 (10.0.9)

- **SDK to Runtime Mapping**: `global.json` and `build_package.py` pin SDK `10.0.301`. Official Microsoft documentation ([dotnet/core release notes 10.0.9](https://github.com/dotnet/core/blob/main/release-notes/10.0/10.0.9/10.0.9.md)) documents SDK 10.0.301 includes **.NET Runtime 10.0.9**.
- **Publish Mechanism**: `dotnet publish VPNRouter.Headless.csproj -c Release -r linux-x64 --self-contained false` builds executable `VPNRouter.Headless`. For framework-dependent executable publish targeting a RID, the .NET SDK copies and customizes the native launcher binary (`apphost`) from the host pack.
- **Host Package**: `Microsoft.NETCore.App.Host.linux-x64` version `10.0.9`.
  - **Nuspec**: `https://api.nuget.org/v3-flatcontainer/microsoft.netcore.app.host.linux-x64/10.0.9/microsoft.netcore.app.host.linux-x64.nuspec`
  - **Repository**: `https://github.com/dotnet/dotnet` commit `901ca941248413c79832d2fdbd709da0c4386353`
  - **Native Binary**: `runtimes/linux-x64/native/apphost` (78,256 bytes, actual worker observed template SHA-256: `a3f840d0dfed7e18034444f55d86a43f731fd27ef1fc10668e9a97a4879ef064`)
  - **Bundled License**: `LICENSE.TXT` (MIT, 1,116 bytes, SHA-256: `cfc21f5e8bd655ae997eec916138b707b1d290b83272c02a95c9f821b8c87310`, `Copyright (c) .NET Foundation and Contributors`)
  - **Bundled Notices**: `THIRD-PARTY-NOTICES.TXT` (76,623 bytes, SHA-256: `66f1d4e44973185519bb4aa8a9718eb22fc7af2cc532e3ae9cfc4c127ee7fc54`)
- **Verification State**: **VERIFIED (Mapped Text / Binary Template Only)** — Verifies template binary hash and bundled notices against catalog pin; does not denote overall legal release authorization.

---

## 4. project.assets.json Analysis & gather_nuget_licenses Bug

### Root Cause of the Bug
`gather_nuget_licenses` currently iterates over `assets_data["libraries"]` seeking `lib_val.get("path")` and scanning that directory for files matching `license*`.
1. **Empty Directory Match**: Packages using `<license type="expression">` (Serilog, YamlDotNet) do NOT unpack any `LICENSE` file into the NuGet cache directory. Only `<license type="file">` packages include one. Seeking `license*` on disk will never locate their licenses.
2. **Targets vs. Libraries Separation**:
   - `libraries` contains all packages across all target frameworks, tools, and build configurations.
   - `targets["<TFM>"]` or `targets["<TFM>/<RID>"]` defines the dependency graph for the specific compilation/runtime target.
   - In `targets`, package entries contain `"runtime"` or `"compile"` assembly maps, but do **NOT** contain the `"path"` key.
   - The `"path"` key resides solely on `libraries["<PackageId>/<Version>"]["path"]`.
3. **Reliable Resolution Algorithm**:
   - Step 1: Select target framework entry from `targets` matching publish target (e.g., `net10.0` or `net10.0/linux-x64`).
   - Step 2: Filter packages having `"runtime"` or `"native"` dictionaries (excluding compile-only/analyzer packages).
   - Step 3: Join matching package keys against `libraries` to read `path` and `files`.
   - Step 4: Parse `.nuspec`: if `<license type="file">`, copy from package folder; if `<license type="expression">`, look up verified upstream text by commit SHA and SHA-256 hash.

---

## 5. Summary of Component States & Provenance Inventory

The packaging catalog incorporates 51 notice texts across 5 components. "VERIFIED (Mapped Text)" strictly denotes exact mapping against trusted catalog pins, not overall legal release authorization.

| Component | Version | Artifact Path | License / Notice Evidence | State |
|---|---|---|---|---|
| **VPNRouter** | 0.1.0 | Managed DLLs | `LICENSE` (full GPLv3 copied to `licenses/VPNRouter-LICENSE`), `NOTICE.md` | **VERIFIED (Project Text)** |
| **Serilog** | 4.4.0 | `Serilog.dll` | Git `497f80fd...`: `LICENSE` (SHA-256 `73ba74df...`) | **VERIFIED (Mapped Text Only)** |
| **YamlDotNet** | 18.1.0 | `YamlDotNet.dll` | Git `748334a8...`: `LICENSE.txt` (`daea8b7f...`), `LICENSE-libyaml` (`cb65f46d...`) | **VERIFIED (Mapped Text Only)** |
| **.NET Apphost** | 10.0.9 | `VPNRouter.Headless` | Host template `Microsoft.NETCore.App.Host.linux-x64` (observed template SHA-256 `a3f840d0...`): `LICENSE.TXT` (`cfc21f5e...`), `THIRD-PARTY-NOTICES.TXT` (`66f1d4e4...`) | **VERIFIED (Mapped Text / Template)** |
| **sing-box-vpnctl** | 1.14.0-vpnctl.5 | `sing-box` | Upstream release archive: exact 791-byte `LICENSE` notice file (`650d5e3b...` preserving nekohasekai copyright, GPL header, and extra branding restriction: *"In addition, no derivative work may use the name or imply association with this application without prior consent"*). Project root GPLv3 copied to `licenses/VPNRouter-LICENSE`. Embedded Go notices and corresponding source distribution unresolved. | **PARTIAL (Text-only; native dependency inventory unresolved)** |
| **Cronet** | 1.13.14 | `libcronet.so` | Worker observed payload SHA-256 `dc7293a9...`; git blob `5b8e36ad...` matches `cronet-go/lib/linux_amd64` at commit `def9ff0fb992` upstream GitHub contents (verified parent blob). Catalog bundles candidate superset (45 texts from `naiveproxy`/`cronet-go`), but `source_chain_unverified: true`; linked dependency selection and build provenance unknown. | **PARTIAL / UNRESOLVED** |

### Critical Evidence & Provenance Boundaries
- **sing-box-vpnctl Overclaim Correction**: Marking `sing-box-vpnctl` as verified was an overclaim. While the exact 791-byte notice file is bound and the full GPL-3.0 text is copied to `licenses/VPNRouter-LICENSE`, documenting this binding does **not** infer that release or distribution obligations are fulfilled. Embedded Go dependency notices and corresponding source distribution are unresolved, keeping coverage strictly `partial`.
- **libcronet Provenance Boundary**: The payload SHA-256 `dc7293a929dffa695aae1a89555e7366158fa0a3f40bbe3012d445bc05c99672` and git blob `5b8e36ad9cd406f5ab783712631ca7c0532b6176` confirm the parent commit `def9ff0fb992` in `github.com/sagernet/cronet-go/lib/linux_amd64`. This is **not** evidence of a full source build chain to `SagerNet/naiveproxy`. The 45 notice texts from naiveproxy and cronet-go in the catalog represent an unverified candidate upstream source superset (`source_chain_unverified: true`), not linked-specific compiled credits; actual source build provenance remains unknown without a source rebuild.
- **Legal Release Clearance**: No verification state in this report grants or implies legal release authorization.

---

## 6. Proposed Validation Tests

1. `test_assets_target_join`: Validates joining `targets` runtime assets with `libraries` path, rejecting compile-time analyzers.
2. `test_expression_license_upstream_hash`: Verifies exact upstream commit license retrieval against pinned SHA-256 hashes.
3. `test_tampered_license_hash_rejection`: Ensures 1-byte corruptions fail verification fail-closed.
4. `test_apphost_license_and_third_party_notices_gathered`: Confirms `apphost` `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT` are bundled into the license directory.
5. `test_partial_coverage_rejection_of_verified_status`: Asserts that components with `coverage: partial` (sing-box-vpnctl, Cronet) cannot be marked `verified_texts` without raising validation errors.
4. `test_apphost_license_and_third_party_notices_gathered`: Confirms `apphost` `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT` are bundled into the license directory.
