# Dev-on-host → build-on-host → test-on-separate-machine workflow

Decided 2026-06-18. Supersedes the VirtualBox-centric flow in `README-VM.md`
for day-to-day iteration (that doc stays valid for anyone who *does* want a
self-contained VBox guest).

## Topology

```
┌──────────────────────────────┐                ┌──────────────────────────────┐
│  DEV HOST (this machine)      │   Install ZIP  │  TEST MACHINE (separate)      │
│  Ryzen 9 7945HX3D, 32 GB      │  ───────────▶  │  clean Windows                │
│  C:\Project\VPNRouter         │   WinRM/LAN    │  C:\Program Files\VPNRouter\  │
│  • edit code, git             │                │  • real TUN / firewall / ETW  │
│  • build.ps1 → *-win.zip      │                │  • live VPN smoke test        │
│  • .NET 8 SDK + Go installed  │                │                               │
└──────────────────────────────┘                └──────────────────────────────┘
```

- **No hypervisor on the dev host.** Building locally is fast; the test target
  is a separate physical/virtual box, so the host stays clean and there is no
  VirtualBox/Hyper-V coexistence problem.
- **Only the ZIP crosses the wire** (~48 MB Install ZIP), never the 13 GB repo.

## One-time setup

### Dev host (done 2026-06-18)
- Repo at `C:\Project\VPNRouter` (canonical path — matches `.claude/settings.json`
  hook + `setup-vm.ps1` defaults).
- git hooks active: `git config --local core.hooksPath .githooks`.
- Build verified: `dotnet build` of App/CLI/Service/Tests + `VpnRouterTestMcp` → OK.
- MCP test server built in Release (`.mcp.json` → `vpnrouter-test`).
- TODO (optional): install **PowerShell 7 (pwsh)** — `Setup-Hooks.ps1` and some
  project scripts target pwsh, not Windows PowerShell 5.1.
- TODO (optional): add the Forgejo `origin` remote when on AmneziaWG VPN
  (see `CLAUDE.local.md`); currently only the GitHub remote is configured here.

### Test machine (one-time)
1. Elevated PowerShell:  `Enable-PSRemoting -Force`
2. The deploying account must be a **local Administrator** (VPNRouter needs admin).
3. Add Windows Defender exclusions for `C:\Program Files\VPNRouter` and
   `C:\ProgramData\VPNRouter` (faster, avoids AV quarantine of the TUN bits).
4. On the dev host, if the test machine is **workgroup** (not domain-joined):
   `Set-Item WSMan:\localhost\Client\TrustedHosts -Value "<ip-or-name>" -Concatenate -Force`

## Daily loop

```powershell
# from C:\Project\VPNRouter

# 1. edit code ...

# 2. build + push + relaunch on the test machine (one command)
.\deploy-to-testpc.ps1 -TestHost 192.168.x.y -Build

# first time on a brand-new test box:
.\deploy-to-testpc.ps1 -TestHost 192.168.x.y -Build -FirstInstall

# pull the newest log back after launch:
.\deploy-to-testpc.ps1 -TestHost 192.168.x.y -TailLog
```

`deploy-to-testpc.ps1` (repo root): resolves the version from
`VPNRouter.Core/AppVersion.cs`, optionally runs `build.ps1`, then over WinRM
stops the old process, copies `app\*` into `C:\Program Files\VPNRouter\app`,
and relaunches `VPNRouter.GUI.exe` on the interactive desktop via a transient
scheduled task. It mirrors the project's own
`.claude/skills/post-ship-mcp-verify/scripts/post-ship-install-launch.ps1`.

## Gotchas

- **Interactive launch.** A WinRM session runs in session 0 (no desktop). The
  script uses a one-shot scheduled task so the GUI appears on the logged-in
  user's screen — that user must actually be logged in on the test machine.
  Use `-InteractiveUser` if it differs from the connecting account.
- **VPN-in-VPN.** VPNRouter creates its own TUN adapter on the test machine.
  If that machine reaches the LAN through the host's AmneziaWG, expect routing
  quirks — validate on a plain LAN/NAT path first.
- **Versioning.** `build.ps1` aborts if `-Version` ≠ `AppVersion.cs`. For pure
  functional testing, leave the version as-is (the script reads it for you);
  bump `AppVersion.cs` only when cutting a real `-rN` candidate.
- **Heavy repo (13 GB .git).** Keep it on the host only. If a test machine ever
  needs source, prefer `git clone --depth 1`, not a full clone. A history
  slim-down (BFG / git-filter-repo + LFS) is a separate, coordinated task
  because it rewrites history shared with GitHub + Forgejo.

## Alternative delivery (not chosen, for reference)
- **SMB share**: `build.ps1` output to a shared folder, test machine pulls + installs manually.
- **GitHub Releases**: `build.ps1 -Version X.Y.Z-rN -Upload`, test machine downloads — this is the *production* path (see `ship-rolling-candidate` / `post-ship-mcp-verify` skills), heavier per iteration.
