# Native License Research: sing-box-vpnctl and libcronet.so Donor

## 1. Overview & Baseline Context

- **Baseline context**: VPNRouter package baseline `d5a8ee70`, backend `05c0bcae`, .NET SDK `10.0.301`.
- **Scope**: Read-only research tracing upstream workflows, native binary build flags, source copying, and license notice chains for `sing-box-vpnctl` and donor `libcronet.so`.
- **Disclaimer**: Technical research findings only; distinguishes mapped text from unresolved components and does not constitute legal completeness, formal advice, or legal release authorization.

---

## 2. Release Workflows, Build Flags & Native Library Copying

### Pinned sing-box-vpnctl
- **Release**: `v1.14.0-vpnctl.5`
- **Repository**: `https://github.com/PavelLizunov/sing-box-vpnctl`
- **Commit**: `8688eab3c51d07ff89a124ce247044409b9f93fd`
- **Archive URL**: `https://github.com/PavelLizunov/sing-box-vpnctl/releases/download/v1.14.0-vpnctl.5/sing-box-1.14.0-vpnctl.5-linux-amd64.tar.gz`
- **Archive SHA-256**: `5f98eacc95b9ed9c1d53592605d812d9d2cb8c496fa95723cfb7c5f9447fe46e`
- **Workflow**: `.github/workflows/release.yml`, job `build-linux` (matrix: `goarch: amd64`).
- **Build Flags**: `CGO_ENABLED="0"`, `GOOS=linux`, `GOARCH=amd64`.
  - Command: `go build -trimpath -tags "$DEFAULT_TAGS" -ldflags "-s -w -checklinkname=0 -X github.com/sagernet/sing-box/constant.Version=1.14.0-vpnctl.5" -o dist/sing-box ./cmd/sing-box`
  - `DEFAULT_TAGS`: `with_gvisor,with_quic,with_dhcp,with_wireguard,with_utls,with_clash_api,with_v2ray_api,with_naive_outbound,with_purego,badlinkname,tfogo_checklinkname0,with_xhttp,with_awg`.
- **Native Library Copying**: None. The packaging step copies only `dist/sing-box`, `LICENSE`, `README.md`. No `libcronet.so` is built, extracted, or bundled in this release tarball.
- **Root LICENSE & Exact Notice Binding**: 791 bytes. SHA-256: `650d5e3b99a446fb38e820fa87a49562e0c79eab868fff58618ac487a58e554c`.
  - Verbatim preserves: `Copyright (C) 2022 by nekohasekai <contact-sagernet@sekai.icu>`.
  - License terms: GNU General Public License v3 or later (`GPL-3.0-or-later`), with nekohasekai's additional restriction: *"In addition, no derivative work may use the name or imply association with this application without prior consent."* (Top GPL extra branding clause preserved).
  - Binding status: The exact 791-byte notice file is bound as `sing-box-vpnctl/LICENSE`. Full GPLv3 text is copied from the pinned project root `LICENSE` to `licenses/VPNRouter-LICENSE`. Documenting this exact file binding does **not** infer that GPL release obligations are fulfilled: embedded Go dependency notices and corresponding source distribution remain unresolved, keeping coverage strictly partial text-only (`coverage: partial`).

### libcronet.so Donor (SagerNet/sing-box)
- **Release**: `v1.13.14`
- **Repository**: `https://github.com/SagerNet/sing-box`
- **Commit**: `25a600db24f7680ad9806ce5427bd0ab8afe1114`
- **Archive URL**: `https://github.com/SagerNet/sing-box/releases/download/v1.13.14/sing-box-1.13.14-linux-amd64.tar.gz`
- **Archive SHA-256**: `f48703461a15476951ac4967cdad339d986f4b8096b4eb3ff0829a500502d697`
- **Workflow**: `.github/workflows/build.yml`, job `build_linux` (`variant: purego`, `naive: true`).
- **Build Flags**: `CGO_ENABLED="0"`, `GOOS=linux`, `GOARCH=amd64`.
  - Tags: `with_gvisor,with_quic,with_dhcp,with_wireguard,with_utls,with_acme,with_clash_api,with_tailscale,with_ccm,with_ocm,with_naive_outbound,badlinkname,tfogo_checklinkname0,with_purego`.
  - LDFLAGS: `-X 'github.com/sagernet/sing-box/constant.Version=1.13.14' -X internal/godebug.defaultGODEBUG=multipathtcp=0 -checklinkname=0 -s -w -buildid=`.
