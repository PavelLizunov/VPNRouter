# Network / Settings / Apps page checklist

Use when release notes mention autostart / DNS lockdown / custom rules /
firewall / split tunnel / process scanner / Apps tab.

## Setup

1. Window already launched.
2. Click "Расширенные настройки" → "Настройки" tab.

## Sub-section sweep

The Settings tab is split into sub-tabs. Verify each per release notes:

### Routing sub-section

3. Verify two radio cards visible:
   - "Раздельный туннель" / "Split Tunnel"
   - "Полный туннель" / "Full Tunnel"
4. Switch between them, verify state persists after Stop+Start.

### Leak Protection sub-section

5. Verify checkboxes:
   - "Только IPv4 (защита от IPv6-утечек)"
   - "Очищать DNS кэш при подключении"
   - "Строгий DNS (весь DNS через VPN)"
   - "Блокировать DNS вне VPN (защита от утечек)" — Wave 39 (r5+)
6. Toggle each, click "Применить", verify hot-reload works (no full
   disconnect).

### Updates sub-section

7. "Канал обновлений": ● Стабильная / ⚠ Эксперимент.
8. "Проверить обновления" button. Click it.
9. **For r12 change (UpdateNotificationViewModel CTS-swap)**:
   - Click "Check for updates" 5× rapidly.
   - Status should NOT flicker `UpToDate → Default → Failed → Default`.
   - Only the LAST click's reset fires after 3s; earlier ones cancel.
   - Log should show no exceptions on rapid clicks.

### Autostart sub-section

10. Verify two sections:
    - "На старте Windows (до логина)" — service-based.
    - "При входе пользователя" — HKCU\Run + LaunchAgent + .desktop.
11. Toggle "Автозапуск VPN" + click Apply. Verify check persists.

## Custom Rules page (Network tab)

12. Click "Сеть" → scroll to "Правила" / "Rules" section.
13. **For r9 change (Custom Rules import/export localization)**:
    - Click "Импорт правил" (Import rules) button.
    - In file picker, cancel without selecting.
    - **Pre-r9**: no message would show.
    - **Post-r9**: validation slot shows «Не удалось открыть диалог
      выбора файлов» (or "file picker open failed" hint).
14. Click "Экспорт правил" / "Export rules" when list is empty.
15. Validation slot should show «Нечего экспортировать — список правил
    пуст».

### Rule type hints (r13/r14)

16. In the Add-rule form, change "Тип" / "Type" dropdown to each value.
17. The hint text below should update:
    - `domain` → «точное имя (discord.com)»
    - `ip_cidr` → «IPv4/IPv6 + маска (10.0.0.0/8)»
    - `process_name` → «имя процесса (Discord.exe)»
    - etc.

## Apps tab

18. Click "Приложения" tab.
19. **For Split Tunnel mode**: app group cards visible, checkboxes
    selectable.
20. **For Full Tunnel mode**: cards greyed (50% opacity), banner
    visible: "[Switch to split tunnel]" button + warning text.
21. Click "Switch to split tunnel" button to restore.

## Per-feature log checks

| Looking for | Pattern |
|---|---|
| Settings saved | `[SettingsLoader] Saved config.yaml` |
| Apply triggered | `[VpnEngine] Apply` |
| Hot-reload succeeded | `[SingBoxManager] Hot-reload via Clash API` |
| Custom rule added | `[CustomRulesParser]` |
| Autostart registered | `[AutostartHelper]` |

## Pass criteria summary

- All sub-tabs render.
- Toggles persist after Stop+Start.
- Apply hot-reloads without disconnect.
- Rule type hints localized (r13/r14).
- Import/export shows localized validation (r9).
- Update check doesn't flicker on rapid clicks (r12).

## Screenshots to attach

- `tmp-rN-settings-routing.png`
- `tmp-rN-settings-leak.png`
- `tmp-rN-settings-updates.png`
- `tmp-rN-rules-import-failed.png` — localized validation
- `tmp-rN-apps-fulltunnel-banner.png`
