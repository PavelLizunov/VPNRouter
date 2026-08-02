# Refactor backlog (deferred)

Refactor / cleanup items intentionally deferred — **not** defects (those live in
`OPEN-DEFECTS.md` and gate cut-stable), **not** active work. Each has a reason + a
pointer to the full record. Pull one back into an active goal when it's worth a
focused session.

Source of truth for verification context: `codebase-reduction-and-split-plan.md` §7
ledger (each item tracked there with how it was/would be verified).

## Deferred

- [ ] **DR-05 Android QR documentation follow-ups** — remove stale comments in
  `AndroidApp.axaml.cs` and `MainActivity.cs` that still name the already-removed
  `QrCodeDecoder` photo-capture implementation. Separately verify whether NOTICE
  should name the live Java artifacts (`zxing-core` and
  `zxing-android-embedded`) instead of `ZXing.Net`; keep the Apache-2.0 notice
  until that license wording review is done.

- [ ] **T2-D Zapret/TgProxy path centralization** — deferred 2026-06-25. The one
  **behavior-affecting** item in the codebase-reduction plan: delegating
  `ZapretUpdater` / `TgProxyUpdater`'s bespoke `CommonApplicationData` `_dataDir` to
  `AppPaths` intentionally changes path resolution under `OverrideDataDir` (the
  plan's "correctness bonus"). Not a pure move / const-extract -> excluded from the
  strictly-behavior-preserving codebase-reduction goal. Needs a focused session: add
  `AppPaths.Zapret*` / `TgProxy*` members, repoint both updaters, MCP-verify the
  DpiBypass + Telegram pages still work, update any `OverrideDataDir` path tests.
  Windows production behaviour is identical (no override there); the change only
  matters under override (Android sandbox / tests). Plan ref: §4 T2-D + §7 ledger.

- [ ] **T1-C Banners partial** (`MainWindowViewModel`) — deferred 2026-06-25. The
  ThemeAndLogo half is DONE (shipped `c52746fd`, characterization-hash-neutral). The
  Banners half's members (PlaceholderPruneNotice + ConflictingVpn warnings) are
  scattered and tangled with conflict-action orchestration referenced across the
  connection flow (`MainWindowViewModel.cs:4053/4161/4171/6939`). Lower value
  (~40 lines); needs a careful per-member scattered extraction. Hash-neutral if done
  as a pure move. Plan ref: §3 T1-C.

- [ ] **Android copies of T2-A/B/C** (`DeepVerifyConstants` + `NetPortUtil`) —
  deferred 2026-06-25. The Core dedup is done; `AndroidFreeConfigDeepVerifier` still
  carries its own `ProbeUrl` / `OverallTimeout` / `FindFreePort` copies. Can't be
  locally build-verified (the `.sln` excludes Android — it builds only under the
  separate .NET 10 + gomobile toolchain), and `NetPortUtil` being `internal` won't
  cross to the Android assembly (would need `public` or `InternalsVisibleTo`
  Android). Do on a session that runs the local Android build. Plan ref: §4 T2-A/B/C.
