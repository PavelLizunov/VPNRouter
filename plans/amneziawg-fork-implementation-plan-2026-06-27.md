# AmneziaWG-in-VPNRouter — implementation plan (the fork path for T1/T2)

Purpose: ready-to-execute design so "build it" = run this plan. **Gated on a validate-first
proof**: only execute after the tester confirms (via the standalone AmneziaVPN app, 20-30 min
Roblox) that AmneziaWG actually clears HIS 277 — the RU community's 94% is general, his path
may throttle differently. If Track A (calibrated HY2, r6) clears it first, skip this entirely.

## Decision: adopt sing-box-lx (don't fork from scratch)
**Use [Leadaxe/sing-box-lx](https://github.com/Leadaxe/sing-box-lx) — the ORIGINAL** ("lx" =
Leadaxe), a thin downstream fork of SagerNet/sing-box that adds **only** XHTTP + **AmneziaWG
2.0 (AWG2)** (obfuscation Jc/Jmin/Jmax, S1-S4, H1-H4, I1-I5 + masquerade sugar), kept
rebaseable on upstream tags via atomic `// lx` commits. 40★, actively released
(v1.13.13-lx.15, 2026-06-22). AWG2 sits **alongside** the existing protocols (VLESS/HY2/TUIC
stay). Official sing-box won't add it ([#4045](https://github.com/SagerNet/sing-box/issues/4045)
open).
- NOT `helloworld1479/sing-box-lx` — that's a 1★ fork-OF Leadaxe's (its own build clones
  Leadaxe's repo). Use the canonical source.
- Version note: lx is on upstream **1.13.13**; VPNRouter bundles official **1.13.14**. Either
  wait for lx to rebase onto 1.13.14 or pin 1.13.13-lx.15 (a one-minor step back — acceptable).

## Step 1 — Vet + pin (security)
- Review the lx diff vs the matching upstream tag (it's "thin" — XHTTP + AWG2 only). Confirm
  no other behavioural changes, no telemetry, no network calls.
- Pin a specific lx commit/tag. Build **from source ourselves** (don't ship their binary).
- Add a reproducible-build check + record the source SHA in the release notes.
- Risk register: third-party-fork trust, upstream-rebase lag, maintainer dependency.

## Step 2 — Build pipeline (per desktop platform)
**Low-risk approach (DONE for Windows):** don't touch build.ps1's download path. build.ps1
already has a `-SingBoxPath` override to bundle a custom binary. So:
- `tools/build-singbox-lx.ps1` builds the lx binary from source at PINNED commits
  (sing-box-lx `c7a2592e`, wireguard-go-awg2-lx `0c0c10b5`) with the canonical tag set
  (incl `with_awg`) — verified 2026-06-27 on Go 1.26.1; the AWG endpoint it produces passes
  `check`. Clones the wireguard-go fork DIRECTLY into `submodules/wireguard-go` (the fork's
  own `git submodule update` trips on its apple/android client submodules).
- Build the AWG candidate: `build.ps1 -Version X.Y.Z-rN -SingBoxPath <lx-exe> -Upload`.
- **Windows-first because the tester is on Windows.** macOS/Linux lx builds (mirror the same
  go-build in `build-mac.sh` / `build-linux.yml`) are DEFERRED until AWG goes cross-platform.
- Vet: the lx diff is thin (XHTTP + AWG2); build from source; pins recorded above.

## Step 3 — ConfigGenerator: AmneziaWG outbound (client config support)
- `VlessServerEntry`: add awg fields — `PrivateKey`, peer `PublicKey`, `Endpoint`(server:port),
  `AllowedIps`, `PreSharedKey?`, and the AWG obfuscation params `Jc/Jmin/Jmax/S1/S2/S3/S4/
  H1/H2/H3/H4` (+ AWG2 `I1..I5` decoy if used). Protocol = `"amneziawg"` (or `"wireguard"` +
  awg block — match sing-box-lx's schema).
- `ServerUriParser`: parse an awg subscription entry (define the scheme — e.g. `awg://` or
  `wireguard://...&jc=...&h1=...`); reuse `ParseMbps`-style helpers for the ints.
- `BuildAmneziaWgOutbound(entry, tag)`: emit the sing-box-lx awg JSON (system address /
  private_key / peers[{public_key, endpoint, allowed_ips, persistent_keepalive}] + the
  obfuscation block). Dispatch from the protocol switch (next to BuildHysteria2Outbound).
- **Safety:** only emit an awg outbound when an awg server is actually active — so a config
  fed to *official* sing-box (older bundles) never contains an awg type it would reject.

## Step 4 — Subscription format
Define exactly how an AmneziaWG node appears in the subscription so the user's server can
emit it and VPNRouter parses it. Document it in the VPS spec
(`plans/roblox-tester-vps-spec-2026-06-27.md`).

## Step 5 — Routing
Two modes (start with per-game, it's the targeted fix):
- **Per-game:** route `RobloxPlayerBeta.exe` (+ Launcher) through the awg outbound; everything
  else stays on the existing proxy. Login/web still tunneled (awg is full-VPN to a foreign
  exit, so roblox.com is reachable). Game UDP rides plain obfuscated UDP — no QUIC signature,
  no TCP-HoL.
- **Full:** whole tunnel via awg (simpler, but loses the VLESS/HY2 split for other apps).

## Step 6 — Android (DEFERRED)
Android uses libbox (currently 1.13.10), not the desktop binary. AWG2 on Android needs a
libbox-lx fork too — separate, larger work. Desktop-first; the tester is on Windows.

## Step 7 — Maintenance
- On each upstream sing-box bump: rebase the pinned lx (or wait for lx to rebase), re-vet the
  diff, rebuild, re-run `check`. Add a CI job that fails if the lx source SHA drifts unpinned.

## Effort / risk
- ~Several days desktop (build-pipeline + config-gen + tests), ongoing rebase maintenance.
- Android: separate effort, deferred.
- Biggest risks: third-party-fork trust (mitigated by source-build + diff review) and
  rebase-lag on upstream bumps.

## Acceptance
- A generated config with an awg outbound passes `sing-box-lx check`.
- The tester, on a VPNRouter build bundling lx + an awg subscription entry, plays Roblox
  30+ min with no 277.
