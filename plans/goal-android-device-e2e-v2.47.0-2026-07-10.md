# Goal — Android device e2e + дальнейшие доработки (2026-07-10)

## Триггер

User разблокировал Android-телефон (снял PIN) — единственный блокер, который держал
все Android-пункты через циклы v2.46.0/v2.47.0. Ground truth на момент составления
(проверено live через Mac-хост):

- Устройство: BALMUDA Phone A101BM, serial `54499112209`, adb state `device`,
  AC-powered 100%, доступ через `ssh slovn@192.168.0.246` (fallback Tailscale
  `100.116.97.112`), adb в `/opt/homebrew/bin/adb`.
- На телефоне: VPNRouter **v2.44.1** (versionCode 2044001, targetSdk 36).
- Открытые Android-долги: (1) v2.46.0 stable APK подписан и приложен к релизу, но
  **не device-tested** (открытый пункт memory v2.46.0-cycle-state); (2) весь
  v2.47.0 Android-контент не проверен на устройстве — clash-api secret в
  `VpnRouterService.java` (poller c Authorization), A1 openTun route logging
  (P0.1), плюс регрессия B3 kill-switch / P4 broadcast после этих правок.

## Phase 0 — доступ (DONE 2026-07-10, зафиксировано выше)

adb online, версия снята. Если доступ пропадает mid-goal — STOP и доложить, не
эмулировать проверки.

## Phase A — v2.46.0 stable APK: device e2e (закрывает "APK not device-tested")

1. Скачать на Mac подписанный `VPNRouter-v2.46.0-android.apk` + `.sha256` с релиза,
   сверить дайджест.
2. `adb install -r` поверх 2.44.1 — это реальный user-путь обновления (та же
   release-подпись — INSTALL_FAILED_UPDATE_INCOMPATIBLE быть не должно; если
   вылезла — это сам по себе P1-факт, зафиксировать).
3. Launch, скриншот, logcat-скан (`FATAL`, `AndroidRuntime`, `vpnrouter`).
4. Connect-цикл: существующий конфиг на устройстве (остался от P4-тестов) или
   вставить subscription URL; VpnService consent-диалог обработать тапом; проверить
   статус "Подключено", трафик через туннель (открыть сайт / notification-статы
   тикают), disconnect.
5. Verdict Phase A → memory (закрыть пункт "Android APK not device-tested").

## Phase B — v2.47.0 APK: сборка + новые Android-поверхности

Сборка по memory `android-local-build-toolchain` (Go 1.25.9 + gomobile fork +
.NET 10.0.301; libbox.aar — кэшированный SFA-fork aar РЕИСПОЛЬЗОВАТЬ, Android
sing-box в 2.47 не менялся — upstream 1.13.10):

1. `dotnet publish VPNRouter.Android -p:EnableAndroidTarget=true
   -p:VpnRouterVersion=...` — ВНИМАНИЕ gotcha: деривация versionCode может не
   принять `-rN` суффикс; если так — собрать с core-версией `2.47.0` и различать
   по versionName/commit.
2. Подпись: предпочтительно CI-путь как у v2.46.0 (upload UNSIGNED asset на
   r-релиз → `gh workflow run "Sign Android APK" -f version=...`) — сохраняет
   signature-continuity, тестирует реальный update-путь 2.46.0 -> 2.47.0.
   Fallback: uninstall + debug-sign fresh install (теряем update-путь — пометить).
3. Установить, e2e по новым поверхностям:
   - **clash_api secret (r3, главный риск)**: сгенерированный конфиг несёт
     `experimental.clash_api.secret`; notification-статы ЖИВЫЕ при подключении
     (poller теперь шлёт Authorization — если extractClashApiSecret сломан, статы
     молча замерзают/нулевые = silent regression);
   - **A1 openTun route logging (P0.1)**: logcat при connect — маршруты
     залогированы;
   - **B3 kill-switch**: убить sing-box-процесс → fail-closed поведение/нудж;
   - **P4 broadcast**: EXT_START/EXT_STOP/EXT_TOGGLE через
     `am broadcast -a $PKG.EXT_* -n $PKG/.VpnControlReceiver` (gotcha zsh/adb:
     `bash -s` + `main(){...}; main </dev/null`);
   - subscription user-info (P2) отображается; общий smoke.