- **Native Library Extraction**:
  - Clones `https://github.com/sagernet/cronet-go.git` at commit in `.github/CRONET_GO_VERSION` (`98d539ce67568fb911654e66a14cf4247ed833ec`).
  - Compiles `cmd/build-naive` with `CGO_ENABLED=0`.
  - Runs: `build-naive extract-lib --target linux/amd64 -o $GITHUB_WORKSPACE/dist`.
  - Mechanism in `cmd_extract_lib.go`: queries `git ls-remote https://github.com/sagernet/cronet-go.git refs/heads/go` for the latest commit on `go` branch, then runs `go mod download -json github.com/sagernet/cronet-go/lib/linux_amd64@<commitHash>` and copies precompiled `libcronet.so` (11,765,624 bytes) into `dist/`.
  - Archive creation: copies `dist/sing-box`, `cp ../LICENSE "${DIR_NAME}"`, and `cp libcronet.so "${DIR_NAME}"`.
  - Notice Gap: Donor release tarball bundled only the 791-byte GPL `LICENSE`. No Chromium notices or Cronet third-party notices were copied into the archive.

---

## 3. Upstream Source Chain & Revision Mappings

### Verified Mappings & Worker Observed Hashes
1. **Donor sing-box v1.13.14 & Precompiled Payload**:
   - `go.mod`: `github.com/sagernet/cronet-go v0.0.0-20260620140045-05ab0dc17597`
   - `go.mod` indirect: `github.com/sagernet/cronet-go/lib/linux_amd64 v0.0.0-20260620135226-def9ff0fb992`
   - `.github/CRONET_GO_VERSION`: `98d539ce67568fb911654e66a14cf4247ed833ec`
   - **Worker Observed Payload SHA-256**: `dc7293a929dffa695aae1a89555e7366158fa0a3f40bbe3012d445bc05c99672`.
   - **Git Blob SHA**: `5b8e36ad9cd406f5ab783712631ca7c0532b6176` (11,765,624 bytes) in `github.com/sagernet/cronet-go/lib/linux_amd64` commit `def9ff0fb992` ("Build from 98d539ce", 2026-06-20T13:52:26Z) upstream GitHub contents, verified parent of the blob.
   - **Provenance Limit**: Verification of the git blob in `def9ff0fb992` confirms the upstream GitHub parent of the precompiled binary blob, but is **NOT evidence of a full source build chain to naiveproxy**.
   - `cronet-go` root `LICENSE` at `def9ff0fb992`: 674 bytes, SHA-256 `2f02b7486bcfa90d115c71a20437f3906b6fd5bef81c5dc0efd341399e89d0fd` (GPL-3.0-or-later header by nekohasekai).
   - In `cronet-go` at `98d539ce6756` / `def9ff0fb992`, submodule `naiveproxy` points to `SagerNet/naiveproxy` commit `888e114241c89b05fac4e4ee01482d7bd89ca15a`.
   - In `SagerNet/naiveproxy` at `888e114241c89b05fac4e4ee01482d7bd89ca15a`:
     - `CHROMIUM_VERSION`: `148.0.7778.96`.
     - Root `LICENSE`: 1559 bytes, SHA-256 `845022e0c1db1abb41a6ba4cd3c4b674ec290f3359d9d3c78ae558d4c0ed9308` (BSD-3-Clause).
     - `src/LICENSE`: 1536 bytes, SHA-256 `368cca1106be99d39ecd32a38d8305585d802a475effb66380b91ffc9bcf709b` (Chromium BSD-3-Clause).
     - 42 third-party license/notice files in `src/third_party` (BoringSSL, Brotli, LLVM libc++, zlib, zstd, abseil-cpp, etc.).
   - **Candidate Upstream Source Superset**: The catalog packages 51 texts across 5 components (45 texts under Cronet from naiveproxy/cronet-go). These 45 texts represent an unverified candidate upstream source superset (`source_chain_unverified: true`), not linked-specific compiled credits; actual source build provenance remains unknown.

