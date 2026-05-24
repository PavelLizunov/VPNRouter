# Migration research: Flowseal -> bol-van/zapret2

**Date**: 2026-05-24
**Status**: Research only, no code changes
**Replaces direction of**: `plans/research-zapret-v2-architecture-2026-05-24.md`
  (which mistakenly interpreted "zapret v2" as "our v2 architecture" and
  recommended sing-box DPI features — that brief stays useful as a sing-box
  fallback option but is NOT the answer to this question.)

---

## Context (correction from prior research)

User clarified: "Zapret v2" = **bol-van/zapret2**, the original zapret
author's next-generation rewrite (https://github.com/bol-van/zapret2). Not
"VPNRouter's v2 architecture for Zapret integration." Flowseal's
`zapret-discord-youtube` (VPNRouter's current dep, see
`VPNRouter.Core/Services/ZapretUpdater.cs:47`) is a Windows wrapper around
bol-van/zapret **v1** binaries. Question: migrate to zapret2?

Key fact discovered: **bol-van/zapret v1 is officially EOL** per its README
("This version of zapret is no longer developed and is in EOL mode... The
current version is zapret 2"). So the question is no longer "if" we move,
it's "when" — but the answer is "not now" (see Recommendation).

---

## 1. bol-van/zapret2 — what it is

Fetched: `github.com/bol-van/zapret2` repo description, `docs/readme.md`
(Russian, 50 KB), `docs/manual.en.md` (English, 344 KB),
`docs/changes_compat.txt`, releases API.

- **Description**: "anti-dpi software" — same problem domain as v1.
  4 151 stars, language C, 6 open issues, pushed 2026-05-09 (active).
- **Latest release**: `v0.9.5.2` (2026-04-30), assets =
  `zapret2-v0.9.5.2.zip` (9.5 MB) +
  `zapret2-v0.9.5.2-openwrt-embedded.tar.gz` (4.3 MB) + sha256sum.
  Note **v0.9.x semver** — no 1.0.0 cut yet → not declared stable.
- **Architectural shift (v1 -> v2)**: hardcoded C strategies replaced by
  **Lua scripting**. Per `docs/readme.md`: *"Lua код получает от C кода
  структурированное представление приходящих пакетов в виде дерева
  (диссекты)."* Strategies live in `lua/zapret-antidpi.lua` (52 KB),
  `lua/zapret-lib.lua` (93 KB), `lua/zapret-auto.lua` (21 KB).
- **Maturity**: bol-van's own README warns *"zapret2 — инструмент для
  таких энтузиастов. Но это не готовое решение для чайников"*
  ("a tool for enthusiasts, not a turnkey solution for novices"). Manual
  is expert-oriented; users write or modify Lua. No "easy mode" guide.
- **Windows binary**: per `manual.en.md`: *"winws2 is built for Cygwin,
  which does not support ARM"*. Still Cygwin. Still WinDivert.
- **Bundle proof**: `bol-van/zapret-win-bundle/zapret-winws/` (the
  official Windows distribution, pushed 2026-04-30) ships BOTH binaries
  side-by-side: `winws.exe` 223 KB (v1) + `winws2.exe` 654 KB (v2) +
  `cygwin1.dll` 2.95 MB + WinDivert64.sys 94 KB. Same Cygwin runtime.
  AV story unchanged — bundle readme says verbatim: *"windivert may cause
  antivirus reaction. It's not a virus, your antivirus is insane."*

---

## 2. Architectural diff (v1 -> v2)

| Aspect | v1 (nfqws/winws) | v2 (nfqws2/winws2) |
|---|---|---|
| Strategy definition | Hardcoded C, CLI flags (`--dpi-desync=multisplit`) | Lua functions (`--lua-desync=fake:...`) |
| Packet engine | C only | C extracts/reassembles → emits structured "дисcект" tree → Lua decides |
| Strategy library | None (built-in) | `zapret-antidpi.lua` reimplements v1 ports (`multisplit`, `fake`, `fakedsplit`, `syndata`, `wssize`) as Lua |
| Customisation | Recompile | Edit Lua, no rebuild |
| Windows runtime | Cygwin + WinDivert | **Cygwin + WinDivert (unchanged)** |
| Discord-specific support | Provided by Flowseal preset .bat | **None documented** in zapret2 manual (Discord shows up only as `discord_ip_discovery` payload type in protocol table) |
| Easy presets | `preset1_example.cmd`, `preset2_example.cmd` (basic) | Same two example presets in win-bundle |
| Backward compat | — | Compat layer: v1 strategies portable via Lua; one breaking change documented in `changes_compat.txt` (`stun_binding_req` payload renamed `stun`) |

Net: **v2 = more flexible, less turnkey, same Windows infrastructure.**

---

## 3. Comparison to current VPNRouter Flowseal integration

| Concern | Flowseal current | bol-van/zapret2 direct | Migration effort |
|---|---|---|---|
| Binary deployment | Cygwin `winws.exe` 223 KB + WinDivert + Flowseal scripts (~3.5 MB ZIP) — `ZapretUpdater.cs:142–406` downloads on demand | Cygwin `winws2.exe` 654 KB + WinDivert + Lua scripts + filters (~9.5 MB ZIP) | Replace download URL + asset name; size 3x but same model |
| AV quarantine | Frequent — `ZapretManager.cs:64` `ImmediateExitDetected` event surfaces toast within 2s when AV kills | **Identical risk** — same WinDivert64.sys SHA, same cygwin1.dll, win-bundle readme acknowledges it | Zero improvement |
| Strategy concept | Parse `general*.bat` → arg string (`ZapretUpdater.cs:652–697`, sort heuristic ALT3 first) | Parse Lua function names from `zapret-antidpi.lua` OR keep Flowseal-style preset .bat with `--lua-desync=...` arg | New parser; rewrite `ParseStrategies` |
| Config format | `_vpnrouter_launch.bat` with `SET BIN=`/`SET LISTS=`, Cygwin path expansion (`ZapretManager.cs:272–287`) | Same Cygwin .bat contract still works (winws2 is Cygwin too), only the `--lua-desync` flag replaces `--dpi-desync` | Minor — change flag name + lua module path |
| Discord hosts list | Flowseal-curated `.service/hosts` + IPSet (`ZapretActions.cs:201, 404`) | Zapret2 ships only generic `list-youtube.txt`, no Discord-specific list found | Lose Flowseal's Discord curation OR keep fetching it separately |
| Discord voice unblock | Flowseal preset "general (ALT3)" with `hostfakesplit` + SNI spoof — production-proven | **Not documented** in zapret2 manual. User must author Lua function. | High risk — no validated recipe |
| Update mechanism | `gh repos/Flowseal/zapret-discord-youtube/releases/latest` (`ZapretUpdater.cs:163`) | `gh repos/bol-van/zapret2/releases/latest` OR `bol-van/zapret-win-bundle` (no releases page, raw repo) | Trivial URL swap |
| Russian ISP coverage | Known good — Flowseal preset library curated by community since 2024, in VPNRouter production since v2.9.5 | **Unknown** — no community curation yet, expert-only manual | Mandatory spike |
| Native Windows | No — Cygwin wrapper | **No — also Cygwin** | Zero improvement |
| Maintainer | Flowseal (community) + bol-van (upstream binaries) | bol-van directly | Cleaner chain, drop one hop |
| Release maturity | v1.9.8c (2026-05-07) — battle-tested through v1.0..v1.9 | v0.9.5.2 (2026-04-30) — pre-1.0 | Risk premium |

---

## 4. Recommendation: **(B) Wait — track zapret2 maturity, defer 3-6 months**

**Why not (A) migrate now:**
1. Zero infrastructural win — Cygwin + WinDivert + Windows Defender false
   positives are identical (proved by bol-van's own win-bundle which ships
   both binaries with the same `cygwin1.dll`). The biggest current
   friction point — AV quarantine triggering `ImmediateExitDetected`
   toast — does NOT improve.
2. Lose Flowseal's Discord curation. Flowseal's value is not the
   `winws.exe` binary — it's the **strategy library** (10+ ALT presets,
   Discord hosts list, IPSet list, voice-server CDN mapping) maintained
   by community since 2024. zapret2 manual has **no Discord strategy**
   section. We would re-author this in Lua ourselves with no validation
   pipeline.
