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

## 4. Licensing & Distribution Disclaimer

- **Incomplete license inventory**: The license metadata collected in `/usr/lib/vpnrouter-headless/licenses/` is best-effort and does not constitute a legally complete distribution bundle.
- **sing-box-vpnctl runtime**: Includes upstream GPL-3.0 `LICENSE` and `README.md`.
- **Cronet shared library**: The SagerNet sing-box archive bundles upstream sing-box licensing, but omits full Chromium third-party notices, patents, and copyright attributions.
- **Upstream preservation**: Repository `NOTICE.md` reflects best-effort inventory. Upstream dependencies must be preserved, not rebranded. Authoritative `Serilog` and `YamlDotNet` licenses are harvested from restored NuGet package content when present.
- **Distribution restriction**: This prototype **cannot** be publicly distributed or published until a complete legal notices audit of all bundled native libraries (specifically Chromium/Cronet) has been performed.

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
