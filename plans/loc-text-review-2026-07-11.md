# VPNRouter Localization / Text-Review — Ranked Report

66 confirmed findings deduplicated to **54 unique defects**. Overlaps collapsed: `ColPing` (4→1), `SplitTunnel/FullTunnel` (2→1), `Connected` (2→1), `QrShareScanHint` (2→1), emoji rule-#9 (7→1 batch). Ranked highest user impact first; theme tag in brackets.

---

## Tier 1 — Broken-looking or meaning-wrong RU in common UI

1. **[RU-quality] SettingsSectionReliability** — `VPNRouter.Core/Localization/Strings.Android.cs:12` — RU «Резервирование» means *redundancy/backup*, not reliability; the section is about keeping the VPN up (Always-on/Doze/reconnect). **Fix:** RU → «Надёжность». *(only HIGH-severity item)*

2. **[RU-quality/untranslated] SettingsRecoveredFromBadConfig** — `VPNRouter.Core/Localization/Strings.cs:1283-1284` — RU string leaks three English words and breaks agreement: «Config был повреждён, восстановлен default. Backup: {path}». **Fix:** «Конфиг был повреждён, восстановлены настройки по умолчанию. Резервная копия: {backupPath}» (both branches of the member).

3. **[RU-quality] RulesImported / RulesExported** — `VPNRouter.Core/Localization/Strings.cs:424, 436` — no numeral declension: count=1 → «1 правил» (should be «правило»), 2-4 → «правила». **Fix:** safe colon pattern «Импортировано правил: {count} [{format}]» / «Экспортировано правил: {count}…».

4. **[RU-quality] SyncComplete** — `VPNRouter.Core/Localization/Strings.cs:113` — same declension bug: «Получено 1 серверов». **Fix:** «Получено серверов: {count}» (matches existing RefreshOk pattern).

5. **[parity/divergence] ColPing** — `VPNRouter.App/Localization/Strings.cs:246` — App wrapper hard-codes EN-only `"Ping"`, overriding Core's bilingual «Пинг»/«Ping»; RU desktop users see an English column header on the Servers list. Self-documented as known TEXT-DRIFT; every sibling `Col*` delegates. **Fix:** `public static string ColPing => global::VPNRouter.Core.Localization.Strings.ColPing;`

6. **[terminology] MenuSectionFreeConfigs / FcOverlayTitle** — `VPNRouter.Core/Localization/Strings.cs:1819, 1823` — feature was deliberately renamed Free→«Публичные»/Public, but these two entry points still say «Бесплатные конфиги»/«Free configs» — and RU «Бесплатные» reintroduces the exact "no-cost" connotation the rename removed. **Fix:** → «Публичные конфиги»/«Public configs».

## Tier 2 — Untranslated English tokens inside RU UI (D1 leaks)

7. **[terminology] SplitTunnel / FullTunnel** — `VPNRouter.Core/Localization/Strings.cs:129, 130` — RU branch keeps «Split Tunnel»/«Full Tunnel» while the Settings cards render «Раздельный туннель»/«Полный туннель». **Fix:** RU → «Раздельный туннель (выбранные приложения)» / «Полный туннель (весь трафик)».

8. **[terminology] Android DPI-bypass labels** — `VPNRouter.Core/Localization/Strings.Android.cs:209-211, 309, 312` (SettingsDpiBypassLabel/DpiBypassOverlayTitle/ToolsTabZapret) — RU left as English «DPI bypass»; desktop RU uses «Обход блокировок»/«Обход DPI». **Fix:** RU → «Обход блокировок (Zapret)» / «Обход DPI».

9. **[parity] FieldPublicKey / FieldShortId / LblPublicKey / LblShortId** — `VPNRouter.Core/Localization/Strings.cs:667, 668, 1234, 1235` — no-op `Ru ? "X" : "X"` ternaries put English «Public Key:»/«Short ID:»/«Pub Key:» into an otherwise-Russian form (Имя:/Сервер:/Порт:). **Fix:** RU → «Открытый ключ:» / «Короткий ID:», or if keeping the protocol term drop the pointless ternary.

10. **[parity] AdvSimpleToggle** — `VPNRouter.Core/Localization/Strings.cs:94` — «← Simple» both languages; localized equivalent exists (SmpToggleToSimple «◂ Простой»). **Fix:** RU → «← Простой» (and align the arrow glyph with the doc-comment).

11. **[RU-quality] SmpCfgCustom** — `VPNRouter.Core/Localization/Strings.SimpleMode.cs:192` — RU branch is literal English «custom» next to «вручную»/«подписка». **Fix:** → «свой».

