# Refactor backlog (deferred)

Refactor / cleanup items intentionally deferred — **not** defects (those live in
`OPEN-DEFECTS.md` and gate cut-stable), **not** active work. Each has a reason + a
pointer to the full record. Pull one back into an active goal when it's worth a
focused session.

Source of truth for verification context: `codebase-reduction-and-split-plan.md` §7
ledger (each item tracked there with how it was/would be verified).

## Deferred

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

- [ ] **Android `libbox.aar` bind metadata is currently ineffective** — found
  during DR-06 on 2026-08-02. The .NET Android SDK auto-includes every AAR with
  `Bind=true`; the project's explicit `AndroidLibrary Include=... Bind=false`
  adds a second item instead of updating the first. An evaluated-item check
  shows both copies, and the clean Release build still generates 49 libbox
  binding files (518,535 bytes). Test a focused `Include` -> `Update` change,
  then verify the Java service/reflection boundary and a real VPN connection on
  A101BM before keeping it.

- [ ] **Remove duplicate explicit `AndroidJavaSource` items** — found during
  DR-06 on 2026-08-02. `AutoImport.props` already includes all four in-tree Java
  files, while the csproj adds the same four paths again. The current toolchain
  tolerates the duplicates, but the entries and their comments are redundant.
  Remove or convert them only in a focused Android build/device task so Java
  service, deep verify, QR scan, and slipstream coverage travel together.

- [ ] **Refresh stale Android QR documentation** — found by Qwen during DR-06.
  `AndroidManifest.xml` still describes the removed photo/JPEG ZXing.Net flow,
  and `plans/v3.0-execution-methodology.md` preserves the old .NET 8 claim that
  `Bind=false` cannot suppress transitive bindings. Update after DR-06 is
  accepted; the csproj's directly affected comment is fixed in the experiment.

- [ ] **Recover and re-verify the offline Android production signing-key
  backup** — operational release blocker found during DR-06 on 2026-08-02.
  `plans/android-keystore-backup-2026-06-02.md` names offline copies on the old
  dev VM/shared folder, but neither the current workstation nor windows-brat has
  them at the documented or searched paths. The installed A101BM certificate is
  still the expected SHA-256 `6e50af0f...45a221`; GitHub Actions can sign with
  its write-only secrets but cannot restore the key. Locate the claimed offline
  copy and verify its certificate before relying on local device/update gates.
