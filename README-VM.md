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

Agent session memory is optional — the repo already contains `AGENTS.md`,
`AGENTS.local.md`, and `docs/agent-contract.md` with everything needed for
onboarding.

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

- bootstraps [Chocolatey](https://chocolatey.org/) if it's not already
  installed (works on all Windows editions, including Enterprise LTSC,
  where Microsoft Store / `winget` are absent)
- installs `.NET 10 SDK`, `Go`, `Git`, `GitHub CLI`, `7-Zip` via `choco`
- adds Windows Defender exclusions for `C:\Project\VPNRouter\` and
  `C:\ProgramData\VPNRouter\`
- clones the repo to `C:\Project\VPNRouter`
- optionally sets global `git user.name` / `user.email`
- verifies GitHub + Forgejo TCP reachability
- runs `dotnet restore` and `dotnet build`

Agent harnesses are not installed automatically inside the VM — the harness is
used from the host or configured per environment instructions.

Useful parameters:

```powershell
.\setup-vm.ps1 `
  -RepoDir "C:\Project\VPNRouter" `
  -GitUser "Your Name" `
  -GitEmail "you@example.com" `
  -SkipBuild
```

If the package manager bootstrap finishes but `dotnet` or `git` are
still not on `PATH`, close the PowerShell window, open a **new**
elevated one, and re-run with `-SkipInstall -SkipDefender`.

### Option B — manual (Chocolatey)

```powershell
# Run as Administrator
Set-ExecutionPolicy Bypass -Scope Process -Force
[System.Net.ServicePointManager]::SecurityProtocol =
    [System.Net.ServicePointManager]::SecurityProtocol -bor 3072
Invoke-Expression ((New-Object System.Net.WebClient).DownloadString(
    'https://community.chocolatey.org/install.ps1'))

choco install -y dotnet-10.0-sdk golang git gh 7zip

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
Get-Content token.txt | gh auth login --with-token
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

The canonical development remote is `origin` at GitHub. Do not change remotes or push directly to protected `main`; push a task branch and use a pull request. If the owner explicitly requests post-acceptance mirroring, add or use a separate Forgejo remote without replacing `origin`. Current repository policy is in `docs/agent-contract.md`.

### First build

`build.ps1` requires `-Version` to equal `VPNRouter.Core/AppVersion.cs` exactly:

```powershell
cd C:\Project\VPNRouter
$version = [regex]::Match((Get-Content .\VPNRouter.Core\AppVersion.cs -Raw), 'Version = "([^"]+)"').Groups[1].Value
if (-not $version) { throw 'Cannot resolve AppVersion.Version' }
.\build.ps1 -Version $version
# -> full-install and update ZIPs, each with a SHA256 sidecar, in .\publish\
```

A release upload additionally requires explicit owner authorization and `gh auth login`:

```powershell
$version = [regex]::Match((Get-Content .\VPNRouter.Core\AppVersion.cs -Raw), 'Version = "([^"]+)"').Groups[1].Value
if (-not $version) { throw 'Cannot resolve AppVersion.Version' }
.\build.ps1 -Version $version -Upload
```

Release builds use the pinned sing-box-lx build helper for the required feature tags. Rebuild only when the task explicitly changes or verifies that core:

```powershell
powershell -ExecutionPolicy Bypass -File tools\build-singbox-lx.ps1
```

## Gotchas

- **UAC elevation.** `VPNRouter.App.exe` needs admin for TUN, ETW, and
  firewall rules. Right-click -> Properties -> Compatibility ->
  "Run as administrator" to stop the UAC prompt from interrupting each
  test run.
- **WinTUN driver.** VPNRouter/sing-box loads the bundled Wintun DLL; there is no
  supported `rundll32` registration step. If initialization fails, keep the app
  stopped, reinstall the verified VPNRouter package, and inspect the redacted
  diagnostics before retrying.
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
- **PATH after Chocolatey installs.** Newly installed tools appear on
  `PATH` only for processes started *after* `choco install` finishes.
  The script refreshes its own session, but if `dotnet` / `git` still
  aren't found, open a fresh elevated PowerShell and re-run with
  `-SkipInstall -SkipDefender`.
- **LTSC / Enterprise without Store.** The setup script uses Chocolatey
  exactly because winget depends on App Installer, which is stripped
  from LTSC images. If you later need winget too, install App Installer
  manually — but for this project everything required ships through
  Chocolatey.
- **sing-box process_name matching is case-sensitive.** Covered in
  `docs/agent-contract.md`. Not something you'll hit during setup, but worth knowing
  if you edit `ConfigGenerator.cs` / `ProcessScanner.cs`.

## See also

- `AGENTS.md` — agent entry point and skill routing
- `AGENTS.local.md` — repository-local branch and authority overlay
- `docs/agent-contract.md` — canonical project contract
- `docs/test-workers.md` — test worker node architecture and facts
- `README.md` — end-user documentation
- `build.ps1` — release build pipeline
- `tools/build-singbox-lx.ps1` — pinned custom sing-box rebuild
