# VPNRouter Arch Linux Runtime Packaging (Prototype)

Source-only Arch Linux packaging driver and PKGBUILD for `VPNRouter.Headless` and bundled `sing-box` runtime components.

## 1. Prototype Notice & Security Boundaries

- **Source-only staging prototype**: This package is a reviewable staging artifact for the Omarchy Quattro shell plugin integration (`/usr/lib/vpnrouter-headless/`). It is not an authorized production release.
- **No production trust root**: The package manifest (`manifest.json`) describes file inventory, modes, and SHA-256 integrity. It does **not** grant execution authority. Core runtime policy remains unchanged (`SingBoxRuntimePolicy.DefaultProduction` is strictly unavailable and untrusted).
- **No installation instructions**: Live host installation, service activation, Polkit rules, sudoers integration, and `setcap` capabilities are intentionally excluded.

## 2. Future Elevated Broker Boundary

Full VPN operation requires privileged network manipulations:
- Creating and configuring TUN interfaces (`tun0`).
- Manipulating host routing tables and endpoint bypass routes.
- Enforcing nftables/iptables kill-switch rules.
- Hardening DNS resolvers and preventing DNS leaks.

A future helper needs a separately approved contract, not a generic privileged
command runner. That contract must define:

- Explicit administrator consent for a bounded session and operation set; neither
  an active desktop seat nor retained blanket authorization is sufficient.
- Kernel-derived caller identity and host-global ownership across users. The
  current per-UID flock is not a cross-user network lock.
- Typed configuration validation and fixed trusted executable/dependency paths;
  no caller-supplied executable, shell command, raw firewall rules or config path.
  Root ownership and an adjacent manifest alone do not establish provenance.
- Exact process generation and resource ownership for stop/restart, without
  killing another application's process or deleting another owner's rules.
- An explicit owner-loss policy and administrator recovery path: removing a
  kill-switch may leak traffic, but retaining it can lock out the host. Neither
  outcome may be selected implicitly as a cleanup convenience.

No helper, authorization policy or elevation mechanism is implemented here.
CAP_NET_ADMIN is broad network authority, not a TUN-only permission. This package
does not grant it and does not replace the full VPN product with a status widget.

## 3. Packaging & Runtime Architecture

- **Framework-dependent deployment**: `VPNRouter.Headless` is compiled as framework-dependent (`linux-x64`, `--self-contained false`) to avoid bundling redundant .NET runtime binaries.
- **Arch package dependencies**: Requires `glibc`, `gcc-libs`, `dotnet-runtime>=10` and `dotnet-runtime<11`. Native dependency inventory is recorded in the manifest; installed-host compatibility has not been tested.
- **Worker build prerequisites**: Build workers use .NET SDK 10.0.301 resolved via private `DOTNET_ROOT` without requiring pacman SDK packages.
- **Dependency verification**: Shared library dependencies are inspected statically using `readelf -d` (recording `(NEEDED)` DT_NEEDED entries). Invocation of `ldd` is strictly forbidden to prevent arbitrary code execution by the dynamic linker.
- **Direct execution**: Installs directly into `/usr/lib/vpnrouter-headless/`. No shell wrapper is required as the plugin system path directly invokes the apphost executable.
- **NuGet restore policy**: Full offline NuGet caching is not an approved requirement and no new offline cache CLI option is provided. Network restore is permitted during `dotnet publish` into an isolated `NUGET_PACKAGES` directory under `work_dir`.
- **makepkg invocation**: Assembled with `makepkg --nodeps --noconfirm --config /etc/makepkg.conf` under isolated `HOME`/`XDG`/`TMPDIR` within `work_dir` and local package destination variables (`PKGDEST`, `SRCDEST`). Dependency check is skipped (`--nodeps`); automatic dependency installation (`-i`, `-s`) is strictly excluded. Options `!strip !debug` preserve cryptographic file hashes.
- **License evidence collection & verification**: `build_package.py` imports `license_evidence` to execute offline evidence gathering (`collect_license_evidence`) into `/usr/lib/vpnrouter-headless/licenses/components.json` and mandatory verification (`verify_license_evidence`) both after manifest generation and during package inspection (`inspect_package`). Packaging file hashes in `manifest.json` include `license_evidence.py` and `notices/catalog.json`.

## 4. Licensing & Distribution Disclaimer

