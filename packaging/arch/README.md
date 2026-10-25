# VPNRouter Headless for Arch Linux

Builds `vpnrouter-headless-0.1.0.r<run_number>.g<sha7>-<run_attempt>-x86_64.pkg.tar.zst` for the Omarchy plugin.
The package ships `/usr/lib/systemd/user/vpnrouter-headless.service`, running
Headless as the logged-in user with `--service`. The coordinated plugin starts
that unit on demand and attaches to its private Unix socket. It is not enabled
at boot; no GUI or root service is installed. Older plugins can still use
`--stdio`. See [session modes](../../VPNRouter.Headless/README.md#usage).

## Build

Use an Arch CI container or build worker with base-devel, Python, git, libarchive,
libcap and the .NET SDK selected by `global.json` (10.0.301 with `latestPatch` servicing roll-forward). The driver supports
`DOTNET_ROOT` for a private SDK install. It runs makepkg as an unprivileged user.

```bash
python3 packaging/arch/build_package.py \
  --repo "$PWD" --source-sha "$(git rev-parse HEAD)" \
  --work-dir /absolute/path/to/new-build-directory \
  --run-number "$GITHUB_RUN_NUMBER" --run-attempt "$GITHUB_RUN_ATTEMPT"
```

`--source-sha` defaults to the checkout HEAD. The driver exports that commit,
publishes framework-dependent linux-x64, verifies pinned native archives,
assembles and inspects the package, and writes its SHA256 sidecar. The manifest
records `source_commit`, `package_version`, `package_release`, `ci_run_number`,
`ci_run_attempt`, runtime archive hashes, file inventory and build tools. The
required positive decimal `--run-number` determines pkgver; `--run-attempt`
(default 1) determines pkgrel. Identical inputs reproduce the version; later
workflow runs or rerun attempts compare newer in pacman.
`--work-dir` must be new and absolute. `--archive-cache` optionally reads an
existing directory of verified archives.

The core is sing-box-vpnctl 1.14.2-vpnctl.2 linux-amd64; the libcronet donor is
SagerNet/sing-box 1.14.2 linux-amd64. Both archive hashes match
[the desktop Linux workflow](../../.github/workflows/build-linux.yml).
The [Arch CI workflow](../../.github/workflows/arch-headless.yml) builds from
`github.sha`, installs and upgrades the result with pacman, verifies capabilities,
and exercises the installed backend as a normal user with `installed_smoke.py --offline`
(protocol, first-launch diagnostics and runtime policy, without starting TUN).
The smoke script retains its separate TUN lifecycle mode for explicitly authorized
network acceptance. Its artifact is
`vpnrouter-headless-arch-0.1.0.r<run_number>.g<sha7>-<run_attempt>`.

## Install and verify

Download the package and sidecar from the same CI run into an empty directory, then:

```bash
sha256sum -c vpnrouter-headless-*.pkg.tar.zst.sha256
sudo pacman -U ./vpnrouter-headless-*.pkg.tar.zst
getcap /usr/lib/vpnrouter-headless/runtime/sing-box
systemctl --user daemon-reload
systemctl --user start vpnrouter-headless.service
{ printf '%s\n' '{"v":1,"id":"check-1","method":"snapshot","params":{}}'; sleep 2; } | \
  /usr/lib/vpnrouter-headless/VPNRouter.Headless --stdio
```

The unit is part of `graphical-session.target`: logout stops the VPN even with
user lingering enabled. Shell/plugin reload only detaches the controller. A
service crash triggers up to three restarts per minute, with a two-second delay;
the new session starts disconnected. The whole service cgroup receives SIGTERM
on stop, allowing sing-box to remove its TUN and routes.

During package upgrade, active user services are runtime-masked and stopped before
files are replaced, then unmasked and started disconnected after capabilities are
restored. The panel retries its attachment. Removal stops them without restarting.
An interrupted transaction may leave a runtime mask; finish/reinstall the package
or run `systemctl --user unmask --runtime vpnrouter-headless.service` before
starting it again.

Dependencies are glibc, gcc-libs, libcap, iproute2 and .NET runtime >=10 and <11.
The pacman install/upgrade script applies `cap_net_admin,cap_net_raw+ep` to
`/usr/lib/vpnrouter-headless/runtime/sing-box`. Package inspection accepts only
that exact install script and the exact shipped user unit. The unit is included
in packaging source hashes; all unrelated systemd package paths remain forbidden. Production trusts this fixed regular file when it
and every ancestor are root-owned and not group/other writable, and the file
has effective and permitted CAP_NET_ADMIN. Symlinks are rejected. NET_RAW is
optional; NET_BIND_SERVICE is not required by the generated configuration.
No binary SHA pin is used for runtime authorization: the package manager owns
that trust decision. Test-file policies retain their SHA checks.

A disconnected normal-user snapshot reports `capabilities.connect=true` when
runtime and ownership checks pass. A failed runtime check sets it false and
includes `unavailableReason`, also shown by HealthCheck. Reinstalling the package
restores capabilities after file replacement. NoNewPrivs/user namespaces can
prevent file capabilities from taking effect. Kill switch and DNS lockdown
remain false on Linux. The ownership lock coordinates cooperating processes of
the same UID. Installed-owner traffic routing and plugin loading require their
own end-to-end checks.

## Package evidence

The manifest verifies file hashes and modes; ELF dependencies are inventoried
with readelf. Managed and native notice evidence is collected offline from the
source, SDK and [notice catalog](notices/catalog.json). Native corresponding
source and linked Cronet notice coverage remain partial; this CI artifact is
not a tagged release and does not claim complete license inventory.
