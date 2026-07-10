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

## Cross-refs

memory: `android-local-build-toolchain`, `mac-android-host`,
`v2.46.0-cycle-state`, `v2.47.0-cycle-and-drive-roadmap`. Plans: Drive handoff
Android P0.1/P0.2, `cut-stable` skill Step 5.6, `plans/OPEN-DEFECTS.md`,
`plans/urltest-deferred-decisions-2026-07-09.md`.
