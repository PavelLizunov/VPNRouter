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
    /// <summary>Kill ALL winws.exe processes system-wide.</summary>
    private void KillAllZapret()
    {
#if PLATFORM_WINDOWS
        // v2.31.6-r12: Debug-log instead of swallowing silently.
        try { _zapret?.Stop(); }
        catch (Exception ex) { _logger.Debug(ex, "[VM] KillAllZapret: _zapret.Stop failed"); }

        // Force kill by process name
        foreach (var proc in System.Diagnostics.Process.GetProcessesByName("winws"))
        {
            try { proc.Kill(entireProcessTree: true); proc.WaitForExit(3000); }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[VM] KillAllZapret: proc.Kill failed (PID {Pid})", proc.Id);
            }
            finally { proc.Dispose(); }
        }

        // Fallback: taskkill /F as last resort
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("taskkill", "/F /IM winws.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[VM] KillAllZapret: taskkill fallback failed");
        }
#endif
    }

    /// <summary>Check if winws.exe is running (from previous session or manual start).</summary>
    private bool IsZapretRunning()
    {
#if PLATFORM_WINDOWS
        // v2.40.0-r3 (audit P0 handle-leak sweep): handle-safe (was GetProcessesByName(...).Length).
        return VPNRouter.Core.Services.ProcessQuery.AnyAlive("winws");
#else
        return false;
#endif
    }

    /// <summary>Load strategies from Flowseal .bat files + legacy built-ins.</summary>
    private void LoadZapretStrategies()
    {
        var names = new List<string>();

#if PLATFORM_WINDOWS
        if (VPNRouter.Core.Services.ZapretUpdater.IsInstalled())
        {
            _parsedStrategies = VPNRouter.Core.Services.ZapretUpdater.ParseStrategies();
            names.AddRange(_parsedStrategies.Select(s => s.Name));
            ZapretVersionText = VPNRouter.Core.Services.ZapretUpdater.GetLocalVersion() ?? "?";
        }
        else
        {
            _parsedStrategies = new();
            ZapretVersionText = IsRussian ? "Не установлен" : "Not installed";
        }
#endif
        // r43: only add legacy stubs ("multisplit", "fake+multisplit") when
        // there are NO parsed .bat strategies (i.e. Zapret not installed or
        // freshly-empty install dir). These stubs have no .bat + no args, so
        // picking them when a real strategy is available leads to:
        //   "multisplit not in parsed list — using custom args path"
        //   "Zapret Args: " (empty)
        //   "Process exited (exit code: 1)"
        // — surfaced as a false-positive AV warning. Removing them when real
        // strategies exist forces the picker to a working option.
        //
        // r44 extension: also DON'T add stubs when Zapret IS installed but
        // _parsedStrategies is still empty (install corrupted, .bat files
        // missing/unreadable). Stubs would only mislead the user; instead
        // log a diagnostic so we can surface "reinstall Zapret" toast later.
#if PLATFORM_WINDOWS
        var zapretActuallyInstalled = VPNRouter.Core.Services.ZapretUpdater.IsInstalled();
#else
        var zapretActuallyInstalled = false;
#endif
        if (_parsedStrategies.Count == 0 && !zapretActuallyInstalled)
        {
            names.Add("multisplit");
            names.Add("fake+multisplit");
        }
        else if (_parsedStrategies.Count == 0 && zapretActuallyInstalled)
        {
            _logger?.Warning(
                "[VM] LoadZapretStrategies: Zapret install dir exists but ParseStrategies returned 0 — likely install corruption");
        }
        // "custom" stays — represents the user's own args path (ZapretCustomArgs).
        names.Add("custom");

        ZapretStrategies = new System.Collections.ObjectModel.ObservableCollection<string>(names);

        // v2.37.0-r36 — build display variant with verification badges from
        // ZapretProbeCache. Currently only the cached winner gets a badge;
        // future r37+ will extend the cache to per-strategy results so every
        // entry can carry verified/failed status.
        RefreshZapretStrategiesDisplay();

        // r39 follow-up — find the most-recent zapret-probe-*.log on disk
        // and surface it as LastProbeLogPath so the "Open probe log" button
        // shows up even on fresh app launch (not only after running a probe
        // in the current session). Best-effort.
        TryRestoreLastProbeLog();

        // Restore saved strategy index.
        // r43: if saved value points to a now-removed stub ("multisplit" /
        // "fake+multisplit") AND we have real parsed strategies — auto-migrate
        // to the first parsed entry (typically "general" or similar). Users
        // upgrading from a pre-r43 install where multisplit was a stub get
        // a working pick on first run instead of a dropdown that skips them
        // to "custom" (empty args).
        var saved = _settings.App.ZapretStrategy;
        var idx = names.IndexOf(saved);
        if (idx < 0
            && _parsedStrategies.Count > 0
            && (string.Equals(saved, "multisplit", StringComparison.OrdinalIgnoreCase)
                || string.Equals(saved, "fake+multisplit", StringComparison.OrdinalIgnoreCase)))
        {
            _logger?.Information(
                "[VM] Migrating saved ZapretStrategy '{Old}' (stub, no longer listed) → '{New}'",
                saved, _parsedStrategies[0].Name);
            idx = names.IndexOf(_parsedStrategies[0].Name);
        }
        ZapretStrategyIndex = idx >= 0 ? idx : 0;
    }

    /// <summary>
    /// r39 follow-up — scan %ProgramData%\VPNRouter\logs\ for the most-recent
    /// zapret-probe-*.log and surface its path so the "Open probe log" button
    /// shows up on fresh app launch. Best-effort: any IO error → leave
    /// LastProbeLogPath as null (button hidden).
    /// </summary>
    private void TryRestoreLastProbeLog()
    {
        try
        {
            var logsDir = VPNRouter.Core.AppPaths.LogsDir;
            if (!Directory.Exists(logsDir)) return;
            var newest = Directory.GetFiles(logsDir, "zapret-probe-*.log")
                .OrderByDescending(p => File.GetLastWriteTimeUtc(p))
                .FirstOrDefault();
            if (!string.IsNullOrEmpty(newest))
            {
                LastProbeLogPath = newest;
                _logger?.Debug("[VM] TryRestoreLastProbeLog: {Path}", newest);
            }
        }
        catch (Exception ex)
        {
            _logger?.Debug(ex, "[VM] TryRestoreLastProbeLog failed (non-fatal)");
        }
    }

    /// <summary>
    /// v2.37.0-r36 — rebuild <see cref="ZapretStrategiesDisplay"/> from the
    /// raw <see cref="ZapretStrategies"/> + the cached probe winner (if any).
    /// Call after a probe finishes or when a fresh cache load happens, so
    /// the Hero mini-row ComboBox shows the latest "✓ N/N" badge.
    /// </summary>
    private void RefreshZapretStrategiesDisplay()
    {
        var display = new System.Collections.ObjectModel.ObservableCollection<string>();
        VPNRouter.Core.Services.ZapretProbeCacheEntry? cached = null;
#if PLATFORM_WINDOWS
        try { cached = VPNRouter.Core.Services.ZapretProbeCache.TryLoad(_logger); }
        catch (Exception ex)
        {
            _logger?.Debug(ex, "[VM] RefreshZapretStrategiesDisplay: probe cache load failed");
        }
#endif

        var winnerName = cached?.Strategy;
        var winnerOk = cached?.IsRecentAndReliable() ?? false;
        var winnerStale = cached?.IsStale() ?? false;
        var hasScore = cached?.HasTargetScore() ?? false;
        var passed = cached?.TargetsPassed ?? 0;
        var total = cached?.TargetsTotal ?? 0;

        // r37: pull per-strategy probe results (each strategy tested in
        // the last sweep gets a ✓/⚠/✗ badge based on its pass rate).
        var perStrategy = cached?.PerStrategyResults
            ?? new System.Collections.Generic.Dictionary<string, VPNRouter.Core.Services.ZapretStrategyTestResult>(StringComparer.Ordinal);

        // r46 — build a list of ZapretStrategyDisplayItem so the ComboBox
        // ItemTemplate can color glyph and name independently. Glyph + score
        // get a status-coloured Foreground (green/yellow/red/gray/orange) via
        // style selectors; name stays default text colour.
        //
        // Glyph vocabulary (legend in DpiBypassPage):
        //   ✓ green   — strategy passed verification
        //   ⚠ yellow  — strategy partially passed (some targets failed)
        //   ✗ red     — strategy failed verification (zero targets passed)
        //   ◌ muted   — strategy never tested (no probe data)
        //   ⏱ orange  — winner data is stale (>7 days old)
        var newDisplay = new System.Collections.ObjectModel.ObservableCollection<ZapretStrategyDisplayItem>();
        foreach (var name in ZapretStrategies)
        {
            // Winner gets the most authoritative badge (✓/⏱ from main
            // cache fields). Non-winners fall back to per-strategy probe
            // data if we have it from the same sweep.
            if (!string.IsNullOrEmpty(winnerName)
                && string.Equals(name, winnerName, StringComparison.Ordinal))
            {
                if (winnerOk)
                {
                    newDisplay.Add(new ZapretStrategyDisplayItem
                    {
                        Glyph = hasScore ? $"✓ {passed}/{total}" : "✓",
                        NameText = name,
                        Kind = ZapretStrategyDisplayKind.Success,
                    });
                }
                else if (winnerStale)
                {
                    newDisplay.Add(new ZapretStrategyDisplayItem
                    {
                        Glyph = "⏱",
                        NameText = name,
                        Kind = ZapretStrategyDisplayKind.Stale,
                    });
                }
                else
                {
                    newDisplay.Add(new ZapretStrategyDisplayItem
                    {
                        Glyph = "◌",
                        NameText = name,
                        Kind = ZapretStrategyDisplayKind.Muted,
                    });
                }
                continue;
            }

            // r37: non-winner badging from per-strategy sweep results.
            if (perStrategy.TryGetValue(name, out var result) && result.Total > 0)
            {
                ZapretStrategyDisplayKind kind;
                string glyph;
                if (result.Passed == result.Total)
                {
                    kind = ZapretStrategyDisplayKind.Success;
                    glyph = $"✓ {result.Passed}/{result.Total}";
                }
                else if (result.Passed == 0)
                {
                    kind = ZapretStrategyDisplayKind.Danger;
                    glyph = $"✗ 0/{result.Total}";
                }
                else
                {
                    kind = ZapretStrategyDisplayKind.Warning;
                    glyph = $"⚠ {result.Passed}/{result.Total}";
                }
                newDisplay.Add(new ZapretStrategyDisplayItem
                {
                    Glyph = glyph,
                    NameText = name,
                    Kind = kind,
                });
            }
            else
            {
                // r45: "not tested" glyph (was bare name) — makes it obvious
                // that probe simply hasn't reached this strategy yet vs.
                // tested-and-passed.
                newDisplay.Add(new ZapretStrategyDisplayItem
                {
                    Glyph = "◌",
                    NameText = name,
                    Kind = ZapretStrategyDisplayKind.Muted,
                });
            }
        }
        ZapretStrategiesDisplay = newDisplay;
    }

    [RelayCommand]
    private async Task UpdateZapretAsync()
    {
#if PLATFORM_WINDOWS
        if (IsZapretDownloading) return;
        IsZapretDownloading = true;
        ZapretStatus = IsRussian ? "Загрузка zapret..." : "Downloading zapret...";

        try
        {
            // Stop zapret if running
            if (ZapretEnabled || IsZapretRunning())
            {
                KillAllZapret();
                ZapretEnabled = false;
            }

            var updater = new VPNRouter.Core.Services.ZapretUpdater(_logger);
            updater.StatusChanged += s =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() => ZapretStatus = s);

            await updater.DownloadAndExtractAsync(System.Threading.CancellationToken.None);

            LoadZapretStrategies();

            ZapretStatus = IsRussian
                ? $"zapret {ZapretVersionText} установлен"
                : $"zapret {ZapretVersionText} installed";
        }
        catch (VPNRouter.Core.Services.ZapretDownloadException zex)
        {
            // Categorized error — use the already-human-readable message directly
            // instead of wrapping with "Download error:" prefix (which adds noise).
            _logger.Warning("[VM] Zapret download failed: {Category} {Msg}", zex.Category, zex.Message);
            ZapretStatus = FormatZapretError(zex);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] Zapret download failed (uncategorized)");
            ZapretStatus = IsRussian
                ? $"Ошибка загрузки: {ex.Message}"
                : $"Download error: {ex.Message}";
        }
        finally
        {
            IsZapretDownloading = false;
        }
