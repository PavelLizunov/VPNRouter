# SignPath Foundation application package

Ready-to-paste answers for the free OSS code signing program of the SignPath Foundation, plus the honest list of what
the program may object to. The enrollment itself is **the owner's step**: the application needs the owner's identity and
contact details, a captcha and the owner's acceptance of the terms, so it cannot be submitted by an agent. The form is
embedded on https://signpath.org/apply and its fields could not be read without a browser; adapt the answers below to the
actual field names. After approval, continue with [code-signing-signpath-runbook.md](code-signing-signpath-runbook.md).

Conditions checked against https://signpath.org/terms as read on 2026-10-01.

## Before submitting

1. Merge the pull request that adds the "Code signing policy" section to `README.md` and `README.ru.md` (the terms ask for
   it on the project home page) and open https://github.com/PavelLizunov/VPNRouter#code-signing-policy to check it renders.
2. Confirm the facts only the owner knows (they are marked **owner:** below): multi-factor authentication on the GitHub
   account and on the future SignPath account, the contact e-mail, and that the roles text in the README is what the owner
   wants to publish.
3. Decide the `sing-box.exe` question (next section) before the artifact configuration is written.
4. After submitting, change the README status line from "is applying" to the real state; after approval, remove
   "Status: not signed yet" once the first signed release is published.

## Decision for the owner: the bundled sing-box core

`.github/workflows/sign-windows.yml` currently expects `sing-box.exe` to be signed, and the runbook lists it as eligible
because it is built from `PavelLizunov/sing-box-vpnctl`. That repository is a fork of `SagerNet/sing-box`. The terms
(section "Sign your own binaries only") allow signing a modified upstream build only if the upstream project publishes
signed builds, the fork is a visible GitHub fork, the release branches follow upstream branches that are usually signed and
all changes get code review. To the best of this project's knowledge upstream publishes unsigned Windows builds; check
before relying on it. The README therefore lists only VPNRouter's own executables and libraries as signed. Options:

- Safest: leave `sing-box.exe` out of the first artifact configuration (and drop it from the 18-file check in
  `sign-windows.yml` and from the contract tests that mirror it), accept that this one file stays unsigned, and ask SignPath
  whether the fork qualifies. The terms explicitly allow unsigned upstream binaries inside signed packages.
- Ask SignPath in the application and follow their answer.

## Ready-to-paste answers

**Project name:** VPNRouter

**Repository / home page:** https://github.com/PavelLizunov/VPNRouter (public)

**Download page:** https://github.com/PavelLizunov/VPNRouter/releases

**Code signing policy page:** https://github.com/PavelLizunov/VPNRouter#code-signing-policy

**One-line description:** Process-based split-tunnel VPN router for Windows, macOS, Linux and Android: choose which
applications go through a VPN/proxy (sing-box core, VLESS+Reality and other protocols) and which connect directly.

**Longer description:** VPNRouter routes traffic per application instead of per device. The user picks applications from a
process list; selected applications use the proxy and the rest use the direct route (or the reverse). It wraps the open
source sing-box core, adds subscription handling, diagnostics, an optional per-process firewall kill switch and DNS leak
protection on Windows, and optional add-ons (a DPI-bypass wrapper around the open source Zapret project, a Telegram
proxy). It is a client for servers the user supplies; the project operates no VPN service and collects no telemetry.

**License:** GPL-3.0-or-later (OSI approved), single license for all project components, no commercial dual-licensing.
Third-party components and their licenses are listed in `NOTICE.md`.

**Open source / no proprietary code:** The repository contains the whole application and its build scripts. Bundled
third-party binaries come from open source projects (sing-box and its fork, Mullvad win-split-tunnel driver, libcronet,
slipstream-client); two `NOTICE.md` entries need a license check before answering this question (see the list of points
below).

**Maintained:** 66 releases since 2026-02-20, latest `v2.50.0-r10` on 2026-09-29; about 1,960 commits by the owner
(GitHub contributors API, 2026-10-01).

**Released in the form to be signed:** The Windows ZIPs (`VPNRouter-vX-win.zip`, `VPNRouter-update-vX-win.zip`) are already
published unsigned on GitHub Releases. A Windows installer (`VPNRouter-Setup-vX.exe`, Inno Setup) is implemented in pull
requests and not yet released; it will be signed from the first release that contains it.

