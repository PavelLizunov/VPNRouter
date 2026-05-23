# v2.36 — Android 5-min auto-disconnect + health probe path mismatch

**Authored**: 2026-05-23, после v2.35.3 stable cut
**Trigger**: User EOStārāTheia report (Telegram, 12:57 + 15:17) — Android VPN
auto-disconnects ~5 минут после Connect, с warning "Проверка не отвечает"
показывающим уже на 32 секундах connection time.
**Authority**: 3-agent audit (Phase 4 prep agents, post v2.35.3-r1) +
Android-wide audit agent + manual code-walk verification.

## Триггер

User screenshot показал:
- `Подключено · 0:32` (VPN connected 32 секунды)
- `⚠ Проверка не отвечает` (Check not responding)
- Update banner: `Доступна v2.35.3 · 82,4 МБ`

User's verbatim:
- "А еще я хотела сказать, что в телефонной версии когда таймер доходит до 5 минут то он автоматически отключается"
- "Может быть это что-то связано с проверкой, он сначала посылает запрос проверки потом она не отвечает и видимо если не отвечает он отключается через какое-то время"

## Symptoms

1. **Always-visible "Проверка не отвечает" warning** — показывается уже через
   30s после Connect для каждого Android user.
2. **Auto-disconnect ~5 минут** после Connect, без user action.

## Root cause analysis

### Symptom 1 — health probe path mismatch (F1)

**Confirmed via code-walk.**

`VPNRouter.Android/AndroidApp.VpnLifecycle.cs:574` (pre-fix) читал sing-box
log из:
```csharp
var extDir = ctx.GetExternalFilesDir(null);
// → /sdcard/Android/data/com.ninitux.vpnrouter/files/singbox.log
```

`VPNRouter.Android/MainActivity.cs:848-851` (Bug-AND-011 / Critical-1
2026-05-16) перенёс sing-box log в:
```csharp
var filesDir = FilesDir;
// → /data/data/com.ninitux.vpnrouter/files/singbox.log
```

**Path mismatch**: writer пишет в private sandbox (`FilesDir`), reader
читает из external storage (`GetExternalFilesDir`). Файл по пути reader'а
просто не существует — `File.Exists(logPath)` возвращает false →
`_lastHealthOk = false` → "Проверка не отвечает" warning показывается
permanently regardless of actual tunnel health.

Bug introduced когда Critical-1 фикс security-сensitive log path moved
to private sandbox, но health probe не обновили.

### Symptom 2 — 5-min auto-disconnect (F4, hypothesis HIGH confidence)

**Probable cause**: missing keepalive / idle timeout settings в sing-box
config that we generate via `AndroidConfigBuilder.BuildConfigJson`.

Sing-box (libbox) outbound VLESS dialer не имеет default idle timeout
expressly, но **VLESS server-side** typically закрывает idle TCP
connections after 60-300 секунд. Combined with Android Doze mode kicking
in после ~5 min screen-off → app TCP socket idle → server drops → libbox
sees connection close → tunnel down.

Other Android-specific contributors:
- MIUI / Xiaomi / Realme background app killers (~5 min default kill window)
- Foreground service systemExempted type ratio: requires user-granted
  battery optimization exemption на most OEMs

Не воспроизводится без real phone testing. Mac+phone (slovn@192.168.0.246
+ A101BM device) — environment для repro.

## Fix strategy

### Fix F1 — health probe path correction (this iteration)

`VPNRouter.Android/AndroidApp.VpnLifecycle.cs:574`:
```csharp
// before
var extDir = ctx.GetExternalFilesDir(null);
var logPath = System.IO.Path.Combine(extDir.AbsolutePath, "singbox.log");

// after (v2.36)
var filesDir = ctx.FilesDir;
var logPath = System.IO.Path.Combine(filesDir.AbsolutePath, "singbox.log");
```

