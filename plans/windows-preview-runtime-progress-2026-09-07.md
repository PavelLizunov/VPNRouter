# Windows preview runtime progress

## Final visible-window evidence
- Interactive Highest tester/session1 task ran read-only EnumWindows probe for exact PID6324. Result: Success=true, ProcessIdentity=true, InteractiveTester=true, WindowFound=true, Visible=true, GenericTitle=true, AvaloniaClass=true. No screenshots or window contents captured. Verification task removed after receipt.
- Application left open for owner at WINBRAT Tailscale100.115.182.0. No release/tag/merge. Headless UI tests8/8, desktop regressions33/33 and e6b85a1a CI passed previously; full local Windows suite remains uncompleted as documented below.
- Activation's transient-process guard did NOT pass; subsequent exact process/config/version and interactive-window verification establishes installed preview state, not a universal pass of the deployment script. Preserve that limitation.

## Latest authoritative state (round 3)
- Installed e6b85a1a App/Core/Service product revisions independently verified equal exact SHA. Old app retained at C:\Program Files\VPNRouter\.app-before-pictograms-20260907-392ffe3d184440f7a4558dfb28cad913; protected backup path below retained.
- Actual config equals protected config.preview.yaml byte hash; all three startup settings disabled by reviewed helper. Actual process PID6324 exact installed App path, tester SID, session1; no network tool processes observed. No temporary launch tasks remain.
- Activation script reported failure during transient process count guard (line215), AFTER swap and launch; do not rerun install. Postlaunch no rollback performed intentionally. SSH MainWindowHandle absent; interactive-session window verification pending. Never claim visible yet.
- YAML issue fixed with unrelated aliases preserved and shared target/cycles rejected; synthetic red-green and actual protected output passed, independent review accepted helper fb6f42cb. File.Replace null reproduced synthetic ArgumentException; explicit backup succeeded. Earlier real failed swap rolled back completely; retry used explicit backup and installed successfully.
- Current activation script hash56717bf2 reviewed. Window verifier being prepared. No release/merge, no tunnel launch.

## Exact snapshots
- PR #247 branch dsh/desktop-pictogram-preview.
- UI commit 1adb285b; corrective test commit a36b609e (all four GitHub checks passed, run34159927504/34159927506).
- Worker task checkout C:\Temp\vpnrouter-pictograms-20260907; SDK10.0.302. Existing C:\Temp\vpnrouter is not a usable tracked clone (all files untracked/no origin), left untouched.

## Evidence and remaining work
- Initial exact Windows compile and seven targeted UI tests passed at1adb285b.
- Independent screenshot review found dark/light identical; VM constructor resets theme. a36b609e moves theme after VM and asserts actual app/resources. Corrected Windows run at a36b609e passed8/8; actual theme/resource assertions pass and four dark/light PNG pairs differ. Captures in /tmp/vpnrouter-pictogram-corrected-evidence independently reviewed PASS for stopped/default tg+zapret at360/520 both themes; no claim all states/pages. Initial retry blocked by abandoned test child, NOT compiler errors.
- Full Windows suite at1adb285b stalled without test progress. Stopped only exact task-owned testhost4268 and remaining VPNRouter.Tests.exe6248 after verifying executable paths; dotnet3548 absent before retry. Do not claim full Windows PASS. Next full run needs bounded hang diagnostics and entire child cleanup.
- Generated page screenshots changed three tracked PNGs on task worker; they are test outputs, not user changes, no production code modifications on worker.
- Final activation not performed; installed app/config unchanged, no app launch. Stage succeeded with 308 checksum-verified files: C:\Program Files\VPNRouter\.app-stage-pictograms-20260907-392ffe3d184440f7a4558dfb28cad913. Protected verified config backup: C:\ProgramData\VPNRouter-pictograms-20260907-backup-392ffe3d184440f7a4558dfb28cad913. Exact source App/CLI/Service e6b85a1a; canonical App->CLI->Service overlay matches build.ps1, six exact Extensions DLL mismatches allowed. Core archive verified. a3968c4f CI all PASS. Owner additionally authorized process-local ExecutionPolicy Bypass and all necessary scoped installation actions, no permanent policy changes.
- Helper baseline self-test passed; actual config transform rejected before output (AnchorOrTag). Diagnostic-only Program.cs local/worker edits uncommitted; worker source transferred explicitly. Need safely determine YAML construct, preserve unrelated bytes and disable only three startup keys; no secrets printed. Final activation script not created: subagent failed before work. Original deployment-script uncommitted edits remain separate. CLI initial OOM retried successfully with m:1/no shared compilation; no unrelated Rust processes touched. All relevant build/CI jobs collected through bash-88.
- Owner explicitly authorized existing SSH instead of unavailable WinRM for this ONE test installation, preserving machine checks/backups/autostart disabling and without credential/TrustedHosts changes (round2). Machine WINBRAT elevated tester, console1 Active; no VPNRouter service or app/core/tool processes observed. Self-contained App publish at e6b85a1a succeeded; matching Service/CLI publish required to avoid InstallHealthCheck mixed-commit self-repair. Stage-only and output-only YAML helper under review; no installer may run yet.
- Current canonical brat WinRM requires old DPAPI cache; no cache in inspected local/Windows checkout. Existing-identity WinRM to fixed IP failed ServerNotTrusted. Do not alter trusted-hosts/credentials or bypass fixed verifier silently.
- Historic remote C:\r4review\release-2.48.0-r1\deploy-release.ps1 is local replacement script (commands Stop-Process/Copy-Item/Expand-Archive; no WinRM/credential/brat). Does not prove authorized current route. User correctly notes SSH access remains functional; do not repeatedly ask passwords.

## New core announced by owner
Release v1.14.0-vpnctl.5 from PavelLizunov/sing-box-vpnctl downloaded Windows x64 archive to /tmp/vpnrouter-core-vpnctl5 and task worker, not installed.
SHA256 archive: 3823e4baed13fec43b84acefa480ff9cf9b2c222ea9dd9ceb9987aefd623aeb4 (published sidecar matched).
Worker version command:1.14.0-vpnctl.5, revision8688eab3c51d07ff89a124ce247044409b9f93fd, go1.26.7/windows-amd64, CGO disabled. Compatibility/config checks pending. No tunnel launched.

## Uncommitted scope
Deployment script cleanup/unique-task/exact path+SID+session fixes and source tests independently reviewed; backup/autostart mode NOT implemented. Findings ledger and approved full-install plan remain uncommitted. Keep separate from green UI block until tested. Owner authorized full test installation, no release/merge.
