# Kill-switch path resolution — session-0 / QueryFullProcessImageName hardening (2026-06-27)

Scope: `VPNRouter.Core/Services/FirewallManager.cs` (`ResolveProcessPath`),
new `VPNRouter.Core/Services/ProcessImagePath.cs`, tests
`ProcessImagePathTests.cs` + `FirewallManagerResolveProcessPathTests.cs`.

## Trigger

Report (2026-06-27): with `VPNRouter.CLI.exe` started as **SYSTEM via a
scheduled task** on windows-brat, `FirewallManager.ResolveProcessPath` returned
**null for every routed process** — including a genuinely-running copy of
`waitfor.exe` named `kstest.exe` — so `CreateBlockRules` created **0** block
rules. With `block_on_vpn_fail=true` that means the kill-switch fails **OPEN**
(routed apps egress direct on a sing-box crash) for autostart / Windows-Service
users.

Hypothesis in the report: `Process.GetProcessesByName(name).MainModule.FileName`
fails cross-session when the reader is in session 0 (SYSTEM / the Service) and
the target is in a user session.

## The change

`ResolveProcessPath` step 1 (running-process lookup) moved off
`Process.MainModule.FileName` onto a new `ProcessImagePath` helper using Win32
`QueryFullProcessImageName` + `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)`:

- `MainModule` walks the **target** process's user-mode module list
  (`EnumProcessModulesEx`), needing `PROCESS_QUERY_INFORMATION | PROCESS_VM_READ`.
  It throws / returns null across a **WOW64 bitness boundary** (and can fail for
  protected processes / mid-module-load races).
- `QueryFullProcessImageName` reads the image path from the kernel `EPROCESS`
  object, needs only the lightweight `PROCESS_QUERY_LIMITED_INFORMATION` (0x1000),
  is immune to the bitness mismatch, succeeds cross-session, returns true
  filesystem casing (which the case-sensitive sing-box `process_name` matching
  wants), and is Microsoft's documented replacement for `GetModuleFileNameEx`.

`where.exe` fallback (step 2) is unchanged. The `.exe` strip is `EndsWith(".exe")`
+ `[..^4]` — NOT `Path.GetFileNameWithoutExtension`, which would truncate a
dotted process name (`"My.App.exe" → "My"`); the old FirewallManager code had
that latent bug.

## Empirical findings on windows-brat (Win10 LTSC 2019, x64) — evidence matrix

A standalone probe compared `MainModule.FileName` vs `QueryFullProcessImageName`
for the same target across reader contexts. The real `FirewallManager.CreateBlockRules`
was also exercised via a Core-referencing harness run as a **real SCM service**
(LocalSystem) and a SYSTEM scheduled task.

| Reader context | target | OLD `MainModule` | NEW QFPIN |
|---|---|---|---|
| Interactive x64 (session 1/4) | explorer/kstest (x64) | resolves | resolves |
| WinRM `tester` x64 (session 0) | explorer (session 1) | resolves | resolves |
| **SYSTEM scheduled task** x64 (session 0) | explorer + user-started kstest (session 1) | **resolves** | resolves |
| **REAL SCM SERVICE** x64 (LocalSystem, session 0) | user-started kstest (session 1) | **resolves** | resolves |
| **x86 (32-bit) as SYSTEM** | x64 explorer + kstest | **THROWS `Win32Exception: A 32 bit processes cannot access modules of a 64 bit process` → null → FAIL-OPEN** | resolves |
| x64 as SYSTEM | **32-bit** kstest (SysWOW64 waitfor) | resolves | resolves |

Installed VPNRouter binaries on the VM are all **x64** (CLI/GUI/App/Service).

### Conclusion

- The **session-0 hypothesis did NOT reproduce** for the shipped **x64** binary:
  LocalSystem in session 0 (real service AND scheduled task) reads `MainModule`
  of a user-session process **successfully**. Could not reproduce the
  "null for every routed process" with a faithful x64 replica of the pre-fix
  code, in the exact contexts described.
- The **one reproduced `MainModule` failure** is the asymmetric WOW64 case:
  a **32-bit reader → 64-bit target** (`ERROR_PARTIAL_COPY`). QFPIN resolves it.
  This does **not** apply to the shipped x64 VPNRouter (x64→x64 and x64→x86 both
  resolve via `MainModule`).

So the originally-reported fail-OPEN trigger remains **unexplained** for the x64
binary. Candidates not yet ruled out (need the reporter's specifics): (a) the
routed app not actually running at `CreateBlockRules` time (connect-time skip is
*expected*; `EnableBlockRules` re-resolves on crash — r10 #1); (b) a 32-bit
VPNRouter build in their harness; (c) AV/EDR/GPO on that specific run;
(d) a protected/AppContainer target.

## Why ship the change anyway

It is a **strict hardening** with no regression risk:
- Resolves in a **superset** of the old `MainModule` cases (proven: x86→x64,
  which `MainModule` cannot do at all) — the kill-switch arms rules in *more*
  cases, never fewer.
- Exception-safe on every path into VPN-start and the crash handler; closes the
  native HANDLE and disposes the `Process[]` in `finally` (AU-9 pattern).
- Improves the `EnableBlockRules` crash-path re-resolution the same way.
- Returns correct filesystem casing for `process_name` matching.

## Verification

- `dotnet build VPNRouter.sln -c Release` → 0 errors.
- New tests green (79 incl. existing FirewallManager suites): `ProcessImagePathTests`
  (in-process QFPIN == MainModule ground-truth, invalid-PID null-safety, dotted-name
  `.exe` strip), `FirewallManagerResolveProcessPathTests` (running process →
  `netsh add rule program=<existing file>`; non-running → skipped, no throw).
- Independent review-agent over the diff: SHIP (P/Invoke `lpdwSize` excl-null
  out-semantics + `ToString(0,capacity)` slice verified against MS docs).
- Live windows-brat gate: the evidence matrix above (probe + real-SCM-service
  harness). VM artifacts cleaned up (0 leftover firewall rules / temp files).

## Open / follow-up

- Get the reporter's exact repro (VPNRouter version + command, whether kstest was
  running BEFORE connect, reader bitness, AV/EDR/GPO) to pin the actual x64
  trigger. Until then this is a hardening, not a confirmed-defect fix.
- `ConflictingVpnDetector.cs:143` uses the same `MainModule` pattern (UI banner,
  GUI context — lower severity); could adopt `ProcessImagePath` for consistency.