- **Incomplete license inventory**: The license metadata collected in `/usr/lib/vpnrouter-headless/licenses/` is structured evidence but does **not** constitute a legally complete distribution bundle. `license_inventory_complete` remains `false`.
- **Managed dependencies (mapped text only, not legal release)**: Managed dependencies (`Serilog` 4.4.0, `YamlDotNet` 18.1.0) specify SPDX license expressions (`Apache-2.0`, `MIT`) in `.nuspec` metadata but do not package license files inside `.nupkg`. The collector validates `.nuspec` identity/version against package assets and matches them with reviewed, pinned upstream repository texts (`LICENSE`, `LICENSE.txt`, `LICENSE-libyaml`) and exact SHA-256 hashes in `packaging/arch/notices/catalog.json`. Marked `verified_texts` strictly denotes exact mapped text against catalog pins, not overall legal release clearance.
- **.NET Apphost verification**: The native launcher executable (`VPNRouter.Headless`) is verified against the template binary from `Microsoft.NETCore.App.Host.linux-x64` (10.0.9, observed worker template SHA-256 `a3f840d0dfed7e18034444f55d86a43f731fd27ef1fc10668e9a97a4879ef064`) to ensure only embedded application DLL name customization occurred, pairing it with authoritative upstream `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT`. Verification confirms mapped template/notice text only, not overall legal release authorization.
- **Native dependencies & partial sources (no complete native notices claimed)**:
  - `sing-box-vpnctl`: Bundles exact 791-byte upstream `sing-box-vpnctl/LICENSE` notice file (preserving nekohasekai copyright, GPL-3.0-or-later terms, and the top GPL extra branding restriction: *"In addition, no derivative work may use the name or imply association with this application without prior consent."*). The full GPL-3.0 text is copied from the pinned project root `LICENSE` to `licenses/VPNRouter-LICENSE`. Documenting this binding for the 791-byte notice does **not** infer that GPL release obligations are fulfilled: native dependency inventory (embedded Go dependency notices and corresponding source distribution) remains unresolved and coverage is marked partial text-only.
  - `Cronet`: The offline notice catalog packages 51 texts across 5 components, including 45 candidate third-party texts harvested from `SagerNet/naiveproxy` and `cronet-go`. These 45 texts represent an unverified candidate upstream source superset (`source_chain_unverified: true`), **not** linked-specific compiled credits or verified build provenance (source build provenance remains unknown without a source rebuild). Worker observed `libcronet.so` payload SHA-256 is `dc7293a929dffa695aae1a89555e7366158fa0a3f40bbe3012d445bc05c99672` (matching git blob `5b8e36ad9cd406f5ab783712631ca7c0532b6176` in `cronet-go/lib/linux_amd64` at commit `def9ff0fb992` upstream GitHub contents, verified parent of the blob). This confirms the parent commit of the prebuilt binary blob, but is **not** evidence of a full source build chain to naiveproxy.
  - Native components remain marked as partial coverage; under no circumstances does this prototype claim complete native notices or fulfilled release obligations.
- **Explicit unresolved evidence tracking**: Any unresolved item (such as `Cronet:linked-dependency-selection` or `sing-box-vpnctl:embedded-go-notices`) is flattened into `missing_licenses` in `manifest.json` and preserved alongside missing source repository license files.
- **Offline collector invariants**: License evidence collection is strictly offline and network-free, operating solely on the extracted source tree, publish output, local .NET SDK directory, and trusted local `packaging/arch/notices/catalog.json` (51 texts across 5 components). No new CLI options or offline flags are added. Current runtime pins and binary behavior are fully preserved.
- **No release authorization**: This prototype cannot be published or distributed. Incomplete native notices gate legal release authorization; no release approval is granted or implied.

## 5. Build Driver Usage

```bash
python3 packaging/arch/build_package.py \
  --repo /path/to/VPNRouter \
  --work-dir /path/to/new-scratch-dir \
  --archive-cache /path/to/archive-cache
```

### Invariants:
- `--repo` must contain the exact approved backend commit `05c0bcaee08a5defb1ceeb1cdaab83b3bc1a2a8a`.
- `--work-dir` must be a new, absolute, private directory (pre-existing, non-empty directories or symlinks are rejected).
- Pinned runtime archives are verified against exact SHA-256 hashes prior to extraction using `staging_tools.safe_extract`.
- Outputs the finalized `.pkg.tar.zst` package path and SHA-256 sidecar file.