#endif
    }

#if PLATFORM_WINDOWS
    /// <summary>Translate categorized Zapret errors to localized, actionable user messages.</summary>
    private string FormatZapretError(VPNRouter.Core.Services.ZapretDownloadException zex)
    {
        return zex.Category switch
        {
            VPNRouter.Core.Services.ZapretErrorCategory.Concurrent => IsRussian
                ? "Загрузка уже идёт — дождитесь завершения."
                : "Download already in progress — wait for it to finish.",
            VPNRouter.Core.Services.ZapretErrorCategory.GitHubRateLimit => IsRussian
                ? "GitHub временно ограничил запросы. Попробуйте через ~15 минут."
                : "GitHub rate-limited us. Try again in ~15 minutes.",
            VPNRouter.Core.Services.ZapretErrorCategory.GitHubServerError => IsRussian
                ? "GitHub недоступен. Повторите попытку через минуту."
                : "GitHub is temporarily down. Try again in a minute.",
            VPNRouter.Core.Services.ZapretErrorCategory.Network => IsRussian
                ? $"Сбой сети: {zex.Message}"
                : zex.Message,
            VPNRouter.Core.Services.ZapretErrorCategory.Corrupted => IsRussian
                ? "Скачанный файл повреждён. Нажмите «Скачать» ещё раз."
                : "Downloaded file is corrupted. Click Download to retry.",
            VPNRouter.Core.Services.ZapretErrorCategory.Invalid => IsRussian
                ? $"Формат релиза изменился: {zex.Message}"
                : zex.Message,
            VPNRouter.Core.Services.ZapretErrorCategory.FileSystem => IsRussian
                ? $"Ошибка файловой системы: {zex.Message}"
                : zex.Message,
            _ => IsRussian
                ? $"Ошибка: {zex.Message}"
                : $"Error: {zex.Message}",
        };
    }
#endif

    [RelayCommand]
    private async Task ToggleZapretAsync()
    {
#if PLATFORM_WINDOWS
        // If any winws process running → stop ALL
        if (ZapretEnabled || IsZapretRunning())
        {
            KillAllZapret();
            ZapretEnabled = false;
            ZapretStatus = Strings.Stopped;
            SaveSettings();
            return;
        }

        // Auto-download if not installed
        if (!VPNRouter.Core.Services.ZapretUpdater.IsInstalled())
        {
            await UpdateZapretAsync();
            if (!VPNRouter.Core.Services.ZapretUpdater.IsInstalled()) return;
        }

        try
        {
            if (_zapret == null)
            {
                _zapret = new ZapretManager(_logger);
                // Bug-r9-G (2026-05-11): when winws.exe exits within < 2 s
                // with non-zero code, almost always AV killed it. Stas's
                // log: "[WRN] [Zapret] Wrapper exited (exit code: -1)"
                // right after launch with no other diagnostics. The
                // toast names the whitelist path explicitly so the user
                // can paste it into their AV's exception list.
                _zapret.ImmediateExitDetected += OnZapretImmediateExit;
            }
            var strategyName = ZapretStrategyIndex >= 0 && ZapretStrategyIndex < ZapretStrategies.Count
                ? ZapretStrategies[ZapretStrategyIndex] : "multisplit";

            if (strategyName == "custom")
            {
                _zapret.Start(ZapretCustomArgs);
            }
            else if (strategyName == "multisplit" || strategyName == "fake+multisplit")
            {
                _zapret.Start(ZapretManager.BuildLegacyArgs(strategyName));
            }
            else
            {
                var parsed = _parsedStrategies.FirstOrDefault(s => s.Name == strategyName);
                if (parsed == null)
                {
                    ZapretStatus = $"Strategy not found: {strategyName}";
                    return;
                }
                // Prefer the original .bat file — it runs Flowseal's prologue
                // (service.bat load_user_lists, etc.) which is required for winws.exe.
                // Silent wrapper: same prologue + winws.exe run directly (no `start`),
                // so it inherits hidden parent window instead of appearing in taskbar.
                if (!string.IsNullOrEmpty(parsed.BatPath) && File.Exists(parsed.BatPath))
                    _zapret.StartFromBat(parsed.BatPath, parsed.Arguments);
                else
                    _zapret.Start(parsed.Arguments);
            }

            // Verify winws actually started (bat wrapper exits fast; check winws by name)
            await Task.Delay(1500);
            var winwsPid = ZapretManager.WinwsPid;
            if (_zapret.IsRunning || winwsPid != null)
            {
                ZapretEnabled = true;
                var pid = winwsPid ?? _zapret.Pid;
                ZapretStatus = IsRussian
                    ? $"Работает [{strategyName}] (PID {pid})"
                    : $"Running [{strategyName}] (PID {pid})";
            }
            else
            {
                ZapretEnabled = false;
                ZapretStatus = IsRussian
                    ? "Ошибка: winws.exe завершился сразу. Проверьте стратегию."
                    : "Error: winws.exe exited immediately. Check strategy.";
            }
            SaveSettings();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] Zapret start failed");
            ZapretStatus = $"Error: {ex.Message}";
            ZapretEnabled = false;
        }
