# Linux .deb protected sing-box runtime (UX audit F-02)

## Intent

Stop the desktop `.deb` from copying its capability-bearing sing-box into
`~/.config/vpnrouter/bin/` and launching the capability-less copy. Use the
package-owned `/opt/vpnrouter/sing-box` directly.

## Scope

- `VPNRouter.Core/AppPaths.cs` runtime path selection.
- `VPNRouter.Core/Platform/Linux/LinuxDesktopPackage.cs` layout and owner checks.
- The existing deployment check skips the copy when the selected runtime is
  the bundled file. Process ownership still requires the existing owner record
  for executable paths outside the user's runtime directory.
- Narrow tests and the Linux packaging README.
- Package installation and self-update verify the same capability set and
  report errors. The legacy updater reapplies capabilities only to the trusted
  package runtime. Existing capability grants are unchanged.
- The package scripts verify root ownership, file type and permissions before
  privileged operations on the runtime. The inline updater uses absolute
  program paths. The release build inspects the assembled Debian archive.
- Excludes Omarchy UI, DNSTT, macOS, deployment and builds.

## Invariants

- A runtime path is selected only when the app base directory is exactly
  `/opt/vpnrouter`, every path component is root-owned and not group/other
  writable, and the file is a regular non-symlink file.
- A writable, user-owned, symlinked or otherwise unverified installation
  path never selects the package runtime.
- Portable tar.gz/AppImage behavior keeps copying into the user bin directory.
- The existing Headless `SingBoxRuntimePolicy` path is unchanged and still wins.

## Verification

- `LinuxDebRuntimeTests` covers exact-layout selection, writable/symlink
  rejection, and the post-install path contract.
- Existing `PackagedRuntimePolicyTests` covers the root-owned metadata predicate.
- Exact `133305cb` passed the PR checks. DeepSeek found the existing ownership
  monitor cannot identify some capable Linux children through `/proc/PID/exe`.
  Durable ownership and orphan recovery need a separate exact-child check.
- Exact `80eccbb7` passed nine PR checks. The subsequent DeepSeek review
  found unqualified privileged commands and insufficient target validation
  in the package scripts.
- An offline shell fixture with inert files and a stub for `setcap` verified
  that both scripts accept a protected file and skip capability operations
  for symlinks, writable files, and writable parent directories.
- Build, full tests and live `.deb` acceptance must run on an authorized worker
  or CI; no local build was performed for this isolated task.

## Acceptance

On a clean Ubuntu host run `sudo apt install ./VPNRouter-v{version}-linux-amd64.deb`, confirm `getcap
/opt/vpnrouter/sing-box` includes effective `cap_net_admin`, launch
the app, and connect without a polkit password prompt. The running process must
be `/opt/vpnrouter/sing-box`; no `~/.config/vpnrouter/bin/sing-box` copy may be
used. A portable tar.gz run must still deploy to the user directory and use the
existing pkexec fallback.

Verify the same APT command as the first upgrade from an older package when
`libcap2-bin` is absent. Verify runtime status and orphan handling with a
non-dumpable capable child, normal and legacy in-app upgrades, and a custom config with an
interface binding. The current capability set remains scoped to the existing
.deb behavior until the custom-config acceptance is verified.
