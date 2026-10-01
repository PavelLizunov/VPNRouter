# P-1: post-ship verification on the verification VM itself

## Why

Reports for r10 to r13 said the post-ship gate "needs the owner's offline PC". The owner pointed out that the workers are
reachable and lifted the earlier ban on touching WINBRAT's VPN (2026-10-01). What really blocked an agent: the verifier
(`tools/post-ship-verify.ps1`) is written for a maintainer PC, it reaches WINBRAT over WinRM with a credential file
(`.testpc-cred-192.168.0.106.xml`) that only exists there, it needs a local .NET SDK for the screenshot gate, and over ssh a
local-admin loopback `New-PSSession` is refused (token filtering).

## What

- `tools/brat-local-shim.ps1`: functions that shadow `New-PSSession`, `Remove-PSSession`, `Invoke-Command -Session` and
  `Copy-Item -ToSession`, so the unchanged `brat-verify.ps1`, `brat-stability.ps1` and `deploy-to-testpc.ps1` run on the VM.
- `tools/post-ship-local.ps1 -Version X.Y.Z-rN`: tag commit checkout, published zip download and hash check, identity,
  clean deploy, N cold UI connect cycles, lifecycle, final state; transcript in `post-<version>.log`.
- `tools/brat-verify.ps1` gets a UTF-8 BOM: its UI-automation helper text holds Cyrillic literals and Windows PowerShell
  5.1 reads a file without a BOM as ANSI (the helper then failed to parse: "Interactive helper exited without writing a
  result JSON"). Older tags get the BOM on the checkout copy by the script.
- Skill, tools zone and worker docs describe the local variant and what it does not cover.

## Not covered

The screenshot gate (6 known failing tests), the strict commit CI gate and the release inventory/hash checks of the
other platforms: they stay separate steps in the report.

## Verification

First run for `v2.50.0-r13` on the `windows-worker` (2026-10-01, CPU and RAM idle, nothing else running there): identity
verified, clean deploy of the published zip, two cold cycles through the UI (Connect then Disconnect invoked by UI
automation), core started twice, TunReady twice, no health failure, restart or failover events; `Status: PASS`; final
state one GUI, no core, TUN absent, direct route. Two earlier attempts failed on the shim (a Copy-Item parameter set) and
on the missing BOM; both are fixed here.

## Outcome

Pending (second run from the merged script, then merge).
