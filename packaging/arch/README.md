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

When updating an existing plugin, disconnect in its panel, disable it with
`omarchy plugin disable io.github.pavellizunov.vpnrouter`, and stop the unit if
installed (`systemctl --user stop vpnrouter-headless.service`). An older package
has no unit: use `pgrep -a -u "$(id -u)" -f '(^|[ /])VPNRouter[.]Headless([.]dll)? --stdio( |$)'`
to find a remaining legacy helper, terminate only its listed PID with
`kill -TERM PID`, and wait for exit. Service masking cannot stop a shell-owned
stdio helper. Do this before replacing old assemblies, even if the VPN was
already disconnected. If an old helper leaves a TUN briefly, wait for it to
vanish before connecting the replacement.

Download the package and sidecar from the same CI run into an empty directory, then:

```bash
sha256sum -c vpnrouter-headless-*.pkg.tar.zst.sha256
sudo pacman -U ./vpnrouter-headless-*.pkg.tar.zst
getcap /usr/lib/vpnrouter-headless/runtime/sing-box
systemctl --user daemon-reload
systemctl --user start vpnrouter-headless.service
{ printf '%s\n' '{"v":1,"id":"check-1","method":"snapshot","params":{}}'; sleep 2; } | \
  /usr/lib/vpnrouter-headless/VPNRouter.Headless --attach
```

Run the attach diagnostic while the plugin is disabled: there is only one
controller. With an enabled plugin, `controller_busy` is expected; use
`systemctl --user is-active vpnrouter-headless.service` instead. Do not start a
second stdio backend with the same data directory beside the service.
Update the plugin from its coordinated branch, remove any plugin-local payload
with `./setup --remove` from the installed plugin directory, rescan/enable it,
and cold-restart the shell once to load the new transport. A local payload takes
priority over this package and retains stdio lifetime. See the plugin's
[EN](https://github.com/PavelLizunov/omarchy-vpnrouter/blob/dsh/omarchy-plugin-n4-lifetime/docs/guide.md#upgrading-an-existing-installation)
and [RU](https://github.com/PavelLizunov/omarchy-vpnrouter/blob/dsh/omarchy-plugin-n4-lifetime/docs/guide.ru.md#обновление-существующей-установки)
upgrade sequence.

The unit is part of `graphical-session.target`: logout stops the VPN even with
user lingering enabled. Shell/plugin reload only detaches the controller. A
service crash triggers up to three restarts per minute, with a two-second delay;
the new session starts disconnected. The whole service cgroup receives SIGTERM
on stop, allowing sing-box to remove its TUN and routes.

During package upgrade, the service is runtime-masked for every existing user
manager before files are replaced, including inactive services that the panel
could otherwise start during extraction. Running services are stopped; after
capabilities are restored the transaction's masks are removed, and only
previously active services are explicitly started, disconnected. A runtime mask
that the caller set before the transaction is preserved; masks recorded by an
interrupted package transaction are recovered instead. The panel retries its attachment. Removal stops them without restarting.
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
includes `unavailableReason`, also shown by HealthCheck. After file replacement or stripped capabilities, repair once in a terminal:

```bash
sudo setcap cap_net_admin,cap_net_raw+ep /usr/lib/vpnrouter-headless/runtime/sing-box
```

If `setcap` reports that the operation is unsupported, install the package on a
filesystem with extended-attribute support. Reinstalling the package also restores
capabilities. Connect and disconnect never invoke an interactive privilege prompt;
missing privileges are reported instead. NoNewPrivs/user namespaces can
prevent file capabilities from taking effect. Kill switch and DNS lockdown
remain false on Linux. The ownership lock coordinates cooperating processes of
the same UID. Installed-owner traffic routing and plugin loading require their
own end-to-end checks.

The package installs `49-vpnrouter-headless-resolved.rules` under
`/usr/share/polkit-1/rules.d`. Only an active local session can set DNS servers,
set routing domains or revert DNS on the `VPNRouter-TUN` link. Generated full
tunnels steer resolved through the virtual TUN peer with routing domain `~.`;
DHCP DNS on private LAN routes would otherwise bypass the TUN. Exclude-app and
strict-DNS sessions use the same steering. Physical-link DNS is unchanged and
resolved removes the per-link settings when the TUN disappears. A restart reapplies
them. This does not block applications using their own resolvers, and does not
enable the DNS-lockdown capability. Without resolved or this local-session grant,
steering fails without opening an authentication dialog and is logged.

## Package evidence

The manifest verifies file hashes and modes; ELF dependencies are inventoried
with readelf. Managed and native notice evidence is collected offline from the
source, SDK and [notice catalog](notices/catalog.json). Native corresponding
source and linked Cronet notice coverage remain partial; this CI artifact is
not a tagged release and does not claim complete license inventory.
