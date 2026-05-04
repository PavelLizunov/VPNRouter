using System;
using System.Globalization;

namespace VPNRouter.Android;

/// <summary>
/// v3.0 Phase 1.H (2026-05-04) — bilingual UI strings (RU/EN), mirrors
/// the desktop <c>VPNRouter.App.Localization.Strings</c> pattern. Static
/// getters branch on the <see cref="Ru"/> flag.
///
/// <para>Initial language is resolved in this priority order:
/// <list type="number">
///   <item>Explicit user choice persisted via
///   <see cref="AndroidStorage.GetLanguage"/> (last toggle).</item>
///   <item>System Locale (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName)
///   — "ru" → Ru=true, anything else → English.</item>
///   <item>Fallback: English.</item>
/// </list></para>
///
/// <para><see cref="ToggleAndPersist"/> flips Ru and writes the choice
/// back to SharedPreferences so the next app launch starts in the same
/// language. Phase 2 will swap to a fully reactive scheme (INotifyPropertyChanged
/// on a singleton VM); for Phase 1.H the AndroidApp manually re-reads
/// each label after toggle.</para>
/// </summary>
internal static class Localization
{
    public static bool Ru { get; private set; }

    public static void LoadFromStorage()
    {
        var stored = AndroidStorage.GetLanguage();
        if (string.Equals(stored, "ru", StringComparison.OrdinalIgnoreCase))
        {
            Ru = true;
            return;
        }
        if (string.Equals(stored, "en", StringComparison.OrdinalIgnoreCase))
        {
            Ru = false;
            return;
        }

        // No explicit choice — guess from system Locale.
        try
        {
            var lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            Ru = string.Equals(lang, "ru", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            Ru = false;
        }
    }

    public static void ToggleAndPersist()
    {
        Ru = !Ru;
        AndroidStorage.SetLanguage(Ru ? "ru" : "en");
    }

    // ── Header ─────────────────────────────────────────────────────────

    public static string Title => "VPNRouter v3.0";
    public static string Subtitle => Ru
        ? "Android · вставь VLESS-URI или подписочный URL и подключись"
        : "Android · paste a VLESS URI or subscription URL and connect";

    public static string LangToggleLabel => Ru ? "EN" : "RU";

    // ── Phase 4: sub-header parity with desktop ────────────────────────

    public static string BrandTitle => Ru ? "Virtual Penguin Network" : "Virtual Penguin Network";

    public static string MenuLanguageLabel => Ru ? "Язык: Русский" : "Language: English";
    public static string MenuThemeLabel => Ru ? "Тема: переключить" : "Theme: toggle";

    // ── Status / Connect button ─────────────────────────────────────────

    public static string StatusConnected => Ru ? "Подключено" : "Connected";
    public static string StatusDisconnected => Ru ? "Отключено" : "Disconnected";
    public static string ButtonConnect => Ru ? "Подключить" : "Connect";
    public static string ButtonDisconnect => Ru ? "Отключить" : "Disconnect";
    public static string ButtonConnecting => Ru ? "Подключение…" : "Connecting…";

    // ── SimplePage parity (Phase 3) ────────────────────────────────────

    public static string SimpleStatusTitleOn => Ru ? "Подключено" : "Connected";
    public static string SimpleStatusTitleOff => Ru ? "Не подключено" : "Not connected";
    public static string SimpleStatusDescOn => Ru
        ? "Трафик идёт через VPN-туннель."
        : "Traffic is routed through the VPN tunnel.";
    public static string SimpleStatusDescOff => Ru
        ? "Трафик идёт напрямую — выбери конфиг и запусти туннель."
        : "Traffic goes direct — pick a config and start the tunnel.";

    public static string SmpConfigRowLabel => Ru ? "Конфиг · Режим" : "Config · Mode";
    public static string SmpSourceManual => Ru ? "вручную" : "manual";
    public static string SmpSourceSubscription => Ru ? "подписка" : "subscription";
    public static string SmpInputLabel => Ru ? "Конфиг VPN" : "VPN Config";
    public static string SmpInputWatermark => Ru
        ? "vless://… или https://…/sub"
        : "vless://… or https://…/sub";
    public static string SmpInputHint => Ru
        ? "Приму vless://-ссылку или URL подписки (http/https)."
        : "Accepts a vless:// share-link or subscription URL (http/https).";
    public static string SmpTunnelModeLabel => Ru ? "Что идёт через VPN" : "What goes via VPN";
    public static string SmpSplitOption => Ru ? "Выбранные приложения" : "Selected apps";
    public static string SmpSplitHint => Ru
        ? "По списку выбранных приложений (расширенные настройки)"
        : "By selected apps list (advanced settings)";
    public static string SmpFullOption => Ru ? "Весь трафик" : "All traffic";
    public static string SmpFullHint => Ru
        ? "Включая игры и банки"
        : "Including games and banks";
    public static string SmpAdvCardTitle => Ru ? "Расширенные настройки" : "Advanced settings";
    public static string SmpAdvCardSubtitle => Ru
        ? "Серверы · Подписки · Маршрутизация · Логи"
        : "Servers · Subscriptions · Routing · Logs";

    public static string SimpleConfigSummary => Ru ? "вручную · полный" : "manual · full";

    // ── Server input ────────────────────────────────────────────────────

    public static string ServerHeader => Ru ? "Сервер" : "Server";
    public static string ServerInputWatermark => Ru
        ? "vless://… или https://…/sub"
        : "vless://… or https://…/sub";
    public static string ButtonSave => Ru ? "Сохранить" : "Save";
    public static string ButtonRefresh => Ru ? "Обновить" : "Refresh";

    public static string ServerInputHintInitial => Ru
        ? "Введи vless://-ссылку или подписочный URL и нажми «Сохранить». Для подписки потом «Обновить»."
        : "Paste a vless:// share-link or a subscription URL, then tap Save. For a subscription URL, tap Refresh after.";

    public static string SaveStatusCleared => Ru
        ? "Сервер очищен. Будет использован встроенный placeholder."
        : "Server cleared. The built-in placeholder will be used.";
    public static string SaveStatusUriBadHost => Ru
        ? "URI распарсен, но не хватает host или порта. Проверь."
        : "Parsed but missing host or port — please double-check.";
    public static string SaveStatusUriOk => Ru
        ? "Сохранено. Сервер: {0}:{1}. Жми «Подключить»."
        : "Saved. Server: {0}:{1}. Tap Connect.";
    public static string SaveStatusUriInvalid => Ru
        ? "Невалидный VLESS URI: {0}"
        : "Invalid VLESS URI: {0}";
    public static string SaveStatusSubStored => Ru
        ? "URL подписки сохранён. Жми «Обновить» чтобы скачать список серверов."
        : "Subscription URL saved. Tap Refresh to fetch the server list.";
    public static string SaveStatusUnknown => Ru
        ? "Не похоже ни на vless://, ни на http(s):// — проверь ввод."
        : "Doesn't look like vless:// or http(s):// — check the input.";

    public static string RefreshNeedsUrl => Ru
        ? "Сначала сохрани подписочный URL (https://…)."
        : "Save a subscription URL first (https://…).";
    public static string RefreshFetching => Ru ? "Скачиваю…" : "Fetching…";
    public static string RefreshOk => Ru
        ? "Получено серверов: {0}. Выбери из списка ниже."
        : "Fetched {0} servers. Pick one below.";
    public static string RefreshFailed => Ru
        ? "Не удалось скачать: {0}"
        : "Refresh failed: {0}";

    // ── Server list ─────────────────────────────────────────────────────

    public static string AvailableServers => Ru ? "Доступные серверы" : "Available servers";
    public static string ServerSelected => Ru
        ? "Выбран: {0} ({1}:{2})"
        : "Selected: {0} ({1}:{2})";

    // ── QR scan (Phase 2.4 placeholder) ────────────────────────────────

    public static string QrComingSoon => Ru
        ? "QR-сканер появится в следующем апдейте — пока вставляй URI вручную."
        : "QR scanner is coming in the next update — paste the URI manually for now.";

    // ── Bottom hint ─────────────────────────────────────────────────────

    public static string HintTunnel => Ru
        ? "Состояние туннеля повторяет иконку 🔑 в строке состояния."
        : "Tunnel state mirrors the system VPN-key icon in the status bar.";
}
