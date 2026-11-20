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
  paths for privileged commands. The release build inspects the assembled
  Debian archive.
- The package updater rejects links and special files throughout the payload
  and requires a protected installation tree. The installed helper validates
  a private root-owned copy before it updates the installation. The inline
  compatibility path checks the source and destination and replaces file
  entries rather than writing through them.
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
- Exact `d14be80d` passed all nine PR checks. The Debian package build and
  installed-runtime checks are separate gates. Independent review confirmed
  the path guards and recommended extending the checks to the full update
  tree, correcting the source-rejection message, and documenting the
  `/usr/local` directory requirement.
- The follow-up offline helper fixture passed nested source and destination
  symlink rejection, FIFO rejection, writable-directory rejection, update
  with a valid payload, and private staging cleanup. The fixture ran without
  elevation or real capability changes.
- Exact `40121a45` passed all nine PR checks. Its review found that updates
  could copy private `0600` files as root and leave the relaunched desktop
  application unable to read them. Normalize package and update modes, keep
  executable bits, and check installed readability before declaring success.
- The committed unprivileged helper fixture exercises `umask 077` with a
  private source tree. It verifies `0644` data files, `0755` directories
  and executables, link rejection, staged cleanup and stubbed capability
  verification. Both fixture cases passed locally and run in PR CI.
- Exact `f7d5c26e` passed all nine PR checks and independent review. The
  follow-up uses explicit executable modes and checks existing readability
  before the privileged copy. A package reinstall can recover an old tree
  with restrictive modes. All four unprivileged helper tests pass locally.
- The legacy inline copy still takes a same-user staging tree and replaces
  individual files. Root-side verification of payload authenticity and
  whole-install rollback are separate work.
- Reviewed the separate ownership prototype `9633bdab`. Its receipt fallback
  reached the signal, cleanup and update paths even when the executable could
  not be independently identified. The prototype stays separate. Use the
  owning process handle for capability-mode stop and keep external signaling
  dependent on a live image check. Any receipt-based status evidence must
  remain display-only and scoped to the protected desktop package.
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
