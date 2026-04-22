# VPNRouter Development VM

Isolated Windows 11 VirtualBox guest for building, testing, and releasing
VPNRouter without touching the host OS.

## Why a VM

- **Isolation** — sing-box TUN adapter, WinTUN driver, firewall rules, and
  process routing stay inside the guest.
- **Reproducibility** — snapshot a known-good state, revert in seconds.
- **Safe to break** — experiment with networking, kill the stack, without
  consequences for the host.

## VM specs

| Resource | Minimum | Recommended |
|---|---|---|
| OS | Windows 10/11 x64 | Windows 11 Pro |
| RAM | 4 GB | 6-8 GB |
| Disk | 40 GB | 60 GB |
| CPU | 2 cores, VT-x/AMD-V enabled | 4 cores |
| Network | NAT or Bridged | NAT through host VPN |

### VirtualBox host settings

```powershell
# Enable nested virtualization (needed if the host uses Hyper-V / WSL2)
VBoxManage modifyvm "win11" --nested-hw-virt on

# Enable shared clipboard + drag-and-drop
# (Guest Additions must be installed inside the guest first - see below)
VBoxManage modifyvm "win11" --clipboard-mode bidirectional
VBoxManage modifyvm "win11" --draganddrop bidirectional
```

### Guest Additions (required for clipboard and shared folders)

1. On the host, in the VirtualBox window menu:
   **Devices -> Insert Guest Additions CD image…**
2. Inside the Windows guest, open This PC -> the new CD drive.
3. Run `VBoxWindowsAdditions-amd64.exe` as Administrator.
4. Reboot the guest.
5. Verify from the host:
   ```powershell
   VBoxManage showvminfo "win11" --machinereadable | `
     Select-String -Pattern 'GuestAdditionsRunLevel|clipboard'
   ```
   Expect `GuestAdditionsRunLevel=3` and `clipboard="bidirectional"`.

## What to copy from the host

| Item | Host path | Guest path | Required? |
|---|---|---|---|
| Forgejo SSH key | `~\.ssh\id_ed25519` + `.pub` | `~\.ssh\` | Only for Forgejo push |
| `known_hosts` | `~\.ssh\known_hosts` | `~\.ssh\known_hosts` | Recommended |
| GitHub CLI token | output of `gh auth token` | paste into `gh auth login --with-token` | For release uploads |
| VLESS subscription URL | from running app | paste in GUI at first start | For smoke tests |

Claude Code project memory (`~\.claude\projects\…\memory\`) is optional —
the repo already contains `CLAUDE.md` and `CLAUDE.local.md` with everything
needed for onboarding.

## Setup inside the guest

### Option A — one-shot script (recommended)

1. Open PowerShell as Administrator.
2. Pull and run the setup script:

   ```powershell
   Invoke-WebRequest `
     -Uri "https://raw.githubusercontent.com/PavelLizunov/VPNRouter/main/setup-vm.ps1" `
     -OutFile "$env:TEMP\setup-vm.ps1"
   powershell -ExecutionPolicy Bypass -File "$env:TEMP\setup-vm.ps1"
   ```

The script:

- installs `.NET 8 SDK`, `Go`, `Git`, `GitHub CLI`, `Claude Code`, `7-Zip`
  via `winget`
- adds Windows Defender exclusions for `C:\Project\VPNRouter\` and
  `C:\ProgramData\VPNRouter\`
- clones the repo to `C:\Project\VPNRouter`
- optionally sets global `git user.name` / `user.email`
- verifies GitHub + Forgejo TCP reachability
- runs `dotnet restore` and `dotnet build`

Useful parameters:

```powershell
.\setup-vm.ps1 `
  -RepoDir "C:\Project\VPNRouter" `
  -GitUser "Your Name" `
  -GitEmail "you@example.com" `
  -SkipBuild
```

If `winget install` finishes but `dotnet` or `git` are still not on `PATH`,
close the PowerShell window, open a **new** elevated one, and re-run with
`-SkipWinget -SkipDefender`.

### Option B — manual

```powershell
# Run as Administrator
winget install Microsoft.DotNet.SDK.8 --silent --accept-source-agreements --accept-package-agreements
winget install GoLang.Go              --silent --accept-source-agreements --accept-package-agreements
winget install Git.Git                --silent --accept-source-agreements --accept-package-agreements
winget install GitHub.cli             --silent --accept-source-agreements --accept-package-agreements
winget install Anthropic.Claude       --silent --accept-source-agreements --accept-package-agreements
winget install 7zip.7zip              --silent --accept-source-agreements --accept-package-agreements