12. **[parity] FcDashboardTimeout** — `VPNRouter.Core/Localization/Strings.FreeConfigs.cs:14` — «Timeout»/«Timeout» among translated siblings. **Fix:** RU → «Таймаут».

## Tier 3 — RU register / word-choice quality

13. **[RU-quality] AutostartLoginAppDescription Unix/Windows** — `VPNRouter.Core/Localization/Strings.cs:588, 592` (+ App duplicate) — «стартануть» is slang, wrong register for Settings. **Fix:** → «запустить».

14. **[RU-quality] MtuWarningHigh vs MtuAutoTuneBlocked** — `VPNRouter.Core/Localization/Strings.cs:712, 724` — ты/вы clash («Попробуй» vs «выключите/меряйте»). **Fix:** unify formal: «Попробуйте 1400, затем 1380.» and «измеряйте».

15. **[RU-quality] ReliabilityAutoReconnectHint vs ExternalControlHint** — `VPNRouter.Core/Localization/Strings.Android.cs:72, 80` — adjacent rows clash «Отключи» (ты) vs «Включайте» (вы). **Fix:** pick one register for the section.

16. **[RU-quality] AutoFailoverCustomMode** — `VPNRouter.App/Localization/Strings.cs:921` — «Кастомный конфиг» anglicism vs app's «Свой конфиг». **Fix:** → «Свой конфиг не отвечает…».

17. **[RU-quality] FcStatusBatched\*** — `VPNRouter.Core/Localization/Strings.FreeConfigs.cs:44, 47, 50` — «батч» dev-slang in a user status line. **Fix:** → «группа».

18. **[RU-quality] CcValidationFailed / CcSaveStatusInvalid** — `VPNRouter.Core/Localization/Strings.Android.cs:173, 189` — «валидно/валиден» anglicism. **Fix:** → «некорректно»/«некорректен» (soft polish).

19. **[RU-quality] AboutTagline** — `VPNRouter.Core/Localization/Strings.SimpleMode.cs:279` — «Процесс-VPN роутер» is an awkward calque. **Fix:** → «VPN-роутер на основе процессов, с поддержкой обхода DPI.».

20. **[RU-quality] EmergencyChannelInstall(+Embedded)** — `VPNRouter.App/Localization/Strings.cs:1406, 1408` — Latin «MB» in RU; rest of app uses «МБ». **Fix:** → «~10 МБ» / «~120 МБ».

## Tier 4 — Terminology consistency (cross-surface, cosmetic)

21. **[terminology] SyncButton / Syncing** — `VPNRouter.Core/Localization/Strings.cs:111, 112` — RU «Обновить» button but «Синхронизация…» progress; EN «Sync» vs «Refresh» elsewhere. **Fix:** one term per language (RU «Обновить»+«Обновление…», or switch to «Refresh»).

22. **[terminology] EmergencyChannelConfigsLabel / AddConfig** — `VPNRouter.App/Localization/Strings.cs:1412, 1414` — «Конфигурация»/«Configuration» vs app-wide «конфиг»/«config». **Fix:** → «Конфиг:» / «Добавить конфиг».

23. **[terminology] SrvTestAll / SrvTestOne / SrvTesting** — `VPNRouter.Core/Localization/Strings.cs:1757, 1759, 1761` (+AdvServersTestAll) — Android «Тест»/«Тестирую…» vs desktop «Проверить»/«Проверка». **Fix:** align on «Проверить…».

24. **[terminology] ZapretOneTapStartButton** — `VPNRouter.Core/Localization/Strings.Telegram.cs:197` — RU «обход блокировок» vs «обход DPI» (badge/section desc). **Fix:** choose one canonical RU phrase across button/title/tooltip/descriptions.

25. **[terminology] FcRetestAll / FcSavedRecheckAllBtn / ZapretReverifyButton** — `Strings.FreeConfigs.cs:86, 166` + `Strings.cs:330` — EN uses Retest/Recheck/Re-verify for one concept. **Fix:** standardize EN verb (e.g. «Recheck»).

26. **[terminology] ServerDeepStop** — `VPNRouter.Core/Localization/Strings.cs:187` — RU «Стоп» vs every other «Остановить». **Fix:** → «Остановить» (or «Остановить проверку»).

27. **[terminology] ServerTestCancel vs SmpCtaCancel** — `Strings.cs:185, 1059` vs `SimpleMode.cs:199`, `Strings.cs:347` — «Отмена» vs «Отменить» for EN «Cancel». **Fix:** pick one (noun «Отмена» for buttons).

28. **[terminology] ModeCustomConfig vs CcModeCustom** — `Strings.cs:105` vs `Strings.Android.cs:146` — «Свой конфиг»/«Custom Config» vs «Свой JSON»/«Custom JSON». **Fix:** align (e.g. «Свой конфиг (JSON)»).

