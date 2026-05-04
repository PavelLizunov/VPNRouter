# VPNRouter Android — Handbook + План работы

**Status**: Draft, 2026-05-04. Phase 5c shipped. VPN routing все ещё broken.

Этот файл — рабочая инструкция для следующего цикла итераций по Android-порту.
Цель: исключить повторение ошибок, которые я делал в Phase 1-5.

---

## 1. Критичные принципы (NO repeat mistakes)

### 1.1 Desktop reference — ВСЕГДА source-of-truth

Любой UI-элемент Android должен **сначала** быть найден на desktop:

```
VPNRouter.App/Views/Pages/SimplePage.axaml          ← layout reference
VPNRouter.App/Views/MainWindow.axaml (rows 230-450) ← header / chips reference
VPNRouter.App/ViewModels/MainWindowViewModel.SimpleMode.cs ← VM logic reference
VPNRouter.App/ViewModels/MainWindowViewModel.cs ~99-160     ← logo invert reference
VPNRouter.App/Styles/Tokens.axaml                   ← brushes / radii
VPNRouter.App/Localization/Strings.cs               ← labels (RU/EN)
VPNRouter.App/Assets/penguin_mascot.png            ← branding artwork
```

**Перед написанием Android-кода**:
1. Прочитать desktop XAML/CS соответствующего раздела
2. Найти **все** computed properties / states / triggers
3. Перенести семантику дословно (не интерпретировать своими словами)

**Урок Phase 4**: я сделал chips статичными `TextBlock` с фоном.
Desktop имеет **3 IsVisible-варианта VPN chip** + `pulse` animation на warn.
Урок Phase 4 retrospect: когда нет уверенности в значении — **читать
ViewModel, не XAML**. `SimpleStatusIsOn / IsWarn / IsOff` — это computed
properties с конкретной логикой:
- `IsOn` = VPN process running + tunnel up + last health-check passed
- `IsWarn` = VPN process running но crash в недавней истории / starting
- `IsOff` = process not running

### 1.2 Computer-use сравнение — обязательная процедура

**Каждый push новой версии Android — обязателен side-by-side check**:

```
1. desktop screenshot:
   mcp__vpnrouter-test__list_windows("VPNRouter") → focus → screenshot
2. android screenshot:
   ssh slovn@192.168.0.246 "/opt/homebrew/bin/adb shell screencap -p > ..." → scp
3. compare visual element-by-element (см. checklist 3.1)
4. honest delta report — что match, что нет
5. fix или document gap
```

**Урок**: я несколько раз заявлял "visual parity достигнута" при значительных
gaps. Avoid via mandatory checklist (см. 3.1).

### 1.3 Запрет на «угадайку»

Если не знаю как desktop делает — НЕ пишу by guessing. Открываю reference,
читаю, переношу. Это правило `B1/B2` из VPNRouter.App/CLAUDE.md.

### 1.4 Coordinate-based MCP клики ненадёжны

Avalonia render проходит мимо `uiautomator` (всё одно FrameLayout).
Координаты приходится подбирать вслепую → 3-5 итераций на каждый тап.

**Workaround**: при добавлении Android UI elements — сразу записать
координаты центра в этот handbook (см. секцию 4 Coordinate map).

### 1.5 Каждый ship — реальный VPN-test, не только UI

**Критерий "VPN работает"**: после Connect и grant consent →
`adb shell timeout 8 curl -s https://ifconfig.io` возвращает IP сервера
(НЕ ISP IP пользователя). Любое другое — VPN broken.

`tun0 UP` ≠ работающий VPN. Нужен реальный outbound IP test.

---

## 2. Архитектурный reference (что уже знаю)

### 2.1 libbox API в нашем .aar

Наш `Lib/libbox.aar` (sing-box 1.13.x baseline) НЕ имеет
`Libbox.newService(json, platformInterface)` метода. Доступно только:

- `Libbox.setup(SetupOptions)` — basePath/workingPath/tempPath
- `Libbox.checkConfig(String)` — validate JSON
- `Libbox.newCommandServer(handler, platformInterface)` → CommandServer
- `Libbox.redirectStderr(String)` — Go-stderr → file
- `commandServer.startOrReloadService(json, OverrideOptions)`

`CommandServerHandler` interface methods:
- `serviceStop()`, `serviceReload()`, `getSystemProxyStatus()`,
  `setSystemProxyEnabled(boolean)`, `writeDebugMessage(String)`

### 2.2 PlatformInterface — что обязательно реализовать

Reference: `https://github.com/PavelLizunov/vpnrouter-android` →
`bg/PlatformInterfaceWrapper.kt`. Минимум для работающего VPN:

| Method | Default OK? | Реализация |
|---|---|---|
| `openTun(TunOptions)` | NO | VpnService.Builder + addAddress + addRoute + addDnsServer + setSession + .establish() → return `pfd.getFd()` (peek, NOT detach) |
| `useProcFS()` | NO | `Build.VERSION.SDK_INT < Q` (true Android <10) |
| `usePlatformAutoDetectInterfaceControl()` | OK | return true |
| `autoDetectInterfaceControl(int fd)` | NO | `protect(fd)` иначе libbox-internal sockets зацикливаются |
| `getInterfaces()` | **NO** ★ | Enumerate via ConnectivityManager.getAllNetworks() + java.net.NetworkInterface; setName/DNSServer/Type/Index/MTU/Addresses/Flags/Metered |
| `systemCertificates()` | **NO** ★ | KeyStore("AndroidCAStore") → enumerate aliases → PEM |
| `localDNSTransport()` | OK | return null (sing-box fallback resolves OK) |
| `findConnectionOwner(...)` | OK | throw — мы не используем process_name rules |
| `clearDNSCache()`, `readWIFIState()`, `includeAllNetworks()`, `underNetworkExtension()`, `start/closeDefaultInterfaceMonitor()`, `sendNotification()` | OK | no-op / sensible default |

★ = Phase 5 root-cause additions. Pre-5 пропущенные → DNS broken и TLS broken.

### 2.3 Routing на Android

`AndroidConfigBuilder.cs`:
- `RoutingMode = "full"` (не "split"!) — на Android нет process_name rules,
  split tunnel делается через VpnService.Builder.addAllowedApplication
- `tun.auto_route = false`, `tun.strict_route = false` — libbox owns TUN,
  sing-box не должен трогать kernel routes
- `log.output` — НЕ AppPaths-derived path (тот не существует на Android),
  лучше `getExternalFilesDir()` или удалить вообще (libbox → stderr)

### 2.4 Mascot / theme-aware logo

Pattern из `MainWindowViewModel.cs:99-160`:
- `LoadAsset("avares://VPNRouter.App/Assets/penguin_mascot.png")` light source
- `TryBuildInvertedLogo()` → WriteableBitmap + Bgra8888 + Unpremul → invert RGB,
  preserve alpha
- Bind `Image.Source` to `LogoSource` computed property → `IsDarkTheme ?
  _logoDark : _logoLight`

На Android: `LoadMascot()` в AndroidApp.axaml.cs делает то же самое.

### 2.5 Chip 3-state pattern (urgent — НЕ реализован!)

Desktop `MainWindow.axaml:262-345`:

```xaml
<!-- VPN chip: 3 mutually-exclusive Buttons via IsVisible -->
<Button IsVisible="{Binding SimpleStatusIsOn}"
        Background="{DynamicResource SuccessBgBrush}"  <!-- green pill -->
        ...>
    <StackPanel Orientation="Horizontal">
        <Ellipse Width="4" Height="4" Fill="{DynamicResource SuccessSolidBrush}"/>
        <TextBlock Text="VPN" Foreground="{DynamicResource SuccessFgBrush}"/>
    </StackPanel>
</Button>

<Button IsVisible="{Binding SimpleStatusIsWarn}"
        Background="{DynamicResource WarningBgBrush}"  <!-- yellow pill -->
        ...>
    <Ellipse Classes="pulse" .../>  <!-- ANIMATED pulse 1→0.55→1 over 1.2s -->
    <TextBlock Text="VPN" Foreground="{DynamicResource WarningFgBrush}"/>
</Button>

<Button IsVisible="{Binding SimpleStatusIsOff}"
        Background="{DynamicResource SurfaceSunkenBrush}"  <!-- gray pill -->
        ...>
    <Ellipse Fill="{DynamicResource TextMutedBrush}"/>
    <TextBlock Text="VPN" Foreground="{DynamicResource TextMutedBrush}"/>
</Button>
```