**Risk**: LOW. One-line read-side fix. Path now matches writer side.
Test: install fixed APK on phone → Connect → wait 32 секунд → warning
DOES NOT appear (sing-box log file now exists at correct path → probe
finds it → `_lastHealthOk = true`).

### Fix F4 — sing-box keepalive config (this iteration if time permits)

`VPNRouter.Android/AndroidConfigBuilder.cs` (or `Core/ConfigGenerator.cs`
if shared with desktop) — добавить per-outbound keepalive settings:

```csharp
// In BuildConfigJson / outbound generation
outbound["dialer"] = new JsonObject {
    ["timeout"] = "30s",
    ["tcp_keep_alive"] = "30s",
    ["tcp_keep_alive_initial"] = "30s",
};
// or via VLESS-specific:
outbound["multiplex"] = new JsonObject {
    ["enabled"] = true,
    ["protocol"] = "smux",
    ["max_streams"] = 4,
    ["padding"] = false,
};
```

**Risk**: MEDIUM. Sing-box config changes can break ConfigGenerator pass
tests. Need to verify:
- `sing-box check` passes on generated JSON
- Server (Virtual Penguin Network) supports the keepalive mechanism
- Не ломает desktop generation (если изменения в shared ConfigGenerator)

**Test**: install fixed APK → Connect → wait 5-10 минут → tunnel STAYS
connected.

### Live repro on phone (slovn@192.168.0.246 + A101BM)

1. Build new APK с F1 fix locally (~5-7 min).
2. SCP / curl на Mac.
3. `adb install -r` на phone.
4. Configure subscription (Virtual Penguin Network URL).
5. Connect.
6. **Pre-fix baseline**: warning appears at 32s (current install).
7. **Post-F1**: warning should NOT appear.
8. Wait 5+ minutes → verify tunnel still connected.
9. If still disconnects → confirms F4 needed → ship F4 fix.

### Diagnostic improvements (optional, v2.36.x follow-up)

- **TCP-ping Clash API** (127.0.0.1:9090) instead of log-file probe.
  Clash API is sing-box's internal HTTP endpoint — if responding,
  sing-box is alive AND processing requests. Removes dependence on
  log file path / mtime semantics entirely.
- **Expand VpnRouterService.java logging** через `Log.i` для phase
  transitions (start / tunnel-up / network-change / stop). На next
  user-report'е лог будет показывать что именно убило tunnel.

## Acceptance

- [ ] F1 fix landed: path mismatch closed, behavioural test шows warning
  goes away on live phone.
- [ ] (optional) F4 fix landed: tunnel stays connected 10+ минут on
  live phone test.
- [ ] APK rebuilt + uploaded to v2.36.0-r1 release (or hotfixed into
  v2.35.4 if scope changes).
- [ ] `dotnet build -c Release` → 0 errors.
- [ ] No regression в existing Android tests.
- [ ] MCP live test от phone PASS: connect / wait 32s / verify NO warning /
  wait 5+ min / verify still connected.
- [ ] Reply to EOStārāTheia with update notice.

## Оценка

- **F1**: 30 минут (1-line fix + verify + ship APK).
- **F4**: 1-2 часа (config change + sing-box check verify +
  cross-platform desktop regression + ship APK).
- **Live test**: 30 мин (включая 5-min watch).
- **Plan + commit + release**: 30 мин.
- **Total**: 2-4 часа в зависимости от scope (F1 only vs F1+F4).

## Связь с другими планами

- `plans/singbox-lifecycle-hardening-v2.36.md` — B1+B2 fixes (Core
  side), shipped 087993a. F1/F4 — Android-side companion в same v2.36
  cycle.
- `VPNRouter.Android/CLAUDE.md` — Android port architecture, libbox.aar
  setup, Bug-AND-011 / Critical-1 context для path mismatch reason.
- `plans/release-notes-v2.35.3.md` — что было в last stable, F1/F4 не
  закрыты тогда (audit нашёл их после ship).