3. zapret2 is pre-1.0 (v0.9.5.2), self-described as "for enthusiasts not
   for novices". User-facing surface is Lua. Our users want a one-click
   "Multisplit / ALT3" experience.
4. Flowseal itself shows zero migration intent — release 1.9.8c (a week
   ago) still references `zapret-win-bundle/zapret-winws` for v1 binaries
   only. The ecosystem we depend on is staying on v1 for now.

**Why not (C) hybrid toggle:**
- Doubles maintenance: two ZapretUpdater code paths, two ZapretManager
  spawn shapes, two strategy parsers, two UI strategy lists. With current
  fragility (`ZapretManager.cs:308` Stop() race fix from v2.36.0-r5,
  Bug-r9-G immediate-exit detection) we'd be adding a second
  Cygwin-shaped process surface to a code path that just stabilised.
- Zero user demand — no GitHub issues / stas reports / brat reports
  requesting zapret2.

**Why not (D) "Option C sing-box DPI" from prior research:**
- Still on the table as a **separate** workstream — eliminates Cygwin
  entirely. But it's an answer to a different question: "how to ditch
  external DPI binary entirely", not "should we update which external
  DPI binary we use." User should treat that prior brief as future work
  for v3.0+ Android-alignment, independent of zapret2.

**When to revisit (3-6 month trigger conditions):**
- bol-van cuts zapret2 v1.0.0 (semver signal of API stability), AND
- Flowseal (or equivalent community wrapper) ships zapret2-based
  preset library with Discord voice support, AND