29. **[terminology] CheckLeaks / SmpMenuCheckLeaks / AndroidToolsCheckLeak** — `Strings.cs:694`, `SimpleMode.cs:218`, `Strings.Android.cs:353` — three RU+EN phrasings. **Fix:** standardize on «Проверить IP-утечку»/«Check IP leak».

30. **[terminology] PingUnavailableWhenConnected** — `VPNRouter.Core/Localization/Strings.cs:198` — «тоннель» (о-spelling), the sole outlier vs app-wide «туннель» (30 uses). **Fix:** → «туннель».

## Tier 5 — EN quality

31. **[EN-quality] ColPingTooltip** — `VPNRouter.Core/Localization/Strings.cs:470` — tooltip says click "Check all" but the button is "Test all" (wayfinding break, EN-only). **Fix:** → «…click "Test all".».

32. **[EN-quality] FcFastScanHint** — `VPNRouter.Core/Localization/Strings.FreeConfigs.cs:208` — runglish word order "marks as 'working' even honeypots". **Fix:** "marks even honeypots as 'working' … Deep Verify filters them out afterwards.".

33. **[EN-quality] ZapretSelectedStrategyFailed** — `VPNRouter.Core/Localization/Strings.cs:277` — "possibly AV blocking winws.exe or pick another" doesn't parse. **Fix:** "…antivirus may be blocking winws.exe, or try another one.".

34. **[EN-quality] CustomConfigsEmptyHint** — `VPNRouter.Core/Localization/Strings.cs:1273` — "a ready sing-box JSON file" calque of «готовый». **Fix:** "a ready-made sing-box JSON file".

35. **[EN-quality] FcSecWarnHeader** — `VPNRouter.Core/Localization/Strings.FreeConfigs.cs:256` — "connecting to a public proxy operator" (wrong object; RU is correct). **Fix:** "…public proxy server".

36. **[EN-quality] TipZapretAutoUpdate** — `VPNRouter.Core/Localization/Strings.cs:1179` — lowercase "zapret" (prior UX-52 fix missed this spot) + EN-only "from Flowseal". **Fix:** "Check for Zapret updates every 24 hours".

37. **[EN-quality] QrShareScanHint** — `VPNRouter.Core/Localization/Strings.cs:1647` — British "recognises" vs American "recognized" in sibling QR toasts. **Fix:** → "recognizes".

## Tier 6 — Hardcoded English (no localization)

38. **[hardcoded] ShowMenuFeedback "Error:" ×6** — `VPNRouter.Android/AndroidApp.axaml.cs:2098, 2164, 2206` + `AndroidApp.PerAppFilter.cs:1192, 1228, 1252` — English toast prefix. **Fix:** `Localization.ErrorPrefix => Ru ? "Ошибка" : "Error"`, interpolate (type-name payload stays English).

39. **[hardcoded] "Diagnostics export failed"** — `VPNRouter.Android/AndroidApp.Notifications.cs:262` — English toast. **Fix:** `Localization.DiagExportFailed`.

40. **[hardcoded] "Diagnostics export error"** — `VPNRouter.Android/AndroidApp.Notifications.cs:269` — English toast. **Fix:** `Localization.DiagExportError`.

41. **[hardcoded] "Apply failed: {msg}"** — `VPNRouter.App/ViewModels/MainWindowViewModel.cs:3382` — catch branch EN-only while sibling `else` (3376) is localized. **Fix:** `$"{(IsRussian ? "Не удалось применить" : "Apply failed")}: {ex.Message}"`.

42. **[hardcoded] "files"** — `VPNRouter.Android/AndroidApp.Notifications.cs:256` — hardcoded in success toast. **Fix:** localized count label with «файлов».

43. **[hardcoded] ToolTip.Tip="Dismiss" ×5** — `VPNRouter.App/Views/MainWindow.axaml:227, 260, 293, 363` + `DpiBypassPage.axaml:147` — English banner-close tooltips. **Fix:** `Strings.TipDismiss => Ru ? "Скрыть" : "Dismiss"` bound via `L_` getter (pattern: TipCloseServerDetail).

44. **[hardcoded] StatusText mode tokens** — `VPNRouter.App/ViewModels/MainWindowViewModel.RuntimeStatus.cs:233-234` — subscribe/manual/custom + split/full stay English inside localized «Подключено через службу [{mode}]». **Fix:** map tokens through localized short labels, or ponytail-comment as intentionally technical.

45. **[hardcoded] "Activity not available"** — `VPNRouter.Android/AndroidApp.ConfigShare.cs:456, 498` — English reason injected into localized ExportFailed/ImportFailedRead template (rare null-activity path). **Fix:** localize the reason.