**Reputation (be straight about it):** 4 GitHub stars and 1 fork; the repository is seven months old. GitHub's release
asset download counters add up to about 114,000 downloads over 66 releases on 2026-10-01; this includes checksum files and
updater archives and counts downloads, not users. A single maintainer. The project is also distributed through an APT
repository and a Homebrew tap; winget manifests are kept in `packaging/winget`.

**Build from source, verifiably:** GitHub Actions. Every pull request runs the test suite on GitHub-hosted Windows runners;
`main` requires the checks `test` and `grep` on an up-to-date branch, no force-push or deletion. Signing is a separate
manually dispatched workflow, `.github/workflows/sign-windows.yml`: it runs from an immutable release tag, proves that
the checked-out commit equals the tag commit and the version in the source, builds the Windows ZIPs from source on a
GitHub-hosted runner, uploads them as a GitHub Actions artifact and passes that artifact ID to SignPath; it then verifies
every signature, the signer subject and one consistent thumbprint before anything is added to a still-draft release.
Nothing is overwritten and nothing is published automatically.

**Release process:** Draft-first. The owner creates an immutable tag and a draft release, CI stages the platform assets
(exactly 16 files with `.sha256` sidecars), integrity and update tests run against the tag, then the owner publishes the
release by hand. Every release needs a manual approval in SignPath (the OSS policy default).

**What would be signed:** `VPNRouter.App.exe/.dll`, `VPNRouter.GUI.exe` (a small Go launcher built from `VPNRouter.GUI/` in
this repository), `VPNRouter.CLI.exe/.dll`, `VPNRouter.Service.exe/.dll`, `VPNRouter.Core.dll` and, once released, the
Windows installer. Product name `VPNRouter` and the same product version in every file (the SignPath file metadata
restrictions the terms require). Not signed with this certificate: the Mullvad split-tunnel driver (already signed by its
publisher), libcronet, slipstream-client, sing-box (see the decision above) and anything the app downloads at run time.

**Team and roles** (as published in the README):

| Role | Who |
|---|---|
| Author (commits to `main`) | PavelLizunov |
| Reviewer | PavelLizunov, the only maintainer; contributions from bots and from AI coding assistants arrive as pull requests |
| Approver of signing requests | PavelLizunov |

State plainly that a large share of the code is prepared with AI coding assistants under the owner's direction; the
public repository contains the agent instructions (`AGENTS.md`), so a reviewer will see it anyway. Every change still goes
through a pull request with required CI. **owner:** MFA on GitHub and SignPath, contact name and e-mail.

**Privacy:** `PRIVACY.md` is the privacy policy. No telemetry, analytics or crash upload. Network connections: the servers
the user configures; GitHub Releases API and assets for update checks, the rolling Free Configs pool and on-demand
downloads of Zapret and the Telegram proxy; `ip-api.com` only as a fallback when the pool is unavailable (it sees the
user's IP address and the candidate servers' addresses). The literal sentence "This program will not transfer any
information to other networked systems unless specifically requested by the user or the person installing or operating it"
is not used because the update check and the Free Configs fetch are automatic; the link to `PRIVACY.md` is the permitted
alternative. The program collects no user data and sends none to systems the user did not choose, so the terms' rule on
disclosure at install time does not apply; the installer shows no data collection because there is none.

## What the software changes on the system

The terms require that system changes are announced, and that uninstallation exists. This list is deliberately complete
for Windows, the platform that would be signed.