## Phase C — P0.2 LAN-capture диагностика (гейт для excludeRoute)

Handoff P0.1/P0.2 явно запрещает `excludeRoute()` пока диагностика не ДОКАЖЕТ
LAN-захват. Теперь можно снять доказательство:

1. VPN подключён (Wi-Fi), с телефона обратиться к LAN-цели (Mac
   `192.168.0.246:22`, роутер `192.168.0.1:80`) — socket/HTTP.
2. Параллельно logcat openTun-маршруты: попадает ли LAN-трафик в TUN.
3. Вердикт (captured / bypassed) → в ledger + handoff-план. Это вход для решения
   по excludeRoute — САМО излишнее route-rewrite НЕ делать (по handoff).

## Phase D — ledger + cut-readiness

1. Обновить `plans/OPEN-DEFECTS.md` + memory результатами A/B/C.
2. Если A+B зелёные: verification gate v2.47.0 покрывает и Android → доложить
   user'у полный cut-readiness. **Stable cut остаётся USER-GATED** (rule #6);
   при команде "cut" — по скиллу cut-stable, включая Step 5.6 (пересборка stable
   APK `VpnRouterVersion=2.47.0` + CI-sign + attach, 16 assets).

## Phase E — stretch / post-cut (не блокирует cut)

- **B4 Profiled AOT + R8 + linker keep-rules** (task #28, годами pending) — теперь
  device-profilable; делать ПОСЛЕ cut, отдельной итерацией с r-циклом.
- Остальное по-прежнему за пределами моей автономии: urltest D1/D2 + app-exclude
  (product-решения user'а), SignPath enrollment (owner), SDR/AWG-server live
  (tester+сервер), wgturn (cross-repo), .NET 8->10 (осенний branch).

## Acceptance

- [ ] Phase A: v2.46.0 APK update-install поверх 2.44.1 OK, connect/traffic/
      disconnect OK, logcat чистый — пункт "not device-tested" закрыт
- [ ] Phase B: 2.47.0 APK собран + установлен; clash-secret в конфиге; статы
      живые (poller auth OK); A1 openTun-лог снят; B3+P4 регрессия зелёная
- [ ] Phase C: LAN-capture verdict записан с evidence (logcat + probe-результат)
- [ ] Phase D: ledger+memory обновлены, cut-readiness доложен
- [ ] Скриншоты/логи с credentials (subscription URL) НЕ закоммичены, удалены

## Риски / gotchas

- adb через Mac ssh: heredoc-слурп — только `bash -s` + `main(){} </dev/null`.
- Экран: держать awake (`input keyevent KEYCODE_WAKEUP`); PIN снят.
- VpnService consent-диалог на первом connect после переустановки.
- versionCode из `-rN` (см. Phase B.1); NU1102 — CI полную Android-сборку не
  тянет, только локально.
- Battery optimizer может убить сервис — на AC 100% риск минимален.
- НИКАКИХ установок VPNRouter на dev box (rule 1a); телефон — только через Mac.

## Outcome (2026-07-10)

**Status: PASS.** Phone unblocked → all three phases run live on BALMUDA A101BM.

- **Phase A (v2.46.0 device e2e) — PASS.** Signed APK digest-verified (`92a3b691…625a`),
  `install -r` over 2.44.1 = real update path (same release key, no
  INSTALL_FAILED_UPDATE_INCOMPATIBLE). Launch/render clean, connect (consent already
  granted), **50 MB @ 770 KB/s through the tunnel** with correct live stats, health-check
  ✓, kill-switch nudge rendered, clean disconnect (tun0 torn down). **Closes "Android APK
  not device-tested".** Found a real (cosmetic) device-only bug → fixed (below).
- **Phase B (2.47.0 build + surfaces) — PASS.** Built the APK locally
  (`~/.dotnet10` + temp SDK-10 global.json override for the net10 subtree +
  `-p:` to dodge Git-Bash `/p:` mangling; **closes the CI-NU1102 local-build gap**),
  versionCode 2047000. Fresh install → subscription refresh fetched+parsed 6 servers →
  select Germany VLESS → Start VPN. Verified: **tun0 + clash-api@9090**, real traffic
  (`inbound/tun → outbound/vless[proxy]`, curl **4.2 MB/s**), **clash-secret bearer works
  — no 401** (r3 feature live on Android), **A1 openTun route logging** present, **my
  protect() fix: 0 log-spam** (was 139/min on 2.46.0), **live stats display 5.8 MB/s
  correct**, battery-opt exemption prompt (AND-NODOZE) fired, Advanced UI all 5 tabs render,
  auto-select toggle present, P4 EXT_STOP broadcast correctly IGNORED (secure-default,
  external-control off). VpnService consent handled per plan.
- **Phase C (P0.2 LAN-capture diagnostic) — evidence: NO CAPTURE.** Phone 192.168.31.137;
  Mac 192.168.0.246 pings **0.857 ms** with VPN all-traffic UP (sub-ms = local, not the
  Germany exit), **0 `192.168.x` captured in tun**. Local/LAN traffic bypasses the tunnel
  → the excludeRoute concern does NOT reproduce here → stays deferred, now with evidence.

**Fix landed (89c271d8, CI green, both remotes):** `fix(android)` — stats poller
`protect()` false-alarm spam. Only warns now when a tick genuinely produces no stats.
AndroidApp*.cs untouched → characterization hash unaffected. Rides into the stable APK
(Step 5.6), no new desktop -rN needed.

**Cut-readiness:** v2.47.0 verification gate now covers Android too (build+install+connect+
traffic+all surfaces on real hardware). Desktop r7 = 14 assets, CI green. **Stable cut
remains USER-GATED** (rule #6) — awaiting "cut".

**Deferred (unchanged, gated):** B4 AOT (post-cut, now device-profilable); SignPath
enrollment (owner); SDR/AWG-server live (tester+server); wgturn (cross-repo); urltest
D1/D2 + app-exclude (product); .NET 8→10 (autumn).

## r9 re-verify on device (2026-07-10, after r8+r9 landed)

Phase B built the APK from ~r7 code. Since then r8 (F1 deep-verify cancel, F2
endpoints custom-config, F3 CI, F4 hygiene) + r9 (RuntimeStatusDetector probe-scope,
ServerTesting AllowConcurrentExecutions) landed. Since VPNRouter.Android source-links
Core, a rebuild was run from HEAD (c5c0f329) to prove the r8/r9 Core changes still
compile for Android AND to re-run the device e2e on the actual r9 binary.

- **Build (dev box, `~/.dotnet10` + VPNRouter.Android/global.json 10.0.301):** clean
  **0 errors**, 209 warnings (all pre-existing cosmetic CS15xx XML-doc + CA1416/CA1422
  platform-availability). **Build-risk for the cut APK = NO BLOCKER.** versionCode 2047000.
- **Install:** `adb install -r` over the Phase-B 2.47.0 (same versionCode + same local
  debug key) → `Success`, settings + VpnService consent preserved.
- **Launch:** clean render (Simple mode), only expected `dns-tunnel Slipstream
  unavailable` INFO (desktop-only native, correctly disabled on Android). No FATAL.
- **Connect:** tun0 UP (172.19.0.1/30), libbox `outbound/vless[proxy]`, **egress
  104.194.156.93 (Germany exit, not local)**. Live stats moving (↓29/↑30 B/s, 9 conn),
  **health check ✓ ("Last check 24s ago")**, kill-switch nudge rendered.
- **r8/r9 Android-reaching surfaces re-confirmed:** clash-secret bearer authenticates
  (stats + /connections read, **0×401**), **protect() spam = 0** (was 139/min pre-fix),
  0 genuine stats-tick failures.
- **Disconnect:** tun0 torn down, **egress reverted to ISP 109.196.76.225**, no crash.

**Verdict: r9 Android build is device-verified PASS.** The signed cut APK (Step 5.6,
official keystore via sign-android.yml) is the only remaining Android step and it is
part of the user-gated stable cut.

### Android surface audit of v2.46.0..HEAD (workflow, adversarially verified)

Ran a multi-agent reach/observability/build-risk/verify workflow over every code
change in the range to confirm exactly what reaches the Android binary. Verdict:

- **Build risk: NO BLOCKER** (matches the actual clean 0-error build). Only 1 `warn`
  = `RuntimeStatusDetector.IsVpnRunning` gains a probe-in-flight second-signal gate,
  but on Android `DeepVerifyProbe.AnyProbeInFlight` is always false + `IsOwnedByAnyone`
  is fail-open try/catch → behavior unchanged, cannot throw.
- **Device-observable on Android (3, all verified):** (1) clash-secret bearer plumbing —
  **device-confirmed PASS** (stats flow, 0×401, health ✓); (2) F2 `FindProxyOutboundTag`
  endpoint-tag fix — reaches Android via `AndroidConfigBuilder.BuildConfigJsonFromCustom`,
  unit-pinned + reach-confirmed (custom-config paste on a Release build isn't adb-drivable,
  so not device-clicked; SFA-libbox HAS `with_wireguard` so a plain-WG endpoint config
  now starts instead of the pre-fix `unknown outbound` FATAL); (3) fork-feature gate —
  see below.
- **Fork-gate (r8 F2) correctness on Android — RESOLVED, not a regression.** The audit
  flagged a risk: the gate keys off `SingBoxFeatures.Probe()` which always returns
  (false,false) on Android (no sing-box binary) → it rejects ALL AWG/xhttp custom
  configs. **Verified the bundled `libbox.so` build tags**: `with_wireguard, with_quic,
  with_utls, with_clash_api, with_v2ray_api` — **NO `with_awg`, NO `with_xhttp`** (0
  amnezia symbols; the 3 "xhttp" hits are coincidental substrings of http2/https/mux
  URLs; `unknown transport type:` reject path present). So SFA-libbox genuinely can't
  run AWG/xhttp → the gate correctly rejects what would otherwise FATAL. Pre-r8 = opaque
  `unknown transport` FATAL; post-r8 = actionable up-front error → strict IMPROVEMENT.
  Only the message copy ("use lx core build") is desktop-worded → ledgered P3 (not
  cut-blocking).
- **Inert on Android (compiled-not-invoked):** `AndroidSingBoxRuntime.IsRunningAsync`
  bearer (type never instantiated), `ConfigGenerator` urltest R5/R3 pool hygiene
  (`ServerHealthStore` never written on Android), `LeakProtection.ValidateProxyEndpoint`
  (unreachable + warn-only).
- **Desktop-only, NO device test (confirmed):** deep-verify refactor #4 (`VlessDeepVerifier`,
  `DeepVerifyProbe`, desktop `RuntimeStatusDetector`/`SingBoxFeatures.Prewarm`, F1 cancel
  phase), r9 `ServerTesting` AllowConcurrentExecutions + all `VPNRouter.App` changes,
  `ClashSingBoxApi`/`ClashLogStream`/`SingBoxManager.HotReload`. (Android has NO
  ProjectReference to VPNRouter.App.)

## Cross-refs

memory: `android-local-build-toolchain`, `mac-android-host`,
`v2.46.0-cycle-state`, `v2.47.0-cycle-and-drive-roadmap`. Plans: Drive handoff
Android P0.1/P0.2, `cut-stable` skill Step 5.6, `plans/OPEN-DEFECTS.md`,
`plans/urltest-deferred-decisions-2026-07-09.md`.
