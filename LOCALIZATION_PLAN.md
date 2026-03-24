# RUS/ENG Localization Plan

## Step 1: AppSettings.cs
Add to AppConfig: `[YamlMember(Alias = "language")] public string Language { get; set; } = "en";`

## Step 2: Create VPNRouter.GUI/Localization/Strings.cs
Static class, ~75 properties. Pattern: `public static string X => L == "ru" ? "RU" : "EN";`

### Strings to translate:
```
// Buttons
StartVPN: Start VPN / Запустить VPN
StopVPN: Stop VPN / Остановить VPN
AddServers: Add Server(s) / Добавить сервер(ы)
Remove: Remove / Удалить
ClearAll: Clear All / Очистить все
AddConfig: Add Config... / Добавить конфиг...
Apply: Apply Changes / Применить
Up: ▲ Up / ▲ Вверх
Down: ▼ Down / ▼ Вниз

// Tabs
ServersTab: Servers / Серверы
AppsTab: Applications / Приложения

// Config mode
VlessServers: VLESS Servers / VLESS Серверы
CustomConfig: Custom Config (JSON) / Свой конфиг (JSON)
PasteVless: Paste VLESS URI(s): / Вставьте VLESS URI:

// Apps tab
SplitTunnel: Split Tunnel (selected apps) / Split Tunnel (выбранные приложения)
FullTunnel: Full Tunnel (all traffic) / Full Tunnel (весь трафик)
CustomAppsLabel: Add custom app (.exe): / Добавить приложение (.exe):
AddApp: Add / Добавить
RemoveChecked: Remove checked / Удалить выбранные

// Status
NotConnected: Not connected / Не подключено
Connected: Connected / Подключено
Stopping: Stopping... / Остановка...

// Header
CheckUpdates: Check for updates / Проверить обновления
Dark: Dark / Тёмная
Light: Light / Светлая
Experimental: Experimental / Экспериментальная
Stable: Stable / Стабильная

// Hints
TcpUdpHint: TCP/UDP split active — TCP servers handle browsing/chat, UDP servers handle voice/video
  / TCP/UDP разделение — TCP серверы для браузинга, UDP для голоса/видео
CustomConfigHint: Double-click to set active config / Двойной клик для выбора активного конфига

// Dialogs
FailedStart: Failed to start VPN: / Не удалось запустить VPN:
AdminRequired: Administrator rights required / Требуются права администратора
NoProfile: No profile specified / Профиль не указан
ConfigExists: Config already exists / Конфиг уже существует
InvalidConfig: Invalid config / Некорректный конфиг

// Tray
TrayStart: Start VPN / Запустить VPN
TrayStop: Stop VPN / Остановить VPN
TraySettings: Settings... / Настройки...
TrayExit: Exit / Выход

// Update
UpdateAvailable: Update available / Доступно обновление
UpToDate: You're up to date / Обновления не требуются
Downloading: Downloading... / Загрузка...
Installing: Installing... / Установка...

// Service
AutostartWindows: Autostart with Windows / Автозапуск с Windows
RestartService: Restart Service / Перезапустить службу
ReinstallService: Reinstall Service / Переустановить службу
```

## Step 3: MainForm.cs
- Add `_langToggle` LinkLabel in header (Location ~404,46), text "RUS"/"ENG"
- Click handler: toggle language, save, call ApplyLanguage()
- `ApplyLanguage()`: update all .Text properties from Strings.XXX
- Replace ALL hardcoded strings with Strings.XXX references

## Step 4: TrayApplicationContext.cs
Replace menu item strings with Strings.XXX

## Step 5: Build, test, release v1.22.0