#endif
    }

    // ────────────────────────────────────────────────────────────────────────
    // v2.36.0-r8 — ZapretOneTap orchestrator + hero label computed props
    // ────────────────────────────────────────────────────────────────────────
    //
    // The hero card on DpiBypassPage binds to:
    //   - LblZapretHeroTitle / LblZapretHeroLede  — state-driven title + lede
    //   - LblZapretMagicButton                    — Start/Stop label
    //   - LblZapretAirPill                        — running-state pill
    //   - IsZapretMagicButtonEnabled              — disabled during probe/download
    //   - ZapretOneClickCommand                   — the magic button itself
    //
    // The orchestrator runs three phases:
    //   1. Optional download (UpdateZapretAsync if !IsInstalled)
    //   2. Discord hosts ensure-installed (default ON)
    //   3. Auto-probe loop via ZapretAutoStrategy.ProbeAsync — ALT3 → general → ALT
    //
    // On Tier1 win: winner stays running, ZapretWinningStrategy set, hero
    // re-narrates to "Активна стратегия: …". On all-fail: IsZapretFallback=true,
    // hero re-narrates to "Стратегия не подобрана", last-tried winws.exe is
    // STOPPED (in contrast to the research doc — research left it running, but
    // a not-working strategy running is noise; safer to leave clean).

    /// <summary>Hero title — flips between Stopped, Probing, Running, Fallback states.</summary>
    public string LblZapretHeroTitle
    {
        get
        {
            if (IsZapretProbing) return Strings.ZapretOneTapTitleProbing;
            if (ZapretEnabled && !string.IsNullOrEmpty(ZapretWinningStrategy))
                return Strings.ZapretOneTapTitleRunning(ZapretWinningStrategy);
            if (IsZapretFallback) return Strings.ZapretOneTapTitleFallback;
            return Strings.ZapretOneTapTitleStopped;
        }
    }

    /// <summary>Hero lede — flips with the four states. v2.37: probing lede
    /// embeds live per-target score "(2/3): general (ALT3) — 7/8 ok" so the
    /// user can see exactly what's passing.</summary>
    public string LblZapretHeroLede
    {
        get
        {
            if (IsZapretProbing && ZapretProbeTotal > 0)
            {
                var name = string.IsNullOrEmpty(ZapretProbeStrategy) ? "..." : ZapretProbeStrategy;
                // Once we have a probe count, show it — earlier in the attempt
                // (during Starting/Soaking phases) ZapretProbeTotalCount=0 and
                // we fall back to the no-score variant.
                if (ZapretProbeTotalCount > 0)
                    return Strings.ZapretOneTapLedeProbingScored(
                        ZapretProbeIndex + 1, ZapretProbeTotal, name,
                        ZapretProbePassCount, ZapretProbeTotalCount);
                return Strings.ZapretOneTapLedeProbing(
                    ZapretProbeIndex + 1, ZapretProbeTotal, name);
            }
            if (ZapretEnabled) return Strings.ZapretOneTapLedeRunning;
            if (IsZapretFallback) return Strings.ZapretOneTapLedeFallback;
            return Strings.ZapretOneTapLedeStopped;
        }
    }

    /// <summary>Magic-button label — Start when stopped, Stop when running.</summary>
    public string LblZapretMagicButton => ZapretEnabled
        ? Strings.ZapretOneTapStopButton
        : Strings.ZapretOneTapStartButton;

    /// <summary>Disable button during download + probing to prevent double-spawn.</summary>
    public bool IsZapretMagicButtonEnabled => !IsZapretDownloading && !IsZapretProbing;

    /// <summary>Air pill text when running. v2.37: shows probe score
    /// "general (ALT3) · 7/8" when we have the count, otherwise falls back
    /// to PID. Score conveys confidence ("7 of 8 targets confirmed") which
    /// is more user-meaningful than the PID number.</summary>
    public string LblZapretAirPill
    {
        get
        {
            var name = string.IsNullOrEmpty(ZapretWinningStrategy) ? "..." : ZapretWinningStrategy;
            if (ZapretProbeTotalCount > 0)
                return Strings.ZapretOneTapAirPillScored(name, ZapretProbePassCount, ZapretProbeTotalCount);
            var pid = ZapretManager.WinwsPid ?? 0;
            return Strings.ZapretOneTapAirPill(name, pid);
        }
    }

    /// <summary>L_ getter for the "Тонкая настройка" expander header.</summary>
    public string L_ZapretOneTapTune => Strings.ZapretOneTapTune;

    /// <summary>L_ getters for the 3-step chip labels in the hero card.</summary>
    public string L_ZapretOneTapStep1 => Strings.ZapretOneTapStep1;
    public string L_ZapretOneTapStep2 => Strings.ZapretOneTapStep2;
    public string L_ZapretOneTapStep3 => Strings.ZapretOneTapStep3;

    /// <summary>v2.37.0-r11 — L_ getters for the cache-control buttons
    /// inside the Tools expander.</summary>
    public string L_ZapretForceFreshProbeButton => Strings.ZapretForceFreshProbeButton;
    public string L_ZapretClearCacheButton => Strings.ZapretClearCacheButton;

    /// <summary>v2.37.0-r24 — L_ getters for the Hero strategy summary card.
    /// The card sits below the main "Включить обход блокировок" button and
    /// shows what's currently cached + 2 action buttons.</summary>
    public string L_ZapretReverifyButton => Strings.ZapretReverifyButton;
    public string L_ZapretReverifyHint => Strings.ZapretReverifyHint;
    public string L_ZapretSummaryDetailsButton => Strings.ZapretSummaryDetailsButton;
    public string L_ZapretSummaryStaleHint => Strings.ZapretSummaryStaleHint;
    public string L_ZapretCancelProbeButton => Strings.ZapretCancelProbeButton;

    /// <summary>v2.37.0-r25 — TabControl tab-header L_ getters for TgProxy
    /// (Telegram-прокси) page. 3 tabs: Settings, Version, Help. Replaces
    /// the prior "Тонкая настройка" Expander block.</summary>
    public string L_TgProxyTabSettings => Strings.TgProxyTabSettings;
    public string L_TgProxyTabVersion  => Strings.TgProxyTabVersion;
    public string L_TgProxyTabHelp     => Strings.TgProxyTabHelp;

    /// <summary>v2.37.0-r25 — drives the TgProxy page tab swap. r29 reads
    /// this to swap visible ScrollViewer in the manual tab strip + Panel
    /// implementation. 3 tabs: 0=Settings, 1=Version, 2=Help.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTgProxyTab0))]
    [NotifyPropertyChangedFor(nameof(IsTgProxyTab1))]
    [NotifyPropertyChangedFor(nameof(IsTgProxyTab2))]
    private int _tgProxyActiveTabIndex;

    public bool IsTgProxyTab0 => TgProxyActiveTabIndex == 0;
    public bool IsTgProxyTab1 => TgProxyActiveTabIndex == 1;
    public bool IsTgProxyTab2 => TgProxyActiveTabIndex == 2;

    [RelayCommand]
    private void SetTgProxyTab(string indexStr)
    {
        if (int.TryParse(indexStr, out var idx) && idx >= 0 && idx <= 2)
            TgProxyActiveTabIndex = idx;
    }

    /// <summary>
    /// One-button magic Zapret orchestrator. Runs on the magic button click
    /// in the new DpiBypassPage hero card. Replaces ToggleZapretAsync for the
    /// hero path; ToggleZapretAsync stays callable from the legacy footer and
    /// for autostart bootstrap.
    /// </summary>
    [RelayCommand]
    private async Task ZapretOneClickAsync()
    {
#if PLATFORM_WINDOWS
        // Already running? → toggle Stop and reset hero state.
        if (ZapretEnabled || IsZapretRunning())
        {
            KillAllZapret();
            ZapretEnabled = false;
            ZapretWinningStrategy = string.Empty;
            ZapretProbePassCount = 0;
            ZapretProbeTotalCount = 0;
            IsZapretFallback = false;
            ZapretStatus = Strings.Stopped;
            SaveSettings();
            return;
        }

        // Phase 1 — install if missing OR if upstream has a newer release.
        // r37: auto-update on every start. RemoteVersionChecker uses a 6-hour
        // TTL cache so we don't hammer GitHub on rapid restarts. If the check
        // fails (network down, rate-limit, etc.) we gracefully fall back to
        // "install only if missing" — never breaks the start flow.
        if (!VPNRouter.Core.Services.ZapretUpdater.IsInstalled())
        {
            ZapretStatus = Strings.ZapretOneTapDownloading;
            await UpdateZapretAsync();
            if (!VPNRouter.Core.Services.ZapretUpdater.IsInstalled()) return;
        }
        else
        {
            try
            {
                var remoteTag = await VPNRouter.Core.Services.RemoteVersionChecker.GetLatestTagAsync(
                    VPNRouter.Core.Services.ZapretUpdater.FlowsealRepoPublic,
                    userAgent: $"VPNRouter/{VPNRouter.Core.AppVersion.Version}",
                    _logger,
                    System.Threading.CancellationToken.None);
                var localTag = VPNRouter.Core.Services.ZapretUpdater.GetLocalVersion();
                if (VPNRouter.Core.Services.RemoteVersionChecker.IsNewer(remoteTag, localTag))
                {
                    _logger.Information(
                        "[VM] OneTap: Zapret update available {Local} → {Remote}, auto-applying",
                        localTag, remoteTag);
                    ZapretStatus = IsRussian
                        ? $"Обновление Zapret до {remoteTag}…"
                        : $"Updating Zapret to {remoteTag}…";
                    await UpdateZapretAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[VM] OneTap: Zapret remote version check failed (non-fatal)");
            }
        }

        // Phase 2a — Discord hosts ensure-installed (default ON for one-tap).
        // Skip if already installed to avoid UAC fatigue on returning users.
        // ToggleDiscordHosts is INSTALL-if-not-installed (we gated above),
        // and it's synchronous (writes hosts file + flushes DNS inline).
        if (!DiscordHostsInstalled)
        {
            try
            {
                ZapretStatus = Strings.ZapretOneTapInstallingHosts;
                ToggleDiscordHosts();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[VM] OneTap: Discord hosts install failed (non-fatal, continuing to probe)");
            }
        }

        // Phase 2b — r34: Flowseal hosts ensure-installed. User asked
        // «проставляються хосты?» — previously only Discord hosts were
        // auto-installed by magic. Flowseal hosts add YouTube + other
        // Cloudflare overrides needed for full DPI bypass coverage.
        if (!FlowsealHostsInstalled)
        {
            try
            {
                await ToggleFlowsealHostsAsync();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[VM] OneTap: Flowseal hosts install failed (non-fatal, continuing to probe)");
            }
        }

        // Phase 2c — r34: Set GameFilter=All on first-time magic if user
        // hasn't configured it. Without this, the strategy works for
        // browsers but UDP game traffic (1024-65535) bypasses DPI bypass
        // → games connect-fail. All is safe default; power users can
        // change in Тонкая настройка → Фильтры and that overrides
        // (IsGameFilterConfigured becomes true → magic stops touching it).
        if (!ZapretActions.IsGameFilterConfigured)
        {
            try
            {
                ZapretActions.SetGameFilterMode(ZapretActions.GameFilterMode.All);
                GameFilterModeIndex = (int)ZapretActions.GameFilterMode.All;
                _logger.Information("[VM] OneTap: Game filter set to All (first-time default — covers games on UDP)");
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[VM] OneTap: Game filter default-set failed (non-fatal)");
            }
        }

        // Phase 3 — auto-probe loop.
        await ProbeAndStartZapretAsync();
#endif
    }

    // ── v2.37.0-r10 — Zapret probe-cache UI controls ───────────────────────
    //
    // r6 added the cache silently — it works for happy-path users but
    // power users who want to re-probe after a network move or wipe the
    // cache for testing had no surface. r10 adds:
    //   - LblZapretCacheStatus: bilingual one-liner surfacing cache state
    //   - ClearZapretCacheCommand: wipes the JSON file (idempotent)
    //   - ForceFreshProbeCommand: sets _forceFreshProbe + runs probe
    //   - _forceFreshProbe transient flag honored by ProbeAndStartZapretAsync
    //
    // r19 (2026-05-25) — moved members OUTSIDE `#if PLATFORM_WINDOWS` because
    // DpiBypassPage.axaml is compiled once (no per-platform XAML) and Avalonia
    // resolves bindings via reflection on the type's full public surface.
    // Pre-r19 the Linux/Mac builds (build-linux.yml, build-mac.yml on push)
    // failed with `AVLN2000: Unable to resolve property or method of name
    // 'LblZapretCacheStatus'`. Inner bodies still guarded by OS check where
    // they touch Windows-only state (ZapretEnabled, IsZapretRunning, etc.).
    // ZapretProbeCache itself is cross-platform (just JSON file in CacheDir).

    private bool _forceFreshProbe;

    // v2.37.0-r21 — probe progress info richness fix. User feedback:
    // «мало информативно что происходит при проверке». Adds an elapsed
    // counter that ticks every second + ETA estimate from elapsed/index.
    // Wired into LblZapretHeroLede via NotifyPropertyChangedFor.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LblZapretHeroLede))]
    [NotifyPropertyChangedFor(nameof(LblZapretProbeElapsed))]
    private int _zapretProbeElapsedSeconds;

    private DateTime _zapretProbeStartTime;
    private System.Threading.Timer? _zapretProbeElapsedTimer;

    /// <summary>v2.37.0-r21 — live elapsed/ETA chip shown under the
    /// probe ProgressBar. Computes ETA only after at least 1 config has
    /// completed (so the per-config ETA estimate is calibrated).</summary>
    public string LblZapretProbeElapsed
    {
        get
        {
            if (!IsZapretProbing || ZapretProbeElapsedSeconds <= 0)
                return string.Empty;
            int? etaSec = null;
            if (ZapretProbeIndex > 0 && ZapretProbeTotal > 0)
            {
                // Time-per-completed-config × remaining configs.
                var perConfig = (double)ZapretProbeElapsedSeconds / Math.Max(1, ZapretProbeIndex);
                var remaining = Math.Max(0, ZapretProbeTotal - ZapretProbeIndex);
                etaSec = (int)(perConfig * remaining);
            }
            return Strings.ZapretProbeElapsedAndEta(ZapretProbeElapsedSeconds, etaSec);
        }
    }

    /// <summary>L_ getter for the new "Start with this strategy" button.</summary>
    public string L_ZapretStartSelectedStrategyButton => Strings.ZapretStartSelectedStrategyButton;
    public string L_ZapretStartSelectedStrategyHint => Strings.ZapretStartSelectedStrategyHint;

    /// <summary>v2.37.0-r21 — apply the strategy currently picked in the
    /// "Тонкая настройка" ComboBox directly, without running the auto-probe.
    /// For users who already know which strategy works on their ISP and
    /// don't want to wait 2-7 minutes for the Flowseal sweep every restart.
    /// </summary>
    [RelayCommand]
    private async Task StartZapretWithSelectedStrategyAsync()
    {
#if PLATFORM_WINDOWS
        var idx = ZapretStrategyIndex;
        if (idx < 0 || idx >= ZapretStrategies.Count)
        {
            _logger.Warning("[VM] StartZapretWithSelectedStrategy: invalid index {Idx}", idx);
            return;
        }
        var strategyName = ZapretStrategies[idx];
        if (string.IsNullOrEmpty(strategyName))
        {
            _logger.Warning("[VM] StartZapretWithSelectedStrategy: empty name at {Idx}", idx);
            return;
        }

        // Stop any running probe / zapret first.
        if (IsZapretProbing)
        {
            _logger.Information("[VM] StartZapretWithSelectedStrategy: a probe is already running — refusing");
            return;
        }
        // r44 — unconditional reap. Pre-r44 we only KillAllZapret when
        // `ZapretEnabled || IsZapretRunning()` was true. But if a recent probe
        // left orphan winws.exe processes (which CleanupOrphanWinws couldn't
        // reach because it was canceled / crashed mid-probe), they'd survive
        // and conflict with the new winws we're about to spawn (port collision,
        // duplicate filter rules). Always kill before spawn.
        KillAllZapret();
        if (ZapretEnabled || IsZapretRunning())
        {
            ZapretEnabled = false;
            ZapretWinningStrategy = string.Empty;
            await Task.Delay(500);
        }

        if (_zapret == null)
        {
            _zapret = new ZapretManager(_logger);
            _zapret.ImmediateExitDetected += OnZapretImmediateExit;
        }

        ZapretStatus = Strings.ZapretStartingSelected(strategyName);

        try
        {
            // Resolve to parsed strategy entry (BatPath + Arguments).
            var parsed = _parsedStrategies.FirstOrDefault(s => s.Name == strategyName);
            if (parsed == null)
            {
                // r44 — same class as r43 stub fix: if user picked "custom"
                // (or any non-parsed name) AND ZapretCustomArgs is empty,
                // we'd spawn winws with no args and it would exit 1 → false
                // AV warning. Guard explicitly and surface a clear status.
                if (string.IsNullOrWhiteSpace(ZapretCustomArgs))
                {
                    _logger.Warning(
                        "[VM] Selected strategy {Name} not in parsed list AND ZapretCustomArgs is empty — refusing to spawn winws with no args",
                        strategyName);
                    ZapretStatus = IsRussian
                        ? $"Стратегия «{strategyName}» не настроена (пустые аргументы). Выбери другую."
                        : $"Strategy '{strategyName}' is not configured (empty arguments). Pick another.";
                    return;
                }
                _logger.Warning("[VM] Selected strategy {Name} not in parsed list — using custom args path", strategyName);
                _zapret!.Start(ZapretCustomArgs);
            }
            else if (!string.IsNullOrEmpty(parsed.BatPath) && File.Exists(parsed.BatPath))
            {
                _zapret!.StartFromBat(parsed.BatPath, parsed.Arguments);
            }
            else
            {
                _zapret!.Start(parsed.Arguments);
            }

            await Task.Delay(1500);
            var winwsPid = ZapretManager.WinwsPid;
            if (_zapret.IsRunning || winwsPid != null)
            {
                ZapretEnabled = true;
                ZapretWinningStrategy = strategyName;
                ZapretProbePassCount = 0;
                ZapretProbeTotalCount = 0;
                IsZapretFallback = false;
                var pid = winwsPid ?? _zapret.Pid ?? 0;
                ZapretStatus = Strings.ZapretRunningSelected(strategyName, pid);
                // Persist as a cache success so warm-start kicks in next time.
                VPNRouter.Core.Services.ZapretProbeCache.RecordSuccess(strategyName, _logger);
                // r36: refresh display badges after manual start success.
                RefreshZapretStrategiesDisplay();
                SaveSettings();
            }
            else
            {
                ZapretEnabled = false;
                IsZapretFallback = true;
                ZapretStatus = Strings.ZapretSelectedStrategyFailed(strategyName);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] StartZapretWithSelectedStrategy threw");
            ZapretStatus = Strings.ZapretSelectedStrategyFailed(strategyName) + " (" + ex.Message + ")";
        }
        NotifyZapretSummaryChanged();
#else
        // Zapret is Windows-only. On Mac/Linux the button stays bound for
        // XAML compile but pressing it is a no-op.
        await Task.CompletedTask;
#endif
    }

#if PLATFORM_WINDOWS
    // M1 (v2.45.0): named TgProxy stats handler so it can be detached in Dispose().
    // TgProxy is Windows-only, so the handler is gated too (keeps the Linux
    // member-set — and its characterization hash — unchanged).
    private void OnTgProxyStats(string stats)
        => Dispatcher.UIThread.Post(() => TgProxyStats = ParseStatsShort(stats));
#endif

    private void StartZapretProbeElapsedTimer()
    {
        _zapretProbeStartTime = DateTime.UtcNow;
        ZapretProbeElapsedSeconds = 0;
        _zapretProbeElapsedTimer?.Dispose();
        _zapretProbeElapsedTimer = new System.Threading.Timer(_ =>
        {
            if (_disposed) return; // M3 (v2.45.0): a tick after Dispose() is a no-op
            try
            {
                var elapsed = (int)(DateTime.UtcNow - _zapretProbeStartTime).TotalSeconds;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    ZapretProbeElapsedSeconds = elapsed;
                });
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[VM] Probe elapsed tick failed");
            }
        }, null, dueTime: TimeSpan.FromSeconds(1), period: TimeSpan.FromSeconds(1));
    }

    private void StopZapretProbeElapsedTimer()
    {
        _zapretProbeElapsedTimer?.Dispose();
        _zapretProbeElapsedTimer = null;
        ZapretProbeElapsedSeconds = 0;
    }

    /// <summary>
    /// One-liner surfacing the current Zapret probe cache state. Used in
    /// the Tools expander as a hint near the Force-fresh / Clear-cache
    /// buttons so the user knows what's persisted. Cross-platform — Zapret
    /// cache file lives in the shared CacheDir on every OS, even though
    /// the probe itself only runs on Windows today.
    /// </summary>
    public string LblZapretCacheStatus
    {
        get
        {
            var entry = VPNRouter.Core.Services.ZapretProbeCache.TryLoad(_logger);
            if (entry == null || string.IsNullOrEmpty(entry.Strategy))
                return Strings.ZapretCacheEmpty;
            return Strings.ZapretCacheInfo(entry.Strategy, entry.SuccessRunCount);
        }
    }

    // ───── r24 — Hero strategy summary card ──────────────────────────────
    //
    // Shown directly under the main "Включить обход блокировок" button so
    // the user knows what's cached without opening Тонкую настройку:
    //
    //   ✓ Стратегия «general» работает
    //   4 из 5 целей · проверено 12 мин назад
    //   [Перепроверить эту] [Подробнее]
    //
    // States (driven by ZapretProbeCacheEntry):
    //   - fresh + reliable   → "✓ работает" green
    //   - stale (>7 days)    → "⚠ устарела" warning
    //   - missing / empty    → "◌ не проверена" muted (card hidden,
    //                          replaced by hint to run probe)
    //
    // All 4 properties are derived from a single TryLoad call cached in
    // _zapretSummaryEntryCached so the file isn't re-read for each XAML
    // binding. Call OnPropertyChanged(nameof(IsZapretSummaryVisible))
    // (and friends) whenever the cache changes — see UpdateZapretSummary.

    /// <summary>True when there's a cache entry to render. Card hidden when false.</summary>
    public bool IsZapretSummaryVisible
    {
        get
        {
            var e = VPNRouter.Core.Services.ZapretProbeCache.TryLoad(_logger);
            return e != null && !string.IsNullOrEmpty(e.Strategy);
        }
    }

    /// <summary>
    /// True when cache exists but is older than 7 days. Used to switch
    /// the card icon ✓ → ⚠ and tint subtext warning-colored.
    /// </summary>
    public bool IsZapretCacheStale
    {
        get
        {
            var e = VPNRouter.Core.Services.ZapretProbeCache.TryLoad(_logger);
            return e != null && e.IsStale();
        }
    }

    /// <summary>
    /// Localized header line, e.g. "Стратегия «general» работает".
    /// Empty string when there's no cache (card hidden anyway).
    /// </summary>
    public string LblZapretSummaryHeader
    {
        get
        {
            var e = VPNRouter.Core.Services.ZapretProbeCache.TryLoad(_logger);
            if (e == null || string.IsNullOrEmpty(e.Strategy)) return string.Empty;
            return e.IsStale()
                ? Strings.ZapretSummaryHeaderStale(e.Strategy)
                : Strings.ZapretSummaryHeaderFresh(e.Strategy);
        }
    }

    /// <summary>
    /// Localized subtext line, e.g. "4 из 5 целей · проверено 12 мин назад".
    /// Score part is omitted for v1 legacy cache entries (TargetsTotal=0);
    /// the relative-time part is always present.
    /// </summary>
    public string LblZapretSummarySubtext
    {
        get
        {
            var e = VPNRouter.Core.Services.ZapretProbeCache.TryLoad(_logger);
            if (e == null) return string.Empty;
            var rel = FormatRelativeTime(e.LastSweepAt);
            return e.HasTargetScore()
                ? Strings.ZapretSummarySubtextWithScore(e.TargetsPassed, e.TargetsTotal, rel)
                : Strings.ZapretSummarySubtextNoScore(rel);
        }
    }

    /// <summary>
    /// Convert a UTC timestamp to a short relative-time string in the
    /// user's current language. Granularity: minutes for &lt;1h, hours
    /// for &lt;24h, days for &lt;30d, weeks for &lt;52w, else "месяцы назад".
    /// </summary>
    private string FormatRelativeTime(DateTime utcWhen)
    {
        var delta = DateTime.UtcNow - utcWhen;
        if (delta < TimeSpan.FromMinutes(1)) return Strings.RelativeTimeJustNow;
        if (delta < TimeSpan.FromHours(1))
        {
            var m = Math.Max(1, (int)delta.TotalMinutes);
            return Strings.RelativeTimeMinutes(m);
        }
        if (delta < TimeSpan.FromDays(1))
        {
            var h = Math.Max(1, (int)delta.TotalHours);
            return Strings.RelativeTimeHours(h);
        }
        if (delta < TimeSpan.FromDays(30))
        {
            var d = Math.Max(1, (int)delta.TotalDays);
            return Strings.RelativeTimeDays(d);
        }
        // Beyond 30 days we don't bother with weeks — by that point the
        // user is already past the stale threshold (7d) and the card is
        // showing the "⚠ устарела" badge anyway.
        return Strings.RelativeTimeLongAgo;
    }

    /// <summary>
    /// Fire OnPropertyChanged for every Hero-card property in one place
    /// so cache mutations propagate to UI atomically. Called whenever
    /// the cache file is written or cleared.
    /// </summary>
    private void NotifyZapretSummaryChanged()
    {
        OnPropertyChanged(nameof(IsZapretSummaryVisible));
        OnPropertyChanged(nameof(IsZapretCacheStale));
        OnPropertyChanged(nameof(LblZapretSummaryHeader));
        OnPropertyChanged(nameof(LblZapretSummarySubtext));
        OnPropertyChanged(nameof(LblZapretCacheStatus));
    }

    /// <summary>
    /// r25 — replaces the r24 IsZapretTuneExpanded boolean. r29 (manual
    /// tab strip + Panel + per-tab ScrollViewer) reads this to swap
    /// which ScrollViewer is visible. Tabs are zero-indexed:
    ///   0 — Strategy   (default — winner ComboBox + direct-start + IPSet)
    ///   1 — Hosts      (Discord + Flowseal hostfile installers)
    ///   2 — Filters    (Game filter + IPSet filter)
    ///   3 — Tools      (Diagnostics + cache + service + folder/GitHub)
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsZapretTab0))]
    [NotifyPropertyChangedFor(nameof(IsZapretTab1))]
    [NotifyPropertyChangedFor(nameof(IsZapretTab2))]
    [NotifyPropertyChangedFor(nameof(IsZapretTab3))]
    private int _zapretActiveTabIndex;

    // r33: Zapret probe cancellation. Created in ProbeAndStartZapretAsync,
    // cancelled by CancelZapretProbeCommand (Cancel button on Hero card
    // visible during IsZapretProbing). Also used by early-winner detection.
    private CancellationTokenSource? _zapretProbeCts;

    /// <summary>r33 — Cancel button on Hero card during probe. User can
    /// stop the 2-7 min sweep at any time. Cancellation triggers
    /// proc.Kill in ZapretAutoStrategy + restores ipset if needed.</summary>
    [RelayCommand]
    private void CancelZapretProbe()
    {
        try
        {
            _zapretProbeCts?.Cancel();
            _logger.Information("[VM] Zapret probe cancellation requested by user");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[VM] CancelZapretProbe failed");
        }
    }

    /// <summary>r29 — per-tab IsChecked/IsVisible getters for the manual
    /// tab strip (RadioButton group) + tab content Panel (ScrollViewers
    /// gated by IsVisible). Drives both the active-tab highlight and
    /// which scrollable content panel renders.</summary>
    public bool IsZapretTab0 => ZapretActiveTabIndex == 0;
    public bool IsZapretTab1 => ZapretActiveTabIndex == 1;
    public bool IsZapretTab2 => ZapretActiveTabIndex == 2;
    public bool IsZapretTab3 => ZapretActiveTabIndex == 3;

    /// <summary>r29 — bound to each tab strip button via Command +
    /// CommandParameter="0..3". Sets the active index; the
    /// NotifyPropertyChangedFor on _zapretActiveTabIndex causes all
    /// 4 IsZapretTabN getters to refresh, swapping visible content.</summary>
    [RelayCommand]
    private void SetZapretTab(string indexStr)
    {
        if (int.TryParse(indexStr, out var idx) && idx >= 0 && idx <= 3)
            ZapretActiveTabIndex = idx;
    }

    /// <summary>
    /// r25 — "Подробнее" button on the Hero summary card navigates to the
    /// Tools tab (index 3), where cache controls + diagnostics + service
    /// management live. The Strategy tab is the default landing because
    /// it's what most users will tweak; Tools is the deep-cuts surface
    /// the Hero card explicitly invites the user into.
    /// </summary>
    [RelayCommand]
    private void ExpandZapretTuneSection()
    {
        ZapretActiveTabIndex = 3; // Tools
    }

    [RelayCommand]
    private void ClearZapretCache()
    {
        try
        {
            VPNRouter.Core.Services.ZapretProbeCache.Clear(_logger);
            NotifyZapretSummaryChanged();
            ZapretStatus = Strings.ZapretCacheCleared;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[VM] ClearZapretCache failed");
        }
    }

    [RelayCommand]
    private async Task ForceFreshProbeAsync()
    {
#if PLATFORM_WINDOWS
        // Stop any running zapret first so the probe starts from clean state.
        if (ZapretEnabled || IsZapretRunning())
        {
            KillAllZapret();
            ZapretEnabled = false;
            ZapretWinningStrategy = string.Empty;
            ZapretProbePassCount = 0;
            ZapretProbeTotalCount = 0;
            await Task.Delay(500);
        }
        _forceFreshProbe = true;
        try
        {
            await ProbeAndStartZapretAsync();
        }
        finally
        {
            _forceFreshProbe = false;
            NotifyZapretSummaryChanged();
        }
#else
        // Non-Windows: Zapret probe path doesn't exist — return cleanly so
        // the binding stays callable but no-ops. The button stays visible
        // because XAML can't conditionally include it, but pressing it
        // does nothing meaningful on Mac/Linux (Zapret is Windows-only).
        await Task.CompletedTask;
#endif
    }
