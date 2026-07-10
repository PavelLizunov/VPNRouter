# ТЗ (Codex): split-tunnel — sing-box crash-loop + TUN-already-exists, не восстанавливается

## Симптом (от пользователя)
В split-tunnel режиме «вылеты»/дропы. Диаг: `C:\Project\logs\Дропы на split моде.zip`
(VPNRouter v2.45.0-r6, Windows 11, Connected=True на момент снятия).

## Что уже найдено (из диага, leads)
1. sing-box **crash-loop**: в `vpnrouter20260629.log` десятки записей
   `[WRN] [SingBoxManager] === sing-box crash tail (last 50 of N lines) ===`
   — sing-box многократно падает/перезапускается.
2. На рестарте — **FATAL**:
   `[sing-box] FATAL start service: start inbound/tun[tun-in]: configure tun interface:
   Cannot create a file when that file already exists.`
   → осиротевший TUN-адаптер от предыдущего нечистого выхода не вычищен ПЕРЕД
   рестартом, поэтому sing-box не может пересоздать `VPNRouter-TUN` → падает снова →
   петля. (В норме `TunAdapterDiagnostics` pre-start cleanup убирает stale-адаптеры —
   на windows-brat видно `pre-start cleanup: removed 1 TUN adapter(s)`.)
3. `[Firewall] Created 17 block rules (disabled — will enable on VPN crash)` —
   kill-switch взведён; проверить, не он ли (или его enable-on-crash) усугубляет петлю.

## Задача
Найти и починить ДВЕ вещи:
- **(A) Первичную причину падения** sing-box (почему он падает В ПЕРВЫЙ раз, до
  TUN-конфликта). Crash tail в `singbox-tail.log` (~4.5 МБ) и
  `singbox-old-tail.log`. Искать panic / fatal / конкретную ошибку перед exit'ом.
  Возможные направления: split-tunnel route-правила (current.json в диаге, 8 КБ —
  больше outbounds/rules чем full-tunnel), process_name matching, конкретный
  outbound, MTU, kill-switch interaction.
- **(B) Невосстановление**: гарантировать, что pre-start TUN-cleanup
  (`TunAdapterDiagnostics`) выполняется на КАЖДОМ (ре)старте sing-box, включая путь
  авто-рестарта `HealthMonitor`/`SingBoxManager`, а не только на первом
  `StartupPipeline`. Если адаптер держится handle-ом упавшего процесса —
  добавить kill+wait+remove перед пересозданием. Чтобы «TUN already exists» больше
  не загонял в петлю.

## Где смотреть
`VPNRouter.Core/Services/SingBoxManager.cs` (lifecycle, Stop/Restart, crash
detection + crash-tail logging), `HealthMonitor.cs` (авто-рестарт с backoff),
`TunAdapterDiagnostics.cs` (pre-start cleanup / `DisableOrphanedAdapter` /
`PreStartCleanupAsync` / `TryRemoveAdapterAsync`), `StartupPipeline.cs` (где
pre-start cleanup вызывается сейчас), `FirewallManager.cs` (enable-on-crash).
Тесты: `TunAdapterReadinessTests.cs`, `TunAdapterDiagnostics*Tests.cs`,
`SingBoxManager*Tests.cs`, `HealthMonitorRecoveryGapTests.cs`.

## Deliverable
1. Root cause (A) с `file:line` + первичной ошибкой из crash tail.
2. Фикс (A) + фикс (B: cleanup на restart-пути) с xUnit-тестами.
3. Не ломать: kill-switch fail-closed, AWG (v2.45.0-r6), full-tunnel путь.
4. Если первичная причина (A) — внешняя/средовая (а не баг кода), всё равно (B)
   обязателен: краш не должен превращаться в неубиваемую петлю.
5. Перед шипом — regression + (если есть доступ) live-проверка split-режима на
   dev-боксе/windows-brat (`tools/testvm-control.ps1`).

## Не трогать
AWG-ядро (v2.45.0-r6) и `tools/build-singbox-lx.ps1`. Это отдельный, не связанный
со split-багом код.
