# Russian IP bypass - 2026-10-19

## Why

The owner reports Russian sites still using VPN with the bypass enabled.
At accepted base `7af9aaeb4b6d9f74ec2427ff164807b61db44332`,
`ConfigGenerator.ApplyGeoBypass` registers both Russian rule sets but routes
only `vpnrouter-geosite-ru` directly. `CustomConfigInjector` already uses both.
The generated current.json on WINBRAT also has the domain-only rule.
This confirms a product defect, not yet the cause of the owner's exact site.

## What and how

Add the already downloaded `vpnrouter-geoip-ru` to the generated direct rule.
Pin both tags, insertion precedence, enabled/disabled behavior and preserved
proxy DNS across full, split include and split exclude modes. Compare the custom
injection path. Do not change DNS privacy, custom-rule priority, settings, UI,
geo downloads, release versions or the owner's working checkout/index.

## Risk and rollback

Russian IPs now take the documented direct route even for non-Russian domains.
Shared infrastructure hosted on those IPs is included by design. Revert the
scoped fix commit to restore previous behavior. Existing custom-first rules keep
their precedence. No new dependency or data download is needed for the fix.

## Verification and gates

1. Build PASS: exact-SHA Windows Release solution build, 0 errors (230 existing
   warnings). Exact-head GitHub Ubuntu/Windows checks and Android compile green.
2. Tests PASS: focused 72/72; full Windows 3457 passed, 19 skipped, 0 failed.
   Negative control reverting only the fix: 3 enabled cases fail, 3 disabled pass.
3. Documentation PASS: brief and defect ledger; no release facts changed.
4. Review PASS within scope: separate-context Sol reviewer found no supported
   defect. Same-model-family review, not cross-family independence. Added an
   explicit assertion that split app routing follows bypass after review.
5. Consumer PASS within scope: real installed sing-box and real geo files on
   WINBRAT load the generated rule and select the expected outbound (see below).
   Owner's exact site/device and desktop TUN end-to-end scenario remain unverified.
6. Integration PASS: generated six mode/toggle cases inspect serialized routing;
   existing custom injection tests pass. No UI change or mechanical refactor.

## Environment evidence

WINBRAT SSH identity confirmed. Preflight 2026-10-19: 8 logical CPUs, 0% CPU,
13.49 GiB free RAM of 16 GiB, C: 119.90 GiB free, no dotnet/msbuild/testhost or
VPNRouter.App/sing-box processes. Private SDK 10.0.301 available.
The development workstation has no .NET SDK; builds run on WINBRAT/CI.
Original checkout remains on main with 18 pre-existing instruction-file edits.
A separate task worktree preserves those edits and its index.

## Outcome

Implementation `c45beb19c32b7003d34694c9e5debc9f22853077` adds one tag in
`ConfigGenerator.Rules.cs`; `ConfigGeneratorTests` exercises full, split/include,
split/exclude with bypass on/off, serialized tags, precedence and proxy DNS.
PR: https://github.com/PavelLizunov/VPNRouter/pull/604. The checkpoint branch is
`dsh/ru-ip-bypass-20261019`; original main/index and pre-existing edits preserved.

Exact implementation-head CI: all five checks green, including Android compile,
Ubuntu tests, Windows tests, Go tests and placeholder scan. Run IDs:
39308303859 (tests), 39308303830 (Android), 39308303813 (scan).
PowerShell is unavailable locally; exact-head GitHub PR checks were used instead
of tools/verify-last-commit-ci.ps1.

A task-owned temporary .NET console on WINBRAT referenced Core at that exact SHA
and called Generate/Serialize, using copies of the installed real geo files.
Only the generated TUN inbound was replaced with a loopback SOCKS inbound, the
outbound endpoint with an unreachable loopback SOCKS endpoint, and experimental
controller/log output with isolated values. Route and DNS stayed generated.
Four bounded installed-core runs confirmed actual outbound selection in logs:

- bypass on, 77.88.55.88: outbound/direct[direct];
- bypass on, 1.1.1.1: outbound/socks[proxy];
- bypass off, 77.88.55.88: outbound/socks[proxy];
- bypass off, 1.1.1.1: outbound/socks[proxy].

Each exact task-owned core process was killed/disposed in finally. This proves
real IP rule matching and outbound selection, not successful remote VPN egress
or the owner's browser/TUN behavior. The installed app/config were not changed.
No merge, release, or user-device installation was performed. The owner's exact
site/device is still unknown. Source repair is not an installed fix.

Worker checkout, generated screenshots, temporary console, copied geo files,
logs and invocation scripts are task-owned and removed after evidence capture.
Local log excerpts and this outcome preserve the executed check results.