#if PLATFORM_WINDOWS

    /// <summary>
    /// Run ZapretAutoStrategy probe loop. Stays in PROBING state while
    /// iterating; on Tier1 success leaves the winner running and sets
    /// ZapretWinningStrategy + ZapretEnabled. On all-fail sets
    /// IsZapretFallback=true and stops cleanly.
    /// </summary>
    private async Task ProbeAndStartZapretAsync()
    {
        if (_zapret == null)
        {
            _zapret = new ZapretManager(_logger);
            _zapret.ImmediateExitDetected += OnZapretImmediateExit;
        }

        IsZapretProbing = true;
        IsZapretFallback = false;
        ZapretWinningStrategy = string.Empty;
        // Suppress Bug-r9-G AV toast during probing — the loop is supposed to
        // try multiple strategies; fast-exits are EXPECTED, not user-facing
        // alarms. Re-enable on probe completion.
        _suppressZapretAvToast = true;
        // r21 — start the live elapsed-time ticker so the hero shows
        // "Прошло 0:25 · осталось ~3:40" under the progress bar.
        StartZapretProbeElapsedTimer();

        try
        {
            // v2.37.0-r3 (user feedback "у тебя прошел очень быстро, через
            // bat файл занимает минуты времени"): delegate the actual probe
            // to Flowseal's `utils/test zapret.ps1` mode 2 (DPI checker) —
            // the canonical, slow, accurate path. It does TCP-byte-level
            // analysis detecting the "16-20 freeze" pattern that's a real
            // DPI signature, not just "is HTTP HEAD reachable" like r1/r2.
            //
            // The script self-iterates ALL 20 configs (mirrors
            // service.bat 11 -> 2 -> 1), runs DPI checks per config,
            // prints "Best config: <name>" at the end. We:
            //   1. Spawn powershell hidden (CreateNoWindow + WindowStyle.Hidden)
            //   2. Pipe stdin "2\n1\n" to auto-answer prompts
            //   3. Stream stdout, parse "[N/M] strategy" → hero progress chip
            //   4. Parse final "Best config: X" → winner
            //   5. Apply that strategy ourselves via ZapretManager.StartFromBat
            //
            // Wall-time: 2-7 minutes typical for a full sweep — that's the
            // cost of accuracy. User can cancel by clicking Stop in footer
            // (cancellation kills the powershell process tree).
            //
            // The script auto-switches ipset to 'any' for accurate DPI tests
            // and restores it on completion via its own trap. Our cancellation
            // path may leave ipset switched — script's trap handles SIGINT but
            // not Process.Kill. Acceptable trade-off; user can manually flip
            // ipset back via expander if they cancel mid-sweep.
            var zapretDir = VPNRouter.Core.Services.ZapretUpdater.ZapretDir;

            // r4 Part B (startup-side check): if a prior probe was killed
            // mid-sweep, the script's `ipset_switched.flag` would still be on
            // disk and `ipset-all.txt` would be in "any" mode. Clean up
            // proactively before starting a fresh probe so the new run
            // begins from a known-good ipset state — and so the user isn't
            // silently wide-open if the probe-trigger happens minutes after
            // an interrupted sweep.
            try
            {
                if (VPNRouter.Core.Services.ZapretAutoStrategy.HasOrphanedIpsetFlag(zapretDir))
                {
                    VPNRouter.Core.Services.ZapretAutoStrategy.RestoreIpsetAfterKill(zapretDir, _logger);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[VM] Pre-probe ipset cleanup failed (continuing anyway)");
            }

            // r6 — warm-start from cache. If the last successful sweep was
            // recent (<7d) and the strategy has at least 1 confirmed success
            // with <3 consecutive failures, skip the 2-7 min Flowseal sweep
            // and apply the cached winner directly. On failure of cache hit,
            // fall through to the full sweep automatically.
            //
            // r10 — _forceFreshProbe (set by ForceFreshProbeCommand) bypasses
            // the cache entirely. Used by the Tools-expander "Re-probe
            // strategy" button when the user wants a fresh sweep regardless
            // of cache state (e.g. after a network/ISP change).
            var cached = _forceFreshProbe
                ? null
                : VPNRouter.Core.Services.ZapretProbeCache.TryLoad(_logger);
            if (cached != null && cached.IsRecentAndReliable())
            {
                _logger.Information(
                    "[VM] ZapretOneTap cache hit: trying {Strategy} (success count {N})",
                    cached.Strategy, cached.SuccessRunCount);

                ZapretProbeStrategy = cached.Strategy;
                ZapretProbeIndex = 0;
                ZapretProbeTotal = 1;
                var hit = await TryApplyCachedWinnerAsync(cached.Strategy);
                if (hit)
                {
                    // r24 — preserve the existing score so the Hero card
                    // keeps rendering "X из Y целей" across warm-start hits.
                    // The score came from the most recent FULL sweep that
                    // chose this strategy; warm-starts don't re-probe
                    // targets so there's no new score to write.
                    VPNRouter.Core.Services.ZapretProbeCache.RecordSuccess(
                        cached.Strategy, cached.TargetsPassed, cached.TargetsTotal, _logger);
                    return;
                }
                else
                {
                    // Cache hit didn't pan out — record failure and proceed
                    // to full sweep. After 3 consecutive failures the cache
                    // entry stops being "reliable" automatically.
                    VPNRouter.Core.Services.ZapretProbeCache.RecordFailure(cached.Strategy, _logger);
                    _logger.Information("[VM] Cache miss path — running full sweep");
                }
            }
            else if (cached != null)
            {
                _logger.Information(
                    "[VM] Cache entry stale or unreliable (last sweep {LastSweep}, fails {Fails}) — running full sweep",
                    cached.LastSweepAt, cached.LastFailureCount);
            }

            var flowsealProgress = new Progress<VPNRouter.Core.Services.ZapretAutoStrategy.FlowsealProgress>(p =>
            {
                // r4 Part A — distinguish "new config header" vs "score-only update".
                // New header carries a non-empty StrategyName + resets counts to 0;
                // score-only update carries empty StrategyName + non-zero TotalChecks.
                if (!string.IsNullOrEmpty(p.StrategyName))
                {
                    ZapretProbeIndex = p.CurrentIndex - 1;  // FlowsealProgress is 1-based
                    ZapretProbeTotal = p.TotalCount;
                    ZapretProbeStrategy = p.StrategyName;
                    ZapretProbePassCount = 0;
                    ZapretProbeTotalCount = 0;
                    _logger.Information("[VM] ZapretOneTap Flowseal probe: {Index}/{Total} {Name}",
                        p.CurrentIndex, p.TotalCount, p.StrategyName);
                }
                else if (p.TotalChecks > 0)
                {
                    // Score-only update — keep strategy + index, refresh score.
                    // Triggers ZapretOneTapLede recompute so the UI lede shows
                    // «Тестирую (5/20): general (ALT3) — 12/18 ok» live.
                    //
                    // r5 — log every 6th score update so post-sweep log review
                    // can confirm the per-test parser is firing without
                    // spamming the log (Flowseal emits ~99 status lines per
                    // config × 20 configs = ~2000 events/sweep). Throttled
                    // by simple modulo on TotalChecks since it's monotonic.
                    ZapretProbePassCount = p.OkCount;
                    ZapretProbeTotalCount = p.TotalChecks;
                    if (p.TotalChecks % 6 == 0)
                    {
                        _logger.Information(
                            "[VM] ZapretOneTap Flowseal score: {Ok}/{Total} on {Strategy}",
                            p.OkCount, p.TotalChecks, ZapretProbeStrategy);
                    }
                }
            });

            // r33: cancellable probe via _zapretProbeCts. CancelZapretProbeCommand
            // (Cancel button on Hero) triggers cts.Cancel(). Also used by
            // early-winner detection inside ZapretAutoStrategy.
            _zapretProbeCts?.Dispose();
            _zapretProbeCts = new CancellationTokenSource();
            ZapretAutoStrategy.FlowsealSweepResult sweep;
            try
            {
                sweep = await VPNRouter.Core.Services.ZapretAutoStrategy.RunFlowsealProbeAsync(
                    zapretDir, flowsealProgress, _logger, _zapretProbeCts.Token);
            }
            finally
            {
                _zapretProbeCts.Dispose();
                _zapretProbeCts = null;
            }

            // r39 — surface the probe log path so the UI can offer
            // "Open probe log" click-through. Also log explicit
            // early-winner status so users understand why sweep stopped.
            LastProbeLogPath = sweep.ProbeLogPath;
            if (sweep.EarlyWinner)
            {
                _logger.Information(
                    "[VM] Probe early-exit: winner {Name} at config {N}/{T} — skipped remaining {M}",
                    sweep.Winner, sweep.TestedCount, sweep.TotalCount,
                    sweep.TotalCount - sweep.TestedCount);
            }

            if (sweep.Winner != null)
            {
                // Apply the winning strategy.
                // r53: tolerant match. The winner string comes from Flowseal
                // stdout ("Best config: general (ALT9).bat" → "general (ALT9)")
                // and could differ from the parsed-catalogue name by trailing
                // whitespace, casing, or a stray ".bat" suffix. Exact `==`
                // matching produced false "Winner X not found in strategy
                // list" reports (Z:\zapret 2026-05-28). Normalise both sides.
                static string NormStrategy(string? s) =>
                    (s ?? string.Empty).Trim().TrimEnd().Replace(".bat", "",
                        StringComparison.OrdinalIgnoreCase).Trim();
                var winnerNorm = NormStrategy(sweep.Winner);
                var parsed = _parsedStrategies.FirstOrDefault(s =>
                        string.Equals(NormStrategy(s.Name), winnerNorm, StringComparison.OrdinalIgnoreCase));
                if (parsed != null)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(parsed.BatPath) && File.Exists(parsed.BatPath))
                            _zapret!.StartFromBat(parsed.BatPath, parsed.Arguments);
                        else
                            _zapret!.Start(parsed.Arguments);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "[VM] Failed to start winning strategy {Name}", sweep.Winner);
                        IsZapretFallback = true;
                        ZapretEnabled = false;
                        ZapretStatus = $"Error starting {sweep.Winner}: {ex.Message}";
                        return;
                    }

                    // Wait briefly for winws.exe to appear, then verify alive.
                    await Task.Delay(1500);
                    var winwsPid = ZapretManager.WinwsPid;
                    if (_zapret.IsRunning || winwsPid != null)
                    {
                        ZapretWinningStrategy = sweep.Winner;
                        ZapretEnabled = true;
                        var pid = winwsPid ?? _zapret.Pid;
                        ZapretStatus = IsRussian
                            ? $"Работает [{sweep.Winner}] (PID {pid})"
                            : $"Running [{sweep.Winner}] (PID {pid})";

                        var idx = ZapretStrategies.IndexOf(sweep.Winner);
                        if (idx >= 0) ZapretStrategyIndex = idx;
                        // r6 — persist this winner so the next probe warm-starts.
                        // r24 — also persist the target-pass score (captured
                        // by the FlowsealProgress callback during the sweep).
                        // The last score we saw belongs to the winner because
                        // Flowseal returns on first qualifier and breaks.
                        // If the score data is missing (parser failure /
                        // sweep aborted just before pass-count update), the
                        // overload defaults to 0/0 and the Hero card
                        // gracefully omits the "X из Y" line.
                        // r37: record ALL per-strategy results from the sweep
                        // (not just the winner), so the Hero ComboBox can badge
                        // every probed strategy with ✓/⚠/✗ from this run.
                        var perStrategy = sweep.PerStrategyResults != null
                            ? new System.Collections.Generic.Dictionary<string, VPNRouter.Core.Services.ZapretStrategyTestResult>(
                                sweep.PerStrategyResults, StringComparer.Ordinal)
                            : new System.Collections.Generic.Dictionary<string, VPNRouter.Core.Services.ZapretStrategyTestResult>(StringComparer.Ordinal);
                        VPNRouter.Core.Services.ZapretProbeCache.RecordSweepResults(
                            sweep.Winner,
                            ZapretProbePassCount,
                            ZapretProbeTotalCount,
                            perStrategy,
                            _logger);
                        // r36: refresh Hero ComboBox display so the ✓ N/M badge
                        // shows up next to the just-verified winner immediately.
                        // r37: also picks up per-strategy badges from the same cache load.
                        RefreshZapretStrategiesDisplay();
                        NotifyZapretSummaryChanged();
                        SaveSettings();
                    }
                    else
                    {
                        IsZapretFallback = true;
                        ZapretEnabled = false;
                        ZapretStatus = IsRussian
                            ? $"Стратегия {sweep.Winner} не запустилась"
                            : $"Strategy {sweep.Winner} failed to start";
                    }
                }
                else
                {
                    _logger.Warning("[VM] Flowseal winner {Name} not in parsed list", sweep.Winner);
                    IsZapretFallback = true;
                    ZapretEnabled = false;
                    ZapretStatus = $"Winner {sweep.Winner} not found in strategy list";
                }
            }
            else
            {
                IsZapretFallback = true;
                ZapretEnabled = false;
                // r4 C.3 + C.4 — diagnostic-aware fallback messaging. If
                // the sweep short-circuited for a known reason (not_admin,
                // sweep_timeout, missing_script, canceled), surface that
                // specific cause instead of the generic "no strategy
                // matched" so the user knows what to fix. Otherwise fall
                // back to the generic toast.
                ZapretStatus = sweep.Diagnostic switch
                {
                    "not_admin" => IsRussian
                        ? "Нужны права администратора для подбора стратегии. Перезапустите VPNRouter от админа."
                        : "Administrator rights required to probe strategies. Restart VPNRouter as admin.",
                    "sweep_timeout" => IsRussian
                        ? "Подбор стратегии превысил 10 минут. Проверьте интернет и попробуйте ещё раз."
                        : "Strategy probe exceeded 10 min cap. Check network and retry.",
                    "missing_script" => IsRussian
                        ? "Скрипт Flowseal не найден. Обнови Zapret через «Тонкую настройку»."
                        : "Flowseal script missing. Update Zapret via Advanced settings.",
                    "canceled" => IsRussian
                        ? "Подбор отменён."
                        : "Probe canceled.",
                    _ => Strings.ZapretOneTapAllFailedToast,
                };

                // Log surface for any [ERROR]/[WARN] lines the script
                // emitted — keeps the diagnostic searchable in Serilog
                // without spamming the toast.
                if (sweep.ErrorLines is { Count: > 0 })
                {
                    foreach (var errLine in sweep.ErrorLines)
                        _logger.Warning("[VM] Flowseal script: {Line}", errLine);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] ZapretOneTap probe orchestrator failed");
            ZapretEnabled = false;
            IsZapretFallback = true;
            ZapretStatus = $"Error: {ex.Message}";
        }
        finally
        {
            // r4 Part B (post-sweep ipset cleanup): regardless of how the
            // sweep ended (winner / cancel / timeout / exception), check
            // for and restore an orphan ipset switch. Idempotent — no-op
            // if no flag exists. Catches the "killed mid-sweep" case while
            // the user's session is still open instead of letting the
            // wide-open ipset linger until the next probe.
            try
            {
                var zd = VPNRouter.Core.Services.ZapretUpdater.ZapretDir;
                VPNRouter.Core.Services.ZapretAutoStrategy.RestoreIpsetAfterKill(zd, _logger);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[VM] Post-probe ipset cleanup failed");
            }

            IsZapretProbing = false;
            _suppressZapretAvToast = false;
            ZapretProbeIndex = 0;
            ZapretProbeTotal = 0;
            ZapretProbeStrategy = string.Empty;
            // r21 — stop the live elapsed-time ticker.
            StopZapretProbeElapsedTimer();
            // Don't clear ZapretProbePass/TotalCount here — they're the
            // persisted score for the winning strategy and must survive
            // the orchestrator's cleanup so the air-pill keeps showing
            // "7/8" while the proxy is running. Cleared on Stop instead.
        }
    }

    /// <summary>
    /// r6 — warm-start path. Apply cached winning strategy directly and
    /// verify via short multi-target HEAD probe (8 endpoints, 5 s timeout
    /// each ≈ 5-7 s wall-time vs 2-7 min full Flowseal sweep). Returns
    /// true on confirmed success (winws.exe alive AND >=70% targets pass),
    /// false on any failure (caller falls through to full sweep).
    /// </summary>
    private async Task<bool> TryApplyCachedWinnerAsync(string strategy)
    {
        try
        {
            var parsed = _parsedStrategies.FirstOrDefault(s => s.Name == strategy);
            if (parsed == null)
            {
                _logger.Warning("[VM] Cached strategy {Name} not in parsed list — bypass cache", strategy);
                return false;
            }

            ZapretProbeStrategy = strategy;
            // 1. Start the strategy.
            if (!string.IsNullOrEmpty(parsed.BatPath) && File.Exists(parsed.BatPath))
                _zapret!.StartFromBat(parsed.BatPath, parsed.Arguments);
            else
                _zapret!.Start(parsed.Arguments);

            // 2. Wait briefly for winws.exe; Bug-r9-G fast-exit would
            //    show up here as a missing PID after ~150 ms.
            await Task.Delay(1500);
            var winwsPid = ZapretManager.WinwsPid;
            if (!_zapret.IsRunning && winwsPid == null)
            {
                _logger.Warning("[VM] Cached strategy {Name} failed to spawn winws.exe", strategy);
                return false;
            }

            // 3. Multi-target HEAD probe — fast sanity, not the full
            //    Flowseal DPI checker. If a strategy was good 6 days ago
            //    and isn't immediately broken, this is enough confidence.
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(7) };
            var targets = VPNRouter.Core.Services.ZapretAutoStrategy.LoadTargets(_logger);
            var report = await VPNRouter.Core.Services.ZapretAutoStrategy.ProbeAllTargetsAsync(
                targets, http, _logger, CancellationToken.None);
            var passPercent = targets.Count == 0 ? 0 : (report.PassCount * 100) / targets.Count;
            _logger.Information(
                "[VM] Cache warm-start probe: {Pass}/{Total} ok ({Pct}%) on {Strategy}",
                report.PassCount, targets.Count, passPercent, strategy);

            if (passPercent >= VPNRouter.Core.Services.ZapretAutoStrategy.Tier2MinPassPercent)
            {
                // Treat Tier1+Tier2 as "good enough" — same threshold the
                // original ZapretAutoStrategy probe uses.
                ZapretWinningStrategy = strategy;
                ZapretEnabled = true;
                ZapretProbePassCount = report.PassCount;
                ZapretProbeTotalCount = targets.Count;
                var pid = winwsPid ?? _zapret.Pid;
                ZapretStatus = IsRussian
                    ? $"Работает [{strategy}] (PID {pid}, warm)"
                    : $"Running [{strategy}] (PID {pid}, warm)";
                var idx = ZapretStrategies.IndexOf(strategy);
                if (idx >= 0) ZapretStrategyIndex = idx;
                SaveSettings();
                return true;
            }

            // Probe under threshold — strategy stopped working since last
            // sweep. Stop the misfire so the full sweep starts clean.
            _logger.Warning("[VM] Cache warm-start probe under threshold — stopping for fresh sweep");
            try { _zapret?.Stop(); } catch { /* defensive */ }
            return false;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[VM] TryApplyCachedWinnerAsync threw");
            try { _zapret?.Stop(); } catch { /* defensive */ }
            return false;
        }
    }

