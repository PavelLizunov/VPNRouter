using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Platform;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.FreeConfigs;
using VPNRouter.App.Localization;
using VPNRouter.App.ViewModels.FreeConfigs;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel
{
    // ── Telegram proxy commands ──

    [RelayCommand]
    private async Task UpdateTgProxyAsync()
    {
#if PLATFORM_WINDOWS
        if (IsTgProxyDownloading) return;
        IsTgProxyDownloading = true;
        TgProxyStatus = IsRussian ? "Загрузка tg-ws-proxy..." : "Downloading tg-ws-proxy...";
        TgProxyDownloadStep = string.Empty;

        try
        {
            // Stop if running
            if (TgProxyEnabled || TgProxyManager.IsAnyRunning(TgProxyPort))
            {
                _tgProxy?.Stop();
                TgProxyManager.KillAll(TgProxyPort);
                TgProxyEnabled = false;
            }

            var updater = new TgProxyUpdater(_logger);
            updater.StatusChanged += s =>
                Dispatcher.UIThread.Post(() =>
                {
                    // v2.36 (MVP one-button task A): per-step messages
                    // from TgProxyUpdater carry "Step N/3:" prefix.
                    // Mirror them into both the persistent status banner
                    // (for backward-compatible logs / older bindings)
                    // and the new TgProxyDownloadStep property that the
                    // page banner can render distinctly. Non-step
                    // messages (e.g. final "Installed v1.6.5") clear
                    // the step badge naturally.
                    TgProxyStatus = s;
                    TgProxyDownloadStep = s.StartsWith("Step ") ? s : string.Empty;
                });

            await updater.DownloadAsync(CancellationToken.None);

            TgProxyVersionText = TgProxyUpdater.GetLocalVersion() ?? "?";
            TgProxyStatus = IsRussian
                ? $"tg-ws-proxy {TgProxyVersionText} установлен"
                : $"tg-ws-proxy {TgProxyVersionText} installed";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] TgProxy download failed");
            TgProxyStatus = $"Download error: {ex.Message}";
        }
        finally
        {
            IsTgProxyDownloading = false;
            TgProxyDownloadStep = string.Empty;
        }
#endif
    }

    [RelayCommand]
    private async Task ToggleTgProxyAsync()
    {
#if PLATFORM_WINDOWS
        // If running → stop
        if (TgProxyEnabled || TgProxyManager.IsAnyRunning(TgProxyPort))
        {
            _tgProxy?.Stop();
            // v2.20.0: pass the port so KillByPort hits the actual
            // python.exe running the proxy (process-name match never
            // worked — see TgProxyManager.KillAll).
            TgProxyManager.KillAll(TgProxyPort);
            // Re-check a beat later; if the port is still bound
            // something couldn't be killed (permissions? zombie?).
            // We surface the truth instead of lying that we stopped.
            await Task.Delay(300);
            TgProxyRuntimeStatus = TgProxyManager.IsAnyRunning(TgProxyPort)
                ? ComponentRuntimeStatus.Failed
                : ComponentRuntimeStatus.Idle;
            TgProxyEnabled = false;
            TgProxyStatus = TgProxyRuntimeStatus == ComponentRuntimeStatus.Failed
                ? (IsRussian ? "Не удалось остановить (проверьте права)" : "Couldn't stop (check permissions)")
                : (IsRussian ? "Остановлен" : "Stopped");
            TgProxyStats = "";
            // v2.36.0-r7 (task #63 / MCP test r6 finding): wrap SaveSettings
            // in try/catch. Pre-r7 a concurrent reader of config.yaml (AV scan,
            // Dropbox sync, another shell briefly reading the file) would
            // surface as an IOException here that propagated uncaught from
            // this async-void path and fatally killed the GUI process. Crash
            // report shipped 2026-05-24 18:16:14 reproduced this exact path.
            // Settings save is best-effort: the in-memory state stays correct,
            // next Save attempt (e.g. on app shutdown or next toggle) will
            // persist. Logging surfaces the failure for diagnosis.
            try { SaveSettings(); }
            catch (System.IO.IOException ex)
            {
                _logger.Warning(ex, "[VM] TgProxy Stop: SaveSettings failed (file lock?), keeping in-memory state");
            }
            return;
        }

        // v2.31.10: Service-side AutostartTgProxyAsync logs entry/decision
        // breadcrumbs with the same shape as below. When the App-side
        // AutostartTgProxyAsync from the DBG-2 sister task lands, lift this
        // structured log pattern verbatim (entry → IsInstalled(_logger) →
        // secret-len + port → ResilientStarter → outcome) so manual-start
        // logs and autostart logs share grep'able prefixes.
        // TODO(DBG-2 sister): once VPNRouter.App has its own
        // AutostartTgProxyAsync, mirror the [Service] AutostartTgProxyAsync
        // entry/decision logs in VPNRouterService.cs:331+ exactly.
        _logger.Information("[VM] ToggleTgProxyAsync: start path entered");

        // Auto-download if not installed.
        // r37: auto-update on every start if installed but upstream newer.
        // 6-hour TTL cache via RemoteVersionChecker keeps GitHub-API quiet.
        if (!TgProxyUpdater.IsInstalled(_logger))
        {
            await UpdateTgProxyAsync();
            if (!TgProxyUpdater.IsInstalled(_logger)) return;
        }
        else
        {
            try
            {
                var remoteTag = await VPNRouter.Core.Services.RemoteVersionChecker.GetLatestTagAsync(
                    TgProxyUpdater.ProxyRepoPublic,
                    userAgent: $"VPNRouter/{VPNRouter.Core.AppVersion.Version}",
                    _logger,
                    System.Threading.CancellationToken.None);
                var localTag = TgProxyUpdater.GetLocalVersion();
                if (VPNRouter.Core.Services.RemoteVersionChecker.IsNewer(remoteTag, localTag))
                {
                    _logger.Information(
                        "[VM] TgProxy: update available {Local} → {Remote}, auto-applying",
                        localTag, remoteTag);
                    TgProxyStatus = IsRussian
                        ? $"Обновление TgProxy до {remoteTag}…"
                        : $"Updating TgProxy to {remoteTag}…";
                    await UpdateTgProxyAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[VM] TgProxy: remote version check failed (non-fatal)");
            }
        }

        try
        {
            // Generate secret if empty
            if (string.IsNullOrWhiteSpace(TgProxySecret))
            {
                var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
                TgProxySecret = Convert.ToHexString(bytes).ToLowerInvariant();
            }

            _logger.Information(
                "[VM] ToggleTgProxyAsync: secret configured (len {SecretLen}), port {Port}, calling TgProxyManager.Start",
                TgProxySecret.Length, TgProxyPort);

            if (_tgProxy == null)
            {
                _tgProxy = new TgProxyManager(_logger);
                // M1 (v2.45.0): subscribe ONCE per manager lifetime via a named
                // handler. The old `+= lambda` ran on EVERY toggle start with no
                // matching `-=`, so handlers accumulated (duplicate UI updates) and
                // rooted the VM; Dispose() now detaches it + disposes the manager.
                _tgProxy.StatsUpdated += OnTgProxyStats;
            }
            _tgProxy.Start(TgProxyPort, TgProxySecret);

            // v2.36 (MVP one-button task C): pre-flight scheme check
            // after spawn succeeded but BEFORE the user is told to
            // open Telegram. Banner is non-blocking — proxy keeps
            // running. The check is cheap (registry probe) and
            // returns true on non-Windows + on any registry error
            // (defensive — don't show false-positive banner).
            IsTelegramSchemeWarningVisible = !TgProxyManager.IsTelegramSchemeRegistered();

            // Verify it actually started
            await Task.Delay(TgProxySettleDelayMs);
            if (_tgProxy.IsRunning || TgProxyManager.IsAnyRunning(TgProxyPort))
            {
                TgProxyEnabled = true;
                TgProxyLink = TgProxyManager.BuildProxyLink("127.0.0.1", TgProxyPort, TgProxySecret);
                TgProxyStatus = IsRussian
                    ? $"Работает (PID {_tgProxy.Pid})"
                    : $"Running (PID {_tgProxy.Pid})";
            }
            else
            {
                TgProxyEnabled = false;
                TgProxyStatus = IsRussian
                    ? "Ошибка: tg-ws-proxy завершился сразу."
                    : "Error: tg-ws-proxy exited immediately.";
            }
            // v2.36.0-r7 (task #63): same defensive wrap as the Stop branch
            // above. The outer try/catch at line ~4605 would catch IOException
            // here today, but routing it as "TgProxy start failed" is
            // misleading — Start actually succeeded, only persistence didn't.
            // Explicit narrow catch keeps the user's runtime state intact.
            try { SaveSettings(); }
            catch (System.IO.IOException ex)
            {
                _logger.Warning(ex, "[VM] TgProxy Start: SaveSettings failed (file lock?), keeping in-memory state");
            }
        }
        catch (TgProxyPortConflictException portEx)
        {
            // v2.36 (MVP one-button task B): typed port-conflict
            // exception thrown by TgProxyManager.Start before the
            // python spawn. Surface the cause + owner hint so the
            // user knows whether to close another app or change
            // the port in settings.
            _logger.Warning(portEx,
                "[VM] TgProxy start blocked: port {Port} busy (owner hint: {Owner})",
                portEx.Port, portEx.OwnerProcessHint ?? "<unknown>");
            TgProxyEnabled = false;
            TgProxyStatus = portEx.OwnerProcessHint is null
                ? string.Format(Strings.TgProxyPortBusy, portEx.Port)
                : string.Format(Strings.TgProxyPortBusyWithOwner, portEx.Port, portEx.OwnerProcessHint);
            ShowTgProxyToast(TgProxyStatus);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] TgProxy start failed");
            TgProxyStatus = $"Error: {ex.Message}";
            TgProxyEnabled = false;
        }
#endif
    }

    [RelayCommand]
    private void CopyTgProxyLink()
    {
        if (string.IsNullOrEmpty(TgProxyLink)) return;
        CopyToClipboard(TgProxyLink);
        // v2.31.6-r4 (BUG #3 fix): don't overwrite TgProxyStatus.
        // Pre-r4 we set it to "Copied!" which persistently shadowed
        // the real status (Stopped / Running / Error) until the next
        // status-mutating event. Computer-use audit on r2/r3 confirmed
        // the field never auto-reverted, so user saw stale "Copied!"
        // 30 minutes after click. The clipboard side-effect is its
        // own feedback channel; we trust users to know the click
        // landed without us hijacking the status banner.
        ShowTgProxyToast(Strings.TgProxyCopied);
    }

    [RelayCommand]
    private void OpenTgProxyInTelegram()
    {
        if (string.IsNullOrEmpty(TgProxySecret)) return;

        // v2.31.6-r4 (BUG #1 fix): if no app is registered for the
        // tg:// URI scheme (Windows shows "We can't open this 'tg' link"
        // dialog), Telegram desktop is missing. Surface the cause
        // directly instead of letting the OS dialog do the talking,
        // and offer the canonical download link.
        if (!TgProxyManager.IsTelegramSchemeRegistered())
        {
            ShowTgProxyToast(IsRussian
                ? "Telegram не установлен — скачай с desktop.telegram.org"
                : "Telegram not installed — download from desktop.telegram.org");
            return;
        }

        TgProxyManager.OpenInTelegram("127.0.0.1", TgProxyPort, TgProxySecret);
    }

    /// <summary>
    /// v2.31.6-r1 (TelegramPage UX simplification): one-click
    /// onboarding for Telegram proxy. Wraps the three things a
    /// first-time user needs into a single CTA:
    ///   1. Download the tg-ws-proxy binary if not already installed.
    ///   2. Start the proxy (which auto-generates a secret if empty).
    ///   3. Open Telegram with the deep-link so the client adds the
    ///      proxy to its Settings → Advanced → Connection type list.
    /// On subsequent visits <see cref="IsTgProxySetUp"/> flips to
    /// true and the page swaps to the simpler Connect/Disconnect
    /// surface — at which point this command is no longer reachable
    /// from the UI but stays callable defensively.
    /// </summary>
    [RelayCommand]
    private async Task SetupTgProxyAsync()
    {
#if PLATFORM_WINDOWS
        if (IsTgProxyDownloading) return;

        // Step 1+2: ToggleTgProxyAsync handles "download → generate
        // secret → start" already. Re-using it keeps the start path
        // single-sourced and avoids drift if the toggle logic
        // evolves later (port retry, secret rotation policy, etc.).
        if (!TgProxyEnabled && !TgProxyManager.IsAnyRunning(TgProxyPort))
        {
            await ToggleTgProxyAsync();
        }

        // Step 3: open Telegram with the deep-link. Skip if the
        // start above failed for some reason (no binary, port
        // collision, etc.) — Status text already explains why.
        // v2.31.6-r5: route through OpenTgProxyInTelegram (the command
        // body, not the relay wrapper) so the BUG #1 toast guard for
        // missing Telegram desktop fires here too. Pre-r5 this branch
        // called TgProxyManager.OpenInTelegram directly and bypassed
        // the registry probe — first-time Linux/macOS-style users
        // without Telegram desktop saw the OS dialog instead of the
        // download-link toast.
        if (TgProxyEnabled && !string.IsNullOrEmpty(TgProxySecret))
        {
            OpenTgProxyInTelegram();
        }
#else
        await Task.CompletedTask;
#endif
    }

    /// <summary>
    /// v2.31.6-r5 (TG-2): unified main-action command wired to the
    /// TelegramPage footer button. Branches on current state:
    /// <list type="bullet">
    ///   <item>Stopped → fires <see cref="SetupTgProxyAsync"/> (download
    ///     binary if needed, start the proxy, open Telegram with
    ///     deep-link to auto-add the entry — single click).</item>
    ///   <item>Running → fires <see cref="ToggleTgProxyAsync"/> which
    ///     stops the proxy.</item>
    /// </list>
    /// User feedback 2026-05-03 night surfaced that the pre-r5 layout
    /// had two visually distant buttons (body «Open in Telegram» +
    /// footer «Start Telegram proxy») that conceptually belonged
    /// together on first run. Folding the start+open chain into the
    /// footer, demoting the body button to a secondary «re-pair»
    /// fallback, removes the «click body, then click footer» two-step
    /// without competing visually with the global Start VPN footer
    /// (per v2.25.6 design intent — footer keeps its secondary style).
    /// </summary>
    [RelayCommand]
    private async Task TgProxyMainActionAsync()
    {
#if PLATFORM_WINDOWS
        if (IsTgProxyDownloading) return;

        if (TgProxyEnabled || TgProxyManager.IsAnyRunning(TgProxyPort))
        {
            await ToggleTgProxyAsync();
        }
        else
        {
            await SetupTgProxyAsync();
        }
#else
        await Task.CompletedTask;
#endif
    }

    /// <summary>
    /// v2.36 (MVP one-button task C): dismiss the non-blocking scheme-
    /// missing warning banner. Banner re-shows next start if the
    /// scheme is still unregistered (user re-installed Telegram, etc.).
    /// </summary>
    [RelayCommand]
    private void DismissTelegramSchemeWarning()
    {
        IsTelegramSchemeWarningVisible = false;
    }

    [RelayCommand]
    private void OpenTgProxyFolder()
    {
        OpenFolderInExplorer(TgProxyUpdater.TgProxyDir);
    }

    [RelayCommand]
    private void OpenTgProxyGitHub()
    {
        OpenUrl("https://github.com/Flowseal/tg-ws-proxy");
    }

    [RelayCommand]
    private void OpenZapretFolder()
    {
        OpenFolderInExplorer(ZapretUpdater.ZapretDir);
    }

    [RelayCommand]
    private void OpenZapretGitHub()
    {
        OpenUrl("https://github.com/Flowseal/zapret-discord-youtube");
    }

    private static void OpenFolderInExplorer(string path)
    {
        // v2.31.6-r11: Debug-log instead of swallowing silently. Iter#4
        // audit P2: user-action paths (Open folder / Open URL / Copy to
        // clipboard) shouldn't fail invisibly — add at least a Debug
        // line so postmortem from logs is possible. We don't escalate
        // to Warning because the failure modes are usually benign
        // (folder doesn't exist, no shell associated with the URL).
        try
        {
            if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Serilog.Log.Logger.Debug(ex, "[VM] OpenFolderInExplorer failed: {Path}", path);
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Serilog.Log.Logger.Debug(ex, "[VM] OpenUrl failed: {Url}", url);
        }
    }

    [RelayCommand]
    private void CopyTgProxySecret()
    {
        if (string.IsNullOrEmpty(TgProxySecret)) return;
        CopyToClipboard(TgProxySecret);
        // v2.31.6-r4 (BUG #3): toast not status — see CopyTgProxyLink.
        ShowTgProxyToast(Strings.TgProxyCopied);
    }

    [RelayCommand]
    private void RegenerateTgProxySecret()
    {
        var wasRunning = TgProxyEnabled || TgProxyManager.IsAnyRunning(TgProxyPort);

        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        TgProxySecret = Convert.ToHexString(bytes).ToLowerInvariant();
        TgProxyLink = TgProxyManager.BuildProxyLink("127.0.0.1", TgProxyPort, TgProxySecret);
        SaveSettings();

        // v2.31.6-r4 (BUG #4 fix): if the proxy was running when the
        // secret got rotated, the existing Telegram client connection
        // is now using a stale secret and will silently keep failing
        // until the user restarts the proxy AND re-pairs Telegram.
        // Make this consequence explicit instead of silent.
        if (wasRunning)
        {
            ShowTgProxyToast(IsRussian
                ? "Новый secret — перезапусти proxy и Telegram client"
                : "New secret — restart proxy and re-pair Telegram client");
        }
        else
        {
            ShowTgProxyToast(IsRussian ? "Новый secret сгенерирован" : "New secret generated");
        }
    }

    /// <summary>
    /// v2.31.6-r4: transient toast surface for TgProxy actions that
    /// pre-r4 hijacked TgProxyStatus (Copied! / Installed v… / similar).
    /// Sets <see cref="TgProxyToast"/>, schedules a 2500 ms revert,
    /// and bails the revert if a newer toast races in. Page binds
    /// the toast separately from the status banner so the runtime
    /// status (Stopped / Running / Error) is never shadowed by
    /// a transient confirmation.
    /// </summary>
    private void ShowTgProxyToast(string message)
    {
        TgProxyToast = message;
        var token = ++_tgProxyToastToken;
        _ = Task.Delay(2500).ContinueWith(_ =>
        {
            // Only clear if no newer toast has fired in the meantime.
            if (token == _tgProxyToastToken)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (token == _tgProxyToastToken) TgProxyToast = string.Empty;
                });
            }
        });
    }

    private int _tgProxyToastToken;

    private void CopyToClipboard(string text)
    {
        // v2.31.6-r12: Debug-log instead of swallowing silently. Iter#4
        // audit P2: clipboard failures (no clipboard service available
        // in headless test, app exited mid-copy, etc.) should leave a
        // forensic trace.
        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow?.Clipboard?.SetTextAsync(text);
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[VM] CopyToClipboard failed (text length: {Len})", text?.Length ?? 0);
        }
    }

    /// <summary>Parse stats line into short summary for UI display.
    /// v2.37.0-r16 \u2014 localized "Active:" and "Total:" prefixes (were
    /// hardcoded English pre-r16; mixed inside an otherwise-Russian
    /// air-pill, violating CLAUDE.md D1).</summary>
    private static string ParseStatsShort(string statsLine)
    {
        // Input: "stats: total=10 active=2 ws=8 tcp_fb=1 cf=0 bad=1 ..."
        var parts = new Dictionary<string, string>();
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(statsLine, @"(\w+)=(\S+)"))
        {
            parts[m.Groups[1].Value] = m.Groups[2].Value;
        }

        parts.TryGetValue("active", out var active);
        parts.TryGetValue("total", out var total);
        parts.TryGetValue("up", out var up);
        parts.TryGetValue("down", out var down);

        var sb = new System.Text.StringBuilder();
        if (active != null) sb.Append($"{Strings.TgProxyStatsActive}: {active}");
        if (total != null) sb.Append($" | {Strings.TgProxyStatsTotal}: {total}");
        if (up != null) sb.Append($" | \u2191{up}");
        if (down != null) sb.Append($" \u2193{down}");
        return sb.ToString();
    }


}