2. **Fork sing-box-vpnctl v1.14.0-vpnctl.5**:
   - `go.mod`: `github.com/sagernet/cronet-go v0.0.0-20260831031307-45832ab07484`
   - `go.mod` indirect: `github.com/sagernet/cronet-go/lib/linux_amd64 v0.0.0-20260831030607-f80ef37265e5`
   - `.github/CRONET_GO_VERSION`: `45832ab074849607406baa3e3a2c4660274602ed`
   - `cronet-go` commit `f80ef37265e5` ("Build from d9872d6d", 2026-08-31T03:06:07Z) contains `libcronet.so` (11,915,112 bytes, git blob SHA `9dbecbef5d11b40d83551fd9744ced49a67a4702`).
   - Submodule `naiveproxy` in `d9872d6d` / `f80ef37265e5` points to `c823360c60cf54aab2ee2ac1644f69cc611d94d5` (`CHROMIUM_VERSION`: `150.0.7871.63`).

3. **.NET Apphost Worker Baseline**:
   - Worker observed host 10.0.9 template SHA-256: `a3f840d0dfed7e18034444f55d86a43f731fd27ef1fc10668e9a97a4879ef064`.

### Unresolved Mismatches
- **Payload Disagreement**: VPNRouter packaging pairs `sing-box` from v1.14.0-vpnctl.5 (compiled against purego stubs for Chromium 150.0.7871.63) with `libcronet.so` from v1.13.14 (built from Chromium 148.0.7778.96). Attribution follows the actual deployed payload (Chromium 148).

---

## 4. Candidate Upstream Superset vs. Source Provenance Boundaries

1. The precompiled donor binary matches the git blob in `SagerNet/cronet-go@def9ff0fb992`, but this commit is itself an automated binary dump ("Build from 98d539ce").
2. The catalog includes 45 notice texts harvested from `SagerNet/naiveproxy@888e114241c89b05fac4e4ee01482d7bd89ca15a` (primary Chromium BSD-3-Clause `src/LICENSE`, wrapper `cronet-go` `LICENSE`, and 43 third-party notices):
   - These 45 texts constitute a **candidate upstream source superset**, not verified linked-specific credits.
   - Full source build chain provenance remains unverified (`source_chain_unverified: true`).
   - Exact compiler flags, static library link selections, and generated Chromium third-party credits remain unknown without a full source rebuild.
3. For Serilog, YamlDotNet, and .NET Apphost, verification confirms mapped text/template hashes against catalog pins, not overall legal release clearance.

---

## 5. Go Dependency Inventory & Concrete Boundaries

- **Static Go Analysis**: Static inspection via `go version -m sing-box` reads embedded Go modules without binary execution.
- **Bound Scope**: Scope is strictly limited to modules reported in `.go.buildinfo` of `sing-box`.
- **Truthful Packaging Boundaries**:
  1. Record dual attribution in package manifest (runtime binary vs native shared object).
  2. Vend pinned license texts into `/usr/lib/vpnrouter-headless/licenses/` with exact SHA-256 sidecars (total catalog: 51 texts across 5 components).
  3. Keep `license_inventory_complete: false` and `sing-box-vpnctl` coverage as `partial` until Go embedded dependencies and corresponding source code distribution are resolved.
  4. Ensure documentation clearly reflects that verified text mappings do not constitute legal release authorization.