#endif

    // ── Zapret tools (diagnostics, Discord cache, hosts, service menu) ──

    [ObservableProperty] private bool _isZapretActionRunning;
    [ObservableProperty] private string _zapretActionTitle = string.Empty;
    public ObservableCollection<string> ZapretActionOutput { get; } = new();

    [RelayCommand]
    private async Task RunZapretDiagnosticsAsync()
    {
#if PLATFORM_WINDOWS
        await RunZapretActionAsync(Strings.RunDiagnostics,
            ct => ZapretActions.RunDiagnosticsAsync(ct));
#endif
    }

    [RelayCommand]
    private async Task ClearDiscordCacheAsync()
    {
#if PLATFORM_WINDOWS
        await RunZapretActionAsync(Strings.ClearDiscordCache,
            ct => ZapretActions.ClearDiscordCacheAsync(ct));
#endif
    }

    [RelayCommand]
    private async Task UpdateZapretHostsAsync()
    {
#if PLATFORM_WINDOWS
        await RunZapretActionAsync(Strings.UpdateHostsFile,
            ct => ZapretActions.UpdateHostsAsync(ct));
#endif
    }

    [RelayCommand]
    private void OpenZapretServiceMenu()
    {
#if PLATFORM_WINDOWS
        try { ZapretActions.OpenServiceMenu(); }
        catch (Exception ex) { _logger.Error(ex, "[VM] OpenServiceMenu failed"); }
#endif
    }

    private async Task RunZapretActionAsync(string title,
        Func<CancellationToken, IAsyncEnumerable<string>> action)
    {
        if (IsZapretActionRunning) return;
        IsZapretActionRunning = true;
        ZapretActionTitle = title;
        ZapretActionOutput.Clear();
        try
        {
            // Stream enumeration on background thread — sub-processes (sc, netsh)
            // should not block UI thread.
            await Task.Run(async () =>
            {
                await foreach (var line in action(CancellationToken.None))
                {
                    var captured = line;
                    await Dispatcher.UIThread.InvokeAsync(() => ZapretActionOutput.Add(captured));
                }
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] Zapret action failed");
            await Dispatcher.UIThread.InvokeAsync(() => ZapretActionOutput.Add($"ERROR: {ex.Message}"));
        }
        finally { IsZapretActionRunning = false; }
    }

    [RelayCommand]
    private async Task ToggleFlowsealHostsAsync()
    {
#if PLATFORM_WINDOWS
        try
        {
            if (FlowsealHostsInstalled)
            {
                var (ok, msg) = VPNRouter.Core.Services.HostsManager.UninstallFlowseal(_logger);
                FlowsealHostsInstalled = VPNRouter.Core.Services.HostsManager.IsFlowsealInstalled();
                ZapretStatus = ok ? (IsRussian ? "Flowseal hosts удалены" : "Flowseal hosts removed") : msg;
            }
            else
            {
                var (ok, msg) = await VPNRouter.Core.Services.HostsManager.InstallFlowsealAsync(_logger);
                FlowsealHostsInstalled = VPNRouter.Core.Services.HostsManager.IsFlowsealInstalled();
                ZapretStatus = ok ? msg : msg;
            }
        }
        catch (Exception ex) { ZapretStatus = $"Error: {ex.Message}"; }
#endif
    }

    [RelayCommand]
    private async Task UpdateIpSetListAsync()
    {
#if PLATFORM_WINDOWS
        await RunZapretActionAsync(IsRussian ? "Обновить IPSet" : "Update IPSet list",
            ct => ZapretActions.UpdateIpSetListAsync(ct));
        // Refresh IpSetModeIndex after update (list content may have changed)
        IpSetModeIndex = (int)ZapretActions.GetIpSetMode();
#endif
    }

    [RelayCommand]
    private void RunZapretTests()
    {
#if PLATFORM_WINDOWS
        try { ZapretActions.RunTests(); }
        catch (Exception ex) { _logger.Error(ex, "[VM] RunTests"); }
#endif
    }

    [RelayCommand]
    private async Task RemoveZapretServiceAsync()
    {
#if PLATFORM_WINDOWS
        await RunZapretActionAsync(IsRussian ? "Удалить службу zapret" : "Remove zapret service",
            ct => ZapretActions.RemoveZapretServiceAsync(ct));
#endif
    }

#if PLATFORM_WINDOWS
    partial void OnGameFilterModeIndexChanged(int value)
    {
        if (_isLoadingUI) return;
        ZapretActions.SetGameFilterMode((ZapretActions.GameFilterMode)value);
    }

    partial void OnIpSetModeIndexChanged(int value)
    {
        if (_isLoadingUI) return;
        ZapretActions.SetIpSetMode((ZapretActions.IpSetMode)value);
    }

    partial void OnZapretAutoUpdateCheckChanged(bool value)
    {
        if (_isLoadingUI) return;
        ZapretActions.SetAutoUpdateCheck(value);
    }
#endif

    [RelayCommand]
    private void ToggleDiscordHosts()
    {
#if PLATFORM_WINDOWS
        try
        {
            if (DiscordHostsInstalled)
            {
                var (ok, msg) = VPNRouter.Core.Services.HostsManager.Uninstall(_logger);
                DiscordHostsInstalled = !ok || VPNRouter.Core.Services.HostsManager.IsInstalled();
                ZapretStatus = ok ? (IsRussian ? "Discord hosts удалены" : "Discord hosts removed")
                                  : msg;
            }
            else
            {
                var (ok, msg) = VPNRouter.Core.Services.HostsManager.Install(_logger);
                DiscordHostsInstalled = VPNRouter.Core.Services.HostsManager.IsInstalled();
                ZapretStatus = ok ? (IsRussian ? "Discord hosts добавлены (200 серверов)" : "Discord hosts added (200 servers)")
                                  : msg;
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] Discord hosts toggle failed");
            ZapretStatus = $"Hosts error: {ex.Message}";
        }
#endif
    }

}