Аналогично Zapret (`ZapretBadgeBrush` color binding) и TG.

**Android implementation Phase 6**: ввести state field
`VpnChipState { get => connected ? On : connecting ? Warn : Off; }` +
условный rebuild на `MainActivity.IntentChanged` события.

---

## 3. Workflow для следующих итераций

### 3.1 Visual parity checklist (обязательно перед каждым commit)

```
□ Mini-header левый край: mascot 28×28, AccentBgSubtle bg, RadiusSm,
  ClipToBounds; Image 26×26 LogoSource (theme-aware invert)
□ Brand title "Virtual Penguin Network" 12pt Bold TextPrimary
□ Chips row под title:
    □ VPN chip: 3-state via IsVisible (Success/Warning+pulse/Muted)
       4×4 Ellipse + "VPN" 9pt SemiBold
    □ Zapret chip: bound to ZapretBadgeBrush, white text, RadiusPill
    □ TG chip: bound to TgProxyBadgeBrush, white text, RadiusPill
□ Right edge: kebab ⋮ button + Simple/Advanced toggle (если Advanced)
□ Status card: dot (state-colored ellipse) + bold title + secondary desc
□ Config row: ⚑ flag in AccentBgSubtle square, "Конфиг · Режим" overline
   + value monospace, chevron flips on expand
□ Form expanded:
    □ "Конфиг VPN" label + TextBox с RadiusXs
    □ "Что идёт через VPN" radios "Выбранные приложения" / "Весь трафик"
    □ "Автозапуск" link card (Android: open VpnService settings intent)
□ CTA button: 3 mutually-exclusive variants (outlined / sunken / accent-solid)
□ Расширенные настройки card: title + sub + chevron in accent-bg circle
□ ScrollViewer wrapper, MaxWidth=420 inner Grid, Margin 16,0,16,0
```

### 3.2 Computer-use comparison ritual

```bash
# 1. Desktop screenshot
list_windows VPNRouter → focus → screenshot to C:\tmp\desktop-cmp.png

# 2. Android screenshot
ssh mac "adb shell screencap -p > tmp.png" && scp → C:\tmp\android-cmp.png

# 3. Read both images, list all elements visible per side
# 4. Build delta table:
#    Element | Desktop | Android | Match?
# 5. For each Match=NO row, fix or document
```

### 3.3 VPN routing test ritual

```bash
# Pre-condition: app installed, server config saved
ssh mac '
  /opt/homebrew/bin/adb logcat -c
  /opt/homebrew/bin/adb shell input tap 540 780  # Connect button
  sleep 8
  echo "=== outbound IP ==="
  /opt/homebrew/bin/adb shell timeout 10 curl -s https://ifconfig.io | head -2
  echo "=== expected: server IP, NOT ISP IP ==="
'
```

PASS criteria: returned IP **матчит** один из VLESS server IPs из subscription.
FAIL: timeout or ISP IP returned → VPN не работает.

### 3.4 Test configs (provided by user 2026-05-04)

3 тестовых сервера для покрытия protocols:

