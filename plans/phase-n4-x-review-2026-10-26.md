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

Build, regression, docs, source review and VM consumer gates: pending.
Windows/macOS live and graphical logout: N/A to this bounded VM round.