- zapret v1 binaries stop receiving fixes in `bol-van/zapret-win-bundle`
  (currently still updated 2026-04-30), AND
- A user-reported issue surfaces something v1 can't fix that v2 can.

---

## 5. If migration becomes warranted later — Phase 0 spike scope (1-2 days)

Only execute when one of the trigger conditions above fires.

1. Download `bol-van/zapret-win-bundle` master, run `winws2.exe` with
   `--lua-desync=multisplit` against YouTube + Discord on Rostelecom /
   MegaFon / Beeline. Compare success rate vs current Flowseal ALT3.
2. Inventory `lua/zapret-antidpi.lua` exports vs Flowseal's 10 ALT
   strategies — map which v1 .bat → which Lua function call.
3. Time `ZapretUpdater.DownloadAndExtractAsync` on 9.5 MB zapret2 ZIP
   vs current 3.5 MB Flowseal — confirm download UX still <30s.
4. Confirm whether AV quarantine rate changed (subjective — same
   `cygwin1.dll` SHA suggests no, but Defender heuristics may have
   moved on).
5. Decide migration path: **drop-in replacement** (swap binary, parser,
   keep Flowseal-style preset .bats) vs **full Lua adoption** (compose
   our own Lua presets, ship as VPNRouter asset).

---

## 6. Open questions

1. **Discord voice on zapret2** — needs ISP-side spike. Manual gives no
   recipe; community hasn't published one. Single biggest unknown.
2. **Will bol-van remove v1 binaries from `zapret-win-bundle`?** If yes,
   migration goes from "optional" to "forced" — currently both ship.
3. **Does Flowseal plan v2 adoption?** No public statement; 1.9.8c
   changelog mentions only "timestamp checks, GitHub domains in hosts,
   DPI test output" — nothing v2-related.
4. **Lua runtime size on Windows** — zapret2 ZIP is 9.5 MB vs Flowseal
   3.5 MB. Our `ZapretUpdater` cleanup (`CleanupStaleTemps`) and disk
   layout assume ~3.5 MB; doubles the install footprint.
5. **Strategy migration path** — port Flowseal `general*.bat` 1:1 into
   `--lua-desync=...` syntax, or compose our own .lua presets and ship
   them as VPNRouter-owned assets (decouples us from Flowseal but adds
   ongoing curation responsibility).

---

## Sources cited (verbatim sections fetched)

- `github.com/bol-van/zapret` README — "This version of zapret is no
  longer developed and is in EOL (End-Of-Life) mode... The current
  version is zapret 2"
- `github.com/bol-van/zapret2` README + repo metadata (4151 stars, lang
  C, pushed 2026-05-09, 6 open issues)
- `bol-van/zapret2/docs/readme.md` (Russian, 50 KB) — Windows section,
  Lua mandatory, "не для чайников" quote
- `bol-van/zapret2/docs/manual.en.md` (English, 344 KB) — "winws2 is
  built for Cygwin", Discord = `discord_ip_discovery` payload only
- `bol-van/zapret2/docs/changes_compat.txt` — one breaking change:
  `stun_binding_req` -> `stun`
- `bol-van/zapret2/releases/latest` (API) — v0.9.5.2 2026-04-30, 9.5 MB
  zip, 4.3 MB openwrt tarball
- `bol-van/zapret-win-bundle/readme.md` — verbatim "windivert may cause
  antivirus reaction. It's not a virus, your antivirus is insane."
- `bol-van/zapret-win-bundle/zapret-winws/` listing — confirms `winws.exe`
  223 KB + `winws2.exe` 654 KB + `cygwin1.dll` 2.95 MB shipped together
- `Flowseal/zapret-discord-youtube` repo + release 1.9.8c (2026-05-07)
  — still uses v1 binaries, no zapret2 migration plans
- VPNRouter local files: `VPNRouter.Core/Services/ZapretUpdater.cs`,
  `ZapretManager.cs`, `ZapretActions.cs` (referenced inline by file:line)