## Tier 7 — Symbols / emoji / typos (rule #9 + consistency)

46. **[symbols] Emoji rule-#9 batch** — `Strings.FreeConfigs.cs` lines 73-77, 119, 129, 141, 144, 205, 212-216, 225, 261-265 (📁🧹⭐💥📺💬🚀📋👤⚡🎯⚙🚫✅); `Strings.cs:1443` (🔑, also RU/EN asymmetry vs EN "VPN-key icon"); `Strings.cs:556` + `Strings.Android.cs:193-198` (⛔); `Strings.cs:42, 552, 1360` + `Strings.Android.cs:40` (⚠, review). Violates rule #9 (only ✓ ✗ → · ║ allowed). **Fix (batch decision):** replace 🚫→✗, ✅→✓, drop leading pictographs from labels; spell 🔑 as «VPN-ключа» in RU; decide ⚠ policy once and apply uniformly.

47. **[symbols] Ellipsis mix** — `VPNRouter.Core/Localization/Strings.cs:112` ("Синхронизация..." ASCII) vs `:1335` ("Подключение…" Unicode). **Fix:** standardize on Unicode «…» across progress strings.

48. **[symbols] En-dash vs hyphen** — `VPNRouter.Core/Localization/Strings.cs:335` — RU «2–5» en-dash vs EN «2-5» hyphen (only en-dash range in the file). **Fix:** RU → «2-5».

## Tier 8 — Drift / dead code (low, no live mismatch today)

49. **[parity] LblCustomBadge** — `VPNRouter.Core/Localization/Strings.cs:1245` — «custom»/«custom» app-row badge vs translated «Свои» elsewhere. **Fix:** RU → «свой» (or «Свои»).

50. **[parity] ZapretSecHosts** — `VPNRouter.Core/Localization/Strings.Zapret.cs:21` — bare «Hosts» among translated section tabs. **Fix:** RU → «Хосты» (or intentional bare literal).

51. **[parity] ReliabilityAlwaysOnTitle** — `VPNRouter.Core/Localization/Strings.Android.cs:19` — no-op ternary «Always-on VPN»; likely intentional OS-label mirror. **Fix:** collapse to bare literal `=> "Always-on VPN";` or verify against RU Android label.

52. **[divergence] AppsMode\* / AppsList\*** — `VPNRouter.App/Localization/Strings.cs:874-895` — desktop-only include/bypass labels; Android uses Core PerAppMode\*; two sources of truth (already drifted). **Fix:** promote to Core (self-flagged in-code). 

53. **[divergence] Connected** — `VPNRouter.App/Localization/Strings.cs:86` — App re-implements method byte-identical to Core:148; Android delegates. **Fix:** delegate to `Core.Strings.Connected(mode, serverName, serverIp)`.

54. **[divergence] AutostartLoginAppDescription Unix/Windows private dups** — `VPNRouter.App/Localization/Strings.cs:363-369` — dead unreachable private copies of Core:587-593 (public getter already delegates). **Fix:** delete the two dead members.

---

## The 5 fixes to do first

1. **`Strings.Android.cs:12` — «Резервирование» → «Надёжность».** The only meaning-changing mistranslation (HIGH); a section header that currently misleads every Russian Android user.
2. **`Strings.cs:1283-1284` — SettingsRecoveredFromBadConfig.** RU recovery banner leaks «Config»/«default»/«Backup» plus broken agreement — looks outright broken. → «Конфиг был повреждён, восстановлены настройки по умолчанию. Резервная копия: {backupPath}».
3. **`Strings.cs:424, 436, 113` — numeral declension** (RulesImported/Exported + SyncComplete). «1 правил»/«1 серверов» is visibly ungrammatical on common counts; fix all three with the safe «…: {count}» pattern (helpers already exist).
4. **`VPNRouter.App/Localization/Strings.cs:246` — ColPing delegate to Core.** One-line, self-documented drift; removes an English «Ping» header from the primary RU Servers list.
5. **`Strings.cs:1819, 1823` — Free→Public.** Restores a deliberate design rename and stops RU «Бесплатные» from re-implying "no-cost". Fold in the other English-in-RU label leaks (SplitTunnel/FullTunnel `:129`, Android DPI-bypass `Strings.Android.cs:209/309/312`) in the same pass — same class, same file neighborhood.

Notes: all `-rN` fixes above are Core/App/Android localization-string edits (no functional surface), so they batch cleanly into one candidate. The emoji rule-#9 batch (#46) needs a one-time user policy call (strip vs. sanction ⚠) before mechanical replacement.