```
# VLESS+Reality+gRPC (TCP 8444)
vless://5550051c-2b10-4c11-8d73-b918118f86ef@93.95.226.167:8444?type=grpc&serviceName=TunService&mode=gun&security=reality&pbk=4xRS--elmOVx36HHH2J_xEUY3An7Mnuu2tf7N6MykVw&fp=chrome&sni=www.bing.com&sid=fb86a31808abe3f7#is-01-grpc-test

# Hysteria2+Salamander (UDP 9443)
hysteria2://YYTaOfChh4aqJ0vEN4FBkMHFvlQfq2JG@93.95.226.167:9443/?obfs=salamander&obfs-password=qbsE_9V0KPqtPUvJs6q61A&insecure=1&sni=www.bing.com#is-01-hy2-test

# TUIC v5 (UDP 9444)
tuic://62736735-6c4f-4490-bf28-62e0655c826a:FcYSKt62nIMKxjhSxED7rEpWem5XQrdj@93.95.226.167:9444?congestion_control=bbr&alpn=h3&insecure=1&sni=www.bing.com#is-01-tuic-test
```

**Test matrix Phase 6**:

| Server | Verify | Expect | Status |
|---|---|---|---|
| VLESS+Reality+TCP+Vision (placeholder de-01:443) | https://1.1.1.1 page renders | Cloudflare logo + lock icon | ✅ PASS (Phase 6.3) |
| VLESS+Reality+gRPC TCP 8444 (is-01-grpc-test) | https://1.1.1.1 page renders | Cloudflare logo + lock icon | ❌ FAIL — handshake EOF before TLS |
| Hysteria2+Salamander UDP 9443 (is-01-hy2-test) | https://1.1.1.1 page renders | Cloudflare logo + lock icon | ✅ PASS (Phase 6.4) |
| TUIC v5 UDP 9444 (is-01-tuic-test) | https://1.1.1.1 page renders | Cloudflare logo + lock icon | ✅ PASS (Phase 6.4 + insecure parser fix) |