Add-MpPreference -ExclusionPath "C:\Project\VPNRouter"
Add-MpPreference -ExclusionPath "C:\ProgramData\VPNRouter"

New-Item -ItemType Directory "C:\Project" -Force | Out-Null
git clone https://github.com/PavelLizunov/VPNRouter.git C:\Project\VPNRouter
cd C:\Project\VPNRouter
dotnet restore VPNRouter.sln
dotnet build VPNRouter.sln --configuration Release
```

## Post-setup

### GitHub CLI authentication

```powershell
gh auth login
# or, non-interactive:
gh auth login --with-token < token.txt
```

### Forgejo push over AmneziaWG

The self-hosted Forgejo mirror lives at
`ssh://git@10.9.1.1:18222/slovn/vpnrouter.git` and is reachable only over
AmneziaWG. Two options:

- **Host VPN + guest NAT (recommended).** AmneziaWG runs on the host; the
  guest uses NAT and its outbound traffic is wrapped by the host VPN
  automatically. Nothing extra to install inside the guest.
- **VPN inside the guest.** Install
  [AmneziaVPN](https://amnezia.org) in the guest and import a dedicated
  peer config. Only do this if the host cannot run the VPN.

Verification from inside the guest:

```powershell
ssh -T git@10.9.1.1 -p 18222
# Expected: "Hi there, slovn!"
```

Two remotes are already configured in the repo (see `git remote -v`):

| Name | URL | Purpose |
|---|---|---|
| `origin` | `ssh://git@10.9.1.1:18222/slovn/vpnrouter.git` | Forgejo (private, VPN) |
| `github` | `https://github.com/PavelLizunov/VPNRouter.git` | Public mirror |

The `main` branch should be pushed to both after each change
(see `CLAUDE.local.md`).

### First build

```powershell
cd C:\Project\VPNRouter
.\build.ps1 -Version "test"
# -> 3 zip artifacts in .\publish\
```

Full release upload (requires `gh auth login` first):

```powershell
.\build.ps1 -Version "1.24.6" -Upload
```

The pre-built `tools\sing-box.exe` (~24 MB, custom build with
`with_utls,with_clash_api,with_quic` tags) ships with the repo. Rebuild it
only if you need a different sing-box version:

```powershell
.\build-singbox.ps1 -Version "1.13.3" -Install
```

## Gotchas

- **UAC elevation.** `VPNRouter.App.exe` needs admin for TUN, ETW, and
  firewall rules. Right-click -> Properties -> Compatibility ->
  "Run as administrator" to stop the UAC prompt from interrupting each
  test run.
- **WinTUN driver.** sing-box installs it on first use. If that fails,
  download from <https://www.wintun.net/> and register manually:
  `rundll32 wintun.dll,RunDll32 install`.
- **Defender real-time scan.** Significantly slows `dotnet build`. The
  setup script adds exclusions; if you skipped that step, add them by
  hand.
- **Forgejo push fails.** Check, in order:
  1. AmneziaWG is actually connected on the host (or inside the guest,
     if you went that route).
  2. `ssh -T git@10.9.1.1 -p 18222` succeeds from the guest.
  3. Key permissions. Windows OpenSSH ignores POSIX bits but does check
     ACL ownership — if `ssh` complains, fix with:
     ```powershell
     icacls $HOME\.ssh\id_ed25519 /inheritance:r `
       /grant:r "$($env:USERNAME):F"
     ```
- **PATH after winget.** Newly installed tools appear on `PATH` only for
  processes started *after* winget finishes. If `dotnet` / `git` aren't
  found, open a fresh elevated PowerShell and re-run with
  `-SkipWinget -SkipDefender`.
- **sing-box process_name matching is case-sensitive.** Covered in
  `CLAUDE.md`. Not something you'll hit during setup, but worth knowing
  if you edit `ConfigGenerator.cs` / `ProcessScanner.cs`.

## See also

- `CLAUDE.md` — codebase architecture and design decisions
- `CLAUDE.local.md` — private notes (VPN access, release workflow)
- `README.md` — end-user documentation
- `build.ps1` — release build pipeline
- `build-singbox.ps1` — custom sing-box rebuild
