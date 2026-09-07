# Windows preview runtime progress

## Exact snapshots
- PR #247 branch dsh/desktop-pictogram-preview.
- UI commit 1adb285b; corrective test commit a36b609e (all four GitHub checks passed, run34159927504/34159927506).
- Worker task checkout C:\Temp\vpnrouter-pictograms-20260907; SDK10.0.302. Existing C:\Temp\vpnrouter is not a usable tracked clone (all files untracked/no origin), left untouched.

## Evidence and remaining work
- Initial exact Windows compile and seven targeted UI tests passed at1adb285b.
- Independent screenshot review found dark/light identical; VM constructor resets theme. a36b609e moves theme after VM and asserts actual app/resources. Corrected Windows run at a36b609e passed8/8; actual theme/resource assertions pass and four dark/light PNG pairs differ. Captures in /tmp/vpnrouter-pictogram-corrected-evidence independently reviewed PASS for stopped/default tg+zapret at360/520 both themes; no claim all states/pages. Initial retry blocked by abandoned test child, NOT compiler errors.
- Full Windows suite at1adb285b stalled without test progress. Stopped only exact task-owned testhost4268 and remaining VPNRouter.Tests.exe6248 after verifying executable paths; dotnet3548 absent before retry. Do not claim full Windows PASS. Next full run needs bounded hang diagnostics and entire child cleanup.
- Generated page screenshots changed three tracked PNGs on task worker; they are test outputs, not user changes, no production code modifications on worker.
- Deployment not performed. No config backup/autostart changes or app launch yet.
- Current canonical brat WinRM requires old DPAPI cache; no cache in inspected local/Windows checkout. Existing-identity WinRM to fixed IP failed ServerNotTrusted. Do not alter trusted-hosts/credentials or bypass fixed verifier silently.
- Historic remote C:\r4review\release-2.48.0-r1\deploy-release.ps1 is local replacement script (commands Stop-Process/Copy-Item/Expand-Archive; no WinRM/credential/brat). Does not prove authorized current route. User correctly notes SSH access remains functional; do not repeatedly ask passwords.

## New core announced by owner
Release v1.14.0-vpnctl.5 from PavelLizunov/sing-box-vpnctl downloaded Windows x64 archive to /tmp/vpnrouter-core-vpnctl5 and task worker, not installed.
SHA256 archive: 3823e4baed13fec43b84acefa480ff9cf9b2c222ea9dd9ceb9987aefd623aeb4 (published sidecar matched).
Worker version command:1.14.0-vpnctl.5, revision8688eab3c51d07ff89a124ce247044409b9f93fd, go1.26.7/windows-amd64, CGO disabled. Compatibility/config checks pending. No tunnel launched.

## Uncommitted scope
Deployment script cleanup/unique-task/exact path+SID+session fixes and source tests independently reviewed; backup/autostart mode NOT implemented. Findings ledger and approved full-install plan remain uncommitted. Keep separate from green UI block until tested. Owner authorized full test installation, no release/merge.