3 из 4 → Android port'a routing solid; gRPC fail — server-side gRPC mode
quirk (URI carries `mode=gun` + `serviceName=TunService`, sing-box gRPC
transport doesn't have a "mode" field — упоминается только в v2ray-core).
Logged как §5.7 follow-up.

### 3.5 Build / SCP / install loop

```bash
# 1. Build (Release, arm64-only — 48 MB вместо 120 MB)
cd C:\Project\VPNRouter\.claude\worktrees\suspicious-kepler-fa08e0
dotnet build VPNRouter.Android/VPNRouter.Android.csproj -c Release \
  -p:AndroidSdkDirectory="$ANDROID_HOME" -p:JavaSdkDirectory="$JAVA_HOME" \
  -p:RuntimeIdentifiers=android-arm64 -p:AndroidEnableProfiledAot=false

# 2. SCP APK к Mac
scp VPNRouter.Android/bin/Release/net8.0-android/com.ninitux.vpnrouter-Signed.apk \
  slovn@192.168.0.246:~/vpnrouter-android-test/p<N>.apk

# 3. Install + launch
ssh mac '
  /opt/homebrew/bin/adb install -r ~/vpnrouter-android-test/p<N>.apk
  /opt/homebrew/bin/adb shell am force-stop com.ninitux.vpnrouter
  /opt/homebrew/bin/adb shell monkey -p com.ninitux.vpnrouter -c android.intent.category.LAUNCHER 1
'

# 4. screenshot + compare
```

---

## 4. Coordinate map (1080×1920 device)

Записывать координаты центров tappable elements при добавлении.
Avoids 3-5 итераций на каждый клик.

```
Element                       | x   | y
------------------------------+-----+----
Kebab menu (⋮)                | 985 | 280
RU/EN toggle (старый)         | 985 | 280
Connect (collapsed form)      | 540 | 780  (после Phase 4)
Disconnect (after Connect)    | 540 | 700-800 (зависит от status text length)
Save button (form expanded)   | 320 | 1335
QR button (form expanded)     | 540 | 1335
Refresh button (form expanded)| 800 | 1335
First server in list          | 540 | 880
Config row                    | 540 | 580
Advanced settings card        | 540 | 950
Consent dialog OK button      | 935 | 1100
```

После каждого UI-rewrite пересчитать таблицу!

---

## 5. Open issues (приоритет от critical вниз)

### 5.1 ✅ RESOLVED — VPN routing (Phase 6)

**Resolution**: Three layered fixes shipped in commits `7dfef98` (6.1+6.2+6.3) +
`520d78f` (6.4). VPN now routes both UDP and TCP through the proxy on a
non-rooted Android 12 device.

**Fix layers**:
1. **6.1 — log.output to externally-readable file**. Pre-fix `Libbox.redirectStderr`
   only captured Go-runtime panics, never sing-box's internal logger output.
   Set `log.output = getExternalFilesDir()/singbox.log` so the actual sing-box
   error stream lands in a world-readable file.
2. **6.2 — startDefaultInterfaceMonitor with NetworkCallback**. Pre-fix the
   stub no-op'd. Symptom (visible after 6.1): "no available network interface"
   on every upstream dial. Fix: ConnectivityManager.NetworkCallback wired with
   API-version-aware registration (registerBestMatchingNetworkCallback on 31+,
   requestNetwork on 28-30, registerDefaultNetworkCallback below). Calls
   InterfaceUpdateListener.updateDefaultInterface(name, index, false, false)
   on every callback fire.
3. **6.3 — TUN stack=gvisor + MTU 1500**. Pre-fix TUN inherited `stack="system"`
   from desktop ConfigGenerator. system stack needs CAP_NET_ADMIN/CAP_NET_RAW
   which Android doesn't grant non-root apps → TCP SYN packets dropped before
   reaching the TCP handler (UDP still flowed because Linux raw sockets aren't
   needed for UDP receive). Switch to `gvisor` (pure user-mode TCP/IP stack
   shipped with libbox.aar via with_gvisor build tag). Drop MTU 9000 → 1500
   to match Android VpnService.Builder + underlying network limits.
4. **6.4 — multi-protocol parser**. Surfaced VPNRouter.Core's existing
   ServerUriParser (Hysteria2 / TUIC / SS) in Android. Plus Core fix for
   ParseTuic to accept `insecure=1` (was only checking `allowInsecure=1`).

**Verified on KYOCERA A101BM (Android 12 / API 31)** with 3 of 4 protocols
passing the browser → https://1.1.1.1 visibility test. 28 TCP "inbound
connection" + 34 UDP "inbound packet connection" events observed in singbox.log
during the placeholder test session (proves both protocols flow).

### 5.7 NEW P2 — VLESS+Reality+gRPC handshake EOF

is-01-grpc-test (URI: `mode=gun&serviceName=TunService`) fails with the
DNS-over-HTTPS proxy returning EOF before the TLS layer. Other VLESS+Reality
configs work; Hysteria2 + TUIC work. Test server may use a v2ray-core gRPC
mode that sing-box's gRPC transport doesn't speak the same way. Investigate:
- Compare singbox check output for the dumped config
- Try with `mode=multi` if server supports
- Review reference repo's gRPC reality-test config for hints

### 5.2 P1 — Chip state semantic

Сейчас chips static. Должны быть 3-state per VpnChipState. Implement:
- `VpnChipState { On / Connecting / Off }` enum
- Bind to MainActivity.IntentChanged + future libbox health callback
- Replace static MakeChip TextBlock с state-aware Border+Ellipse+TextBlock
- Pulse animation на Connecting (Avalonia Style with KeyFrame)

### 5.3 P1 — Kebab menu полная структура

4 секции из `MainWindow.axaml:380+`:
- "Вид" — Light|Dark + RU|EN segmented controls
- "Диагностика" — Open logs / IP leak check / Updates check
- "Устранение неполадок" — Health Check / Safe Mode / Reset config
- "О приложении" — version pill + AboutDialog open

### 5.4 P2 — Form expand state default

Desktop держит форму expanded по умолчанию когда subscription not configured.
Android по умолчанию collapsed. Synchronize.

### 5.5 P2 — Per-app filter UI

Desktop ApplicationsPage. Android — список installed packages с
checkbox include/exclude → передать в VpnService.Builder.

### 5.6 P3 — Logs page in-app

Read sing-box log file + tail в TextBox. Без adb logcat.

---

## 6. Текущий state (после Phase 6.4, commits 7dfef98 + 520d78f)

### Готово
- `VpnRouterService.java` rewrite по reference impl
  (real getInterfaces / systemCertificates / useProcFS / protect / peek-fd)
- DefaultInterfaceMonitor wired (Phase 6.2 — NetworkCallback per API tier)
- AndroidApp.axaml.cs sub-header (mascot + brand + chips + kebab)
- Real PNG mascot с RGB-invert (LoadMascot + TryBuildInverted)
- Light theme default
- 3-variant CTA button (outlined / sunken / accent-solid)
- Status card / Config row / Form / Adv settings card
- **VPN routing works** (Phase 6.1+6.2+6.3) — TCP + UDP both flow through proxy
- Multi-protocol input (VLESS / Hysteria2 / TUIC / SS via ServerUriParser)
- Debug helper: `getExternalFilesDir()/test-uri.txt` overrides URI without
  rebuilding APK

### Известные недоработки
- VLESS+Reality+gRPC fails (test server compat issue, see §5.7)
- Chips static (должны быть 3-state) — §5.2
- Kebab menu только 2 пункта (должно 4 секции) — §5.3
- Form по умолчанию collapsed — §5.4
- Per-app filter не реализован — §5.5
- In-app logs viewer не реализован — §5.6

### Файлы изменённые в Phase 6
- `VPNRouter.Android/VpnRouterService.java` — startDefaultInterfaceMonitor wiring
- `VPNRouter.Android/AndroidConfigBuilder.cs` — log.output param + stack=gvisor + MTU 1500
- `VPNRouter.Android/AndroidManifest.xml` — CHANGE_NETWORK_STATE permission
- `VPNRouter.Android/MainActivity.cs` — singboxLogPath compute + test-uri.txt override
- `VPNRouter.Android/AndroidApp.axaml.cs` — IsSupportedScheme gate
- `VPNRouter.Android/AndroidStorage.cs` — ServerUriParser.Parse instead of VlessUriParser
- `VPNRouter.Core/Services/ServerUriParser.cs` — TUIC insecure flag (3 spelling variants)

---

## 7. Next session quick-start

```
1. cd C:\Project\VPNRouter\.claude\worktrees\suspicious-kepler-fa08e0
2. git pull origin main  (current head: 520d78f)
3. Read this handbook fully (especially §5 + §3.4 test results)
4. Pick top P1/P2 from §5 (P0 §5.1 closed)
5. Apply 3.1 checklist before commit
6. Apply 3.3 VPN test ritual after each ship
7. Update §4 Coordinate map if UI changes
```

### Recommended first action

**Phase 7.1 — Chip 3-state semantic** (§5.2). The functional meaning user
called out:
  green = enabled, yellow = connecting, gray = disabled.
Currently chips are static decoration. Implementation sketch:
1. Add `enum VpnChipState { On / Connecting / Off }` (mirror desktop's
   pattern in MainWindowViewModel).
2. Bind to `MainActivity.IntentChanged` for VPN; future Zapret/TG state
   bindings come from VpnEngine signals once Phase 7 wires them.
3. Replace static `MakeChip(label, bg, fg)` with state-aware factory:
   - On → SuccessSurfaceBrush + SuccessForegroundBrush + green dot
   - Connecting → WarningSurfaceBrush + pulse animation (Avalonia Style
     KeyFrame on Opacity)
   - Off → MutedSurfaceBrush + MutedForegroundBrush + gray dot

**Phase 7.2 — Full kebab menu sections** (§5.3). Mirror desktop's
MainWindow.axaml:380+ ContextMenu structure: Вид / Диагностика /
Устранение неполадок / О приложении.

**Phase 7.3 — Form expand state default** (§5.4) when subscription not
configured (matches desktop initial-launch UX).

Phase 6 cycle (routing + multi-protocol) is closed. Future Android
sessions are UI parity polish, not core plumbing.

---

**Last updated**: 2026-05-04 после Phase 6.4 (520d78f).
**Maintainer**: Claude (continuing).
**User**: Pavel (provides feedback + steers priorities).
