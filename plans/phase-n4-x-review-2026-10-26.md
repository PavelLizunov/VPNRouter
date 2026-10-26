# N4-X independent delta review

## Why and scope

Review backend 2cc9e7e7..4ad09f87 and plugin 1246ac4..b5eaee1, then fix
confirmed P1/P2 issues in the owner-selected lifetime worktrees. No release,
tag, merge, owner workstation deployment or shared unrelated VM changes.

## Confirmed findings before implementation

- P1: Program constructs Core before UnixSocketServer acquires the data lock.
  Core construction and disposal restore the DNS sentinel, so a competing daemon
  or stdio diagnostic can revert a running service's DNS before refusal.
- P2: migration instructions omit stopping the legacy shell-owned stdio helper
  before pacman replaces assemblies. Service masking cannot stop that helper.
- P2: only active services are masked during upgrade; an inactive service can
  start via panel retries while assemblies are replaced. Mask all user managers
  and preserve whether each was active, including retry and old empty markers.
- P3: the lifetime design note still describes the unit as independent of
  graphical-session.target; the shipped unit is PartOf that target.

## Implementation and invariants

Acquire daemon locks before constructing the handler. Refuse stdio access to
a service-owned data directory before constructing Core; keep first-launch
read-only snapshots free of persistent writes. Document the explicit legacy
plugin shutdown/update/start sequence and use attach for service diagnostics.
Keep peer permissions, operation admission, EOF semantics and platform routing.

## Verification and rollback

Backend .NET builds and regression checks run only in exact-SHA GitHub CI.
Run package Python and plugin Node/packaging checks; record QML/validate results.
Under the VM lock install the final artifact/plugin, cold restart shell, connect
HY2, rescan, inspect a connected screenshot and disconnect; inspect TUN/rules/DNS.
Review security and platform changes directly; lead reviews new patches.
Rollback restores the previous package and plugin after disabling the plugin
and stopping its backend. Other-laptop and logout behavior remain unverified.

## Gates / outcome

Packaging: 131/131 passed; four upgrade expectations fail on the old hook.
Plugin: 13 Node suites and 42 isolated packaging cases passed; docs-only plugin
78ab311 has green CI 40252590875. Source/diff review performed directly.
Backend .NET/Arch CI and final VM consumer gate: pending, recorded in external X.md.
The first plugin packaging run saw installed-host backend/processes; the isolated
PID/network/filesystem run passed without touching that backend.
Windows/macOS live and graphical logout: N/A to this bounded VM round.

## Shared desktop Linux follow-up

Source review confirmed that dns_mode=disabled was emitted for every Linux
frontend, but packaging/linux/postinst does not ship the Arch Headless resolved
grant. Preserve native DNS for desktop/CLI and restrict the automatic managed
resolver replacement to the existing Headless runtime-policy context. Explicit
DnsLeakLockdown behavior remains available. Add a generated JSON regression for
non-Headless context and scope the original Headless test explicitly.

## Runtime mask ownership follow-up

The inactive-service repair must preserve a caller's pre-existing runtime mask.
Skip masked-runtime units only when no transaction marker already records our
own interrupted upgrade. This retains recovery for old empty markers while
avoiding unmasking caller intent. Add a transaction regression against b49ba27a.

## Verification outcome

Backend b295a001 has green Arch 40256016210 (r51) and all six jobs in
40256018110. Packaging is 132/132; the caller-mask regression is RED against
b49ba27a. Plugin 3bb9b30 has green 40253908948. Fresh Node (13 suites), isolated
packaging (42), QML (6 EN/RU cases) and native plugin validation passed.
The first qmllint invocation lacked the virtual qs import root; its exit 0 was
not accepted as full import validation. A private qs import alias resolved that
namespace: exit 0, zero failed imports, 535 remaining dynamic-type warnings.

Live r49 covered legacy r33 migration, DNS-sentinel refusal, connected rescan/
cold restart, active reinstall and active uninstall. Live r50 preserved service/
core through rescan and restart; connected MTU 1280/1420 both retained DNS peer
and routing domain, generated tun/dns_mode=disabled, and HTTP 200. Disconnect
left no TUN or current/legacy rules. Exact final package, loaded-source proof,
caller-mask installation and cleanup receipts are recorded in the owner-requested
external X.md / X-evidence. Another laptop, desktop Linux GUI, macOS live, IPv6
data plane, PMTU blackhole, logout/suspend and hung-stop remain unverified.
Lead Claude owns independent acceptance of the new patches; no release or merge.
