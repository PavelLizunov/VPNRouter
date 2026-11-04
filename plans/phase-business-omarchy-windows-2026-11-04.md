# Business reachability build on the owner's Windows 11 VM

- Owner request: install the new Business client on the Windows KVM guest on
  the owner's Omarchy workstation and configure SSH access to that host.
- Accepted source: 0079ee82cd266e43af895a7c5d3d92359bdf0a24; all eleven applicable
  PR checks pass. Product source is 28a7eb3a. Continue PR #646; no public release.
- Risk: MEDIUM, private packaging and an owner-requested test installation.
- Target: linked Omarchy environment, then its local Windows 11 guest.
  This explicit owner request supersedes the usual WINBRAT-only target for
  this deployment. Do not start or modify WINBRAT or unrelated guests.
- The owner authorizes exactly one remote Codex agent for SSH/VM preparation.
  Preserve gpt-6.1-sol / xhigh / default; no recursive agents or source edits.
- Build only in exact-SHA GitHub CI with read-only capacity/tool preflight.
  No SDK installation or compilation on Harness, Omarchy or the new guest.
- Recover the previously supplied private connection without printing it;
  use private temporary files and encrypted-only CI artifact delivery.
- Pin the Omarchy SSH host key from the independent trusted T3 channel, add
  the existing Harness public key without replacing authorized keys, and
  preserve other SSH access and services.
- Place verified installer/receipts below ~/Windows in a task-owned folder;
  deploy through the guest share \\host.lan\Data after verifying guest identity.
- Leave the application ready for the owner's manual test. Do not connect the
  VPN automatically or claim new live dataplane/UIS/memory acceptance.
- Verification: build/source/hash, guest install, Business UI and new controls.
  No live desktop screenshots or raw configuration/logs in reports or Git.
- Rollback: retain the previous private installer; preserve existing user data.
- Outcome: DEPLOYED. Temporary CI secrets and raw build inputs removed; retain
  the private delivery artifacts, requested SSH and running test installation.

## Build and source evidence

- Private CI 41895251229 succeeds at exact source 0079ee82. The source had all
  eleven applicable PR checks green before dispatch. No product changes or SDK
  installations were made on Harness, Omarchy or the guest.
- CI preflight: 4 CPUs, 1 percent load, 14027664 KiB available RAM, 27.69 GiB free
  disk, zero competing builds; 2944 MiB page file with zero use. First-run private
  provisioning test passes; native core split/full configuration checks pass.
- Installer: VPNRouterBusiness-Setup-v2.50.5-server-health.exe, 62994257 bytes,
  SHA256 d02712e4e7c8ed3bf6508c67a6dbe430578ac3964081150eef7b01389034c94f.
  Source receipt and decrypted output match. Hash rechecked after transfer to
  Omarchy and inside the Windows guest before installation.
- The owner-provided subscription reference was recovered from the original
  private task without printing it. Its current preset has 10 entries and 3
  physical VLESS hosts, with no blank/duplicate labels. Exact comparison of 17
  private connection values against 1495 current/PR-history blobs found no
  matching tracked paths. Values and expanded host details remain outside Git.

## SSH and guest deployment

- Exactly one owner-authorized remote Codex agent prepared SSH and the local VM.
  Existing OpenSSH was enabled, existing authorized keys preserved, Harness key
  added. Parent pinned the Ed25519 host key supplied through the independent
  trusted T3 channel and verified SSH key authentication. sshd is active/enabled.
- The owner subsequently stopped host root/password prompts while gaming.
  Parent did not invoke sudo/pkexec. Installation/verification used an isolated
  Xvfb/FreeRDP session as the normal host user with existing private guest
  credentials supplied through stdin, not process arguments. No live desktop
  screenshot was taken. No extra agent, VM start or resource change was used.
- Agent prelaunch observations: 32 host CPUs, load 1.76/2.46/2.71, 50159 MiB
  available RAM, 157 MiB swap used, 109.2 GiB free disk, no compiler jobs. Guest
  allocations remained 8 CPUs/16 GiB; the running game and host VPN were left alone.
- The SMB server alias was not the guest computer name. An initial identity
  guard stopped before installation. A read-only RDP probe established the real
  guest identity, Windows 11 Pro build 26200, QEMU Q35, the intended desktop user
  and an already-admin guest token. There was no previous Business installation.
- Installation exits 0 at C:\Program Files\VPNRouterBusiness. Installed assembly
  metadata contains both new health classes and the exact 0079ee82 source hash.
  Initial script/UIA observations were not accepted as completion: the process
  and window were confirmed, then a final process-scoped child-label query passed.
- Final UI result: app/window running, selector present/enabled, Refresh present,
  exactly three distinct node labels; labels/configuration were not exported.
  No Connect action was invoked. This is installation/UI acceptance, not a new
  VPN dataplane, UIS, long-memory, every-node or routing/DNS acceptance claim.

## Handoff and cleanup

- VM and app left running for the owner's manual test. Desktop shortcut is
  VPNRouter Business. Private files are below the owner's ~/Windows task folder,
  reachable as \\host.lan\Data\VPNRouterBusiness-ServerHealth-2026-11-04.
- Open-Windows-for-Test.sh opens only RDP to the already-running guest, without
  root or VM lifecycle calls. Closing it does not stop the VM. Russian checklist,
  source/hash receipts and sanitized guest results accompany the installer.
- Both ephemeral GitHub pilot secrets were removed and verified absent. Raw
  reference/config/key files and the task GPG agent were removed after retrieval.
  Guest installer staging, host automation/proof files and task RDP logs/cache
  were removed. Private delivery artifacts and executed orchestration evidence
  are intentionally retained on Harness; the previous 8f874598 installer remains.
- Only this outcome record changes the repository. No merge, tag or public
  release was created. The outcome commit requires its own exact-head CI gate;
  the installed/tested source remains 0079ee82 regardless of that records head.