| Change | When | Removed by |
|---|---|---|
| Program files in `C:\Program Files\VPNRouter`; data (settings with connection credentials, profiles, logs, caches) in `%ProgramData%\VPNRouter`, whose access list is restricted to the current user, Administrators and SYSTEM | install | Uninstaller removes the program files; the data folder is kept unless the user says yes or passes `/DELETEDATA` |
| A TUN network adapter (`VPNRouter-TUN`) and routes, created by the sing-box core | while the VPN runs | stopping the VPN |
| Windows Firewall rules `VPNRouter_Block_<process>` (per-process block if the VPN fails) | only when that protection is enabled and the VPN runs | stopping the VPN; `VPNRouter.CLI.exe cleanup` removes leftovers |
| DNS leak lockdown: Windows Firewall rules `VPNRouter-DnsLockdown-*` and `0_VPNRouter-DnsLockdown-*` blocking plain DNS and DNS-over-TLS outside the tunnel, and two DNS-client registry values set to 1 (`DisableSmartNameResolution` under the DNSClient policy key, `DisableParallelAandAAAA` under `Dnscache\Parameters`); previous values are saved in `%ProgramData%\VPNRouter\dns-hardening-state.json` | only when DNS hardening is enabled and the VPN runs | restored on stop; `cleanup` restores from the saved state |
| Kernel driver service `mullvad-split-tunnel` (Mullvad's win-split-tunnel, signed by its publisher) for true per-application split | first use of that mode | `cleanup` removes it only if it is VPNRouter's own copy (binary path check); another VPN's copy is left alone |
| DPI-bypass add-on: `winws.exe` and the WinDivert driver/service (`zapret`, `WinDivert*`) | only if the user enables it in the Tools tab; downloaded on demand from upstream | `cleanup` removes the services whose binary lies in the VPNRouter folder; foreign ones are left alone |
| Windows service `VPNRouter` (boot-time start) | only if the user installs it (Settings, `VPNRouter.CLI.exe service install`, or an installer task that is **off** by default) | uninstaller stops and deletes it |
| Autostart: `HKCU\...\Run\VPNRouter` | only if the user turns it on (installer task is **off** by default) | uninstaller, `cleanup` |
| Microsoft Defender exclusions for the install and data folders | `install.ps1` adds them unconditionally; the installer task is **off** by default and exists only because binaries are unsigned | the installer removes them at uninstall if its task added them; see the risk below |
| Start Menu shortcut, optional desktop shortcut, Apps & Features entry | install | uninstaller |

Where the user is told: the installer lists the tasks and leaves every system-level one unchecked; the README and
`docs/` describe the firewall, DNS and driver behavior; the app needs Administrator rights (UAC) and the README says why.
**Gap to close before applying, if the owner wants a cleaner answer:** a short first-run notice in the app that names the
firewall and DNS changes, since today the warning lives in documentation and settings text, not in a dialog.

Uninstall story: the Inno Setup uninstaller (`unins000.exe`, listed in Apps & Features) stops the app and the service it
owns, runs `VPNRouter.CLI.exe cleanup` (firewall rules by prefix, saved DNS values, the driver and WinDivert services that
belong to VPNRouter, the autostart value), removes the Defender exclusions it added and deletes the program files; it
needs no network. `cleanup --dry-run` prints what it would remove. Until the installer is released, the script
installer's uninstall remains `iwr ... | iex` (`uninstall.ps1`).

## Points the reviewers may raise

1. **"No hacking tools": the DPI-bypass add-on.** The clause forbids features that identify or exploit security
   vulnerabilities or circumvent security measures of the software's execution environment. The add-on (a wrapper around the
   open source Zapret/winws) works against network-level filtering by a provider or a national firewall; it does not attack or
   bypass anything on the user's own machine and it is optional, off by default, and downloaded only on demand from
   upstream. Say this once, plainly, and offer to ship the signed build without the add-on if SignPath prefers: the add-on
   is downloaded at run time, not bundled, so the signed files do not contain it.
2. **Defender exclusions.** Adding antivirus exclusions can read as circumventing a security measure. The installer keeps it
   an unchecked, explained task, removes it on uninstall, and the recommendation should be withdrawn (task removed) once the
   binaries are signed, because signing is the actual fix for the antivirus false positives the exclusions work around.
   `install.ps1` still adds the exclusions unconditionally; that script is superseded by the installer.
3. **System changes without warning.** Firewall and DNS changes exist only while the VPN runs and only when the
   corresponding protection is on; the honest gap is the missing first-run dialog noted above.
4. **Third-party licenses.** `NOTICE.md` lists `slipstream-client` ("see upstream repository") and MaxMind GeoLite2 data
   ("respective upstream terms") without a license name. **owner:** check both before answering "no proprietary
   components"; if either is not open source, drop it from the signed packages or say so in the application.
5. **Reputation and single maintainer.** See above; nothing to add except real numbers.
6. **The bundled sing-box core.** See the decision near the top.
7. **Unsigned installer hash.** The installer and ZIPs are verified by `.sha256` sidecars, which do not authenticate the
   publisher; that is exactly the gap code signing closes.

## After approval

Follow [code-signing-signpath-runbook.md](code-signing-signpath-runbook.md): project `vpnrouter`, policy
`release-signing`, artifact configuration for the ZIPs (and later the installer; for Inno Setup the installer and the
uninstaller it embeds are signed in the same step), the five repository secrets and `SIGNPATH_EXPECTED_SUBJECT`. Then
remove the status line from the README section, drop the Defender exclusion task from the installer, and ask winget and
the other channels to rely on the signed installer.
