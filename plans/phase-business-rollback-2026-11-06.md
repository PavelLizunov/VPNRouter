# Business version rollback and mandatory Windows acceptance

## Intent and scope

Implement the owner's requested older-version rollback through the existing desktop updater, and execute the real Windows scenario. Continue dsh/business-pilot and PR646. Preserve PR652 and its issued2.50.7 source952e23ba without merging or changing that checkout. Version2.50.8 carries the exact four Romania presentation/test files from that source.

## Implementation and invariants

Product source is ed2c570cdd6f95e74ce2a081526f4c770314a441. Reuse IUpdateSource.ListOlderAsync, UpdateNotificationViewModel history/selection/confirmation/download/apply and the Business installer. The existing trusted latest.json keeps its schema/latest fields and adds optional releases entries: maximum12,16KiB document, depth6, stable canonical versions, Business/win-x64, same-authority HTTPS assets, exact size/SHA and per-package PE/resource checks. Old5/6/7 parsers ignore compatible history; actual7-to8 succeeds. Normal checks offer only newer versions. Only explicit admitted older-row confirmation permits rollback. An immutable downloaded offer/path survives overlapping checks/history.

The compact bilingual history lives in the menu. Apply validates the staged package before creating the existing private downgrade config backup and clearing the downgrade receipt. The production preparation stops its owned engine and refuses transition/stop failures before apply/exit. Protected provisioning remains outside the replaced install directory. Standard public MainWindowViewModel signatures, routing and lifecycle sources are retained. Replacement is still per-file, not whole-package transactional; failure does not relaunch a partial replacement.

## Gates and observed outcome

1. Build: PASS exact source ordinary CI, shared package42194693930 and encrypted configured installer42195330728. Twelve applicable PR checks succeed; three private dispatch jobs are expected skips. No product builds or SDKs on Harness/Omarchy.
2. Tests: PASS Linux3682/81skipped, Windows3849/26, Business441/6. The new rollback suites have59passed Linux cases. Shared full ZIP/PE/resource admission7/1; private bootstrap1/0 and native split/full config checks pass. Tests cover actual command-to-MainWindow preparation, stop/failure/no-exit ordering, true pending-download barriers, immutable/forged rows, invalid history, stage tampering and private backup failures.
3. Documentation: PASS this outcome, reconciled ledger and sanitized business-rollback-winbrat-2026-11-06.json. The follow-up commit contains records only; binaries remain exact ed2c570c.
4. Review: PASS three distinct independent native source lenses, excluding their own implementation, plus the requested GPT ASTRA XHIGH full16-file review at exact ed2c570c. No confirmed new P0/P1/P2 source finding. Composed fault-injection coverage remains an explicit P3.
5. Windows/UI: PASS real bridge5 bootstrap,5-to7,7-to8, cancelled and confirmed8-to7, private byte-identical backup/ACL, repeated7-to8, complete278-file payload hashes, and a fresh install of the exact configured8EXE. RU/EN340/420 headless popup images were inspected; confirmation/cancel fit. No desktop screenshot or diagnostic upload.
6. Integration/characterization: PASS applicable CI and unchanged public MainWindowViewModel signatures. After re-update, split/full/split on two reachable physical choices passes actual IPv4HTTPS, generated TCP host, distinct VPN egress, connected selector/mode/subscription locks, connected split health refresh and TUN/core lifecycle. Standard config/service baseline is unchanged. No running unrelated Standard instance was present, so live concurrent-instance isolation is not claimed.

The live refreshed profile has16backing entries and6physical choices. Account/provisioning/source/cache, prior rows, selected choice, mode and Russian language survive replacement/restart/downgrade. Refreshed cache bytes may change; this is not a byte-identical-cache claim. No owner reports existed in the isolated baseline.

## Runtime counterevidence and limits

The initial second-choice full-mode attempt starts the core but hits the existing20second PhaseB warm-up timeout. A diagnostic matrix then passes first-choice full and split on BOTH7and8, while second-choice split proxyHTTPS fails identically on both versions. Generated TCP target matches; bounded logs identify TCP i/o timeouts to the proxy endpoint. The packaged core is byte-identical7/8, and routing/lifecycle sources are unchanged. Independent simultaneous probes show direct Harness TCP timeout while the same endpoint through the established external egress completes CONNECT and validated TLS. This establishes a path/source-dependent failure outside the new updater, not its precise routing/filter/ACL cause or authenticated VLESS availability through that external path.

Direct TCP choices2and3 time out from the test network;1/4/5/6 respond. Final authenticated acceptance uses choices1and4, recording those exact indices, for split/full/split. Do not claim all nodes work. Metadata availability/activity is separate from authenticated VPN availability.

UIS, sustained memory, every rule, DNS/UDP and office reachability remain unverified. History5/6 assets are retained and admitted, but only rollback7 was executed live. UI consumer tests inject stop/installer boundaries; combined installer/backup-error-to-Window no-exit and real-engine-still-running injection remain the documented P3.

Task-only verifier corrections cover the absent Border control peer, post-relaunch UIA readiness, transient enumeration/history state and an observation cap90seconds compatible with the application's60+20second budgets. The shorter35second oracle was invalid, but did not cause the separately confirmed node2 PhaseB failure. Canonical tracked tools are unchanged. Tray exit automation did not locate the worker's tray entry; cleanup used only the identity/path/source/session/start-handle-verified idle task App process with no core/TUN. Graceful tray exit and tray visuals are not claimed as accepted.

## Delivery, privacy and cleanup

Generic8 ZIP73474489bytes, SHA25668f8c7de9cb837f7858dc0458c5363295e25696f813091a1b547eabc6c6d52b6. Trusted HTTPS full download matches. The compatible manifest now offers8 and immutable history7/6/5; older assets and Caddy configuration are preserved.

Configured private8EXE63048329bytes, SHA256583c5d7d832ef16cde923477fa09a0070712774e720dde8c64f3bdfe90a59b5b. Delivered privately on Harness and Omarchy Downloads, directories0700/files0600, remote SHA rechecked. It was not published as a GitHub release or public EXE. Ephemeral GitHub build secrets were deleted and absence verified. Exact-value checking of11actual private endpoint/credential/URL values finds0matches in current tracked files; no complete-history audit is claimed.

Both owned Windows installs, Business data/registry, probe, canonical transient tasks and the two complete staging roots were removed. Final owned process/TUN counts are0. Standard config/service baseline is retained. Official lease vpnrouter-business-rollback-win-20261106 is RELEASED. Private delivery installers and sanitized evidence are retained intentionally. No tag, merge, second feature PR, public configured release or backend service change.

## Preflight provenance

PVE1 admission: load3.25/2.63/2.06,30908MiB RAM total/21747MiB available, no swap,316GiB free disk. The request queued behind another owner and was admitted only after their release. WINBRAT initial runtime preflight:8logical CPUs,0percent load,13815588KiB free RAM,124655112192bytes free disk,2432MiB pagefile/0used, no competing build, Business app/data/core/TUN. Windows PowerShell5.1.17763.9376 and an interactive Explorer session were present. Fresh8 preflight:45percent CPU,13703988KiB free RAM,124525518848bytes free disk, no competing builds. CI package preflights record their separate exact-source SDK/tool/capacity evidence; Windows was used only for runtime acceptance.
