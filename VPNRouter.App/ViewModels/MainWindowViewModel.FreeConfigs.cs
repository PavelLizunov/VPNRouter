#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VPNRouter.App.Localization;
using VPNRouter.Core;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.FreeConfigs;

namespace VPNRouter.App.ViewModels;

/// <summary>
/// Phase 2B (Wave 8, 2026-05-18) — Free Configs apply path split out of
/// the <c>MainWindowViewModel</c> god-class. Hosts the two methods that
/// drive the "Use this free config" command from the Free Configs page:
///
/// <list type="bullet">
///   <item><see cref="ApplyFreeConfigAsync"/> — adopt a
///   <see cref="FreeConfigEntry"/> as the active VLESS server and
///   (re)start the VPN. Mode-flip lives here (forces
///   <c>IsSubscribeMode=false</c> / <c>IsVlessMode=true</c>) so the
///   subsequent <see cref="MainWindowViewModel.SaveSettings"/> writes
///   <c>ConfigMode='generated'</c> regardless of which tab the user was
///   on when they hit Apply (v2.28.3-r4 fix).</item>
///   <item><see cref="ShowFreeConfigSecurityWarningAsync"/> — one-time
///   privacy warning shown before the FIRST Free-config Connect. After
///   the user clicks Proceed, <c>FreeConfigSecurityWarningAcked</c> is
///   persisted so the dialog never reappears.</item>
/// </list>
///
/// <para>The actual Free Configs page UI (cache, recheck, master-detail
/// list) lives in <c>VPNRouter.App.ViewModels.FreeConfigs.FreeConfigsPageViewModel</c>
/// — this partial only carries the apply-into-MVM glue.</para>
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>
    /// Apply a free config (from the Free Configs page) as the active VLESS server and (re)start the VPN.
    /// IMPORTANT: mutates the VM-level <see cref="Servers"/> collection (not _settings directly) because
    /// SaveSettings() rebuilds _settings.Vless.Servers from the VM collection — direct mutations to
    /// _settings.Vless.Servers would be wiped out.
    /// </summary>
    private async Task<bool> ApplyFreeConfigAsync(FreeConfigEntry entry)
    {
        try
        {
            // v2.40.0 (contracts B4 #4) — defensive backstop: never adopt a
            // public config that hasn't passed deep verify, even if a caller
            // bypassed the FreeConfigsPageViewModel.ApplySelected gate. This is
            // the Core-adjacent guard layer (UI → VM → here); connecting to an
            // unverified/dead endpoint would route the user's traffic to it.
            if (entry.Status != FreeConfigStatus.Verified)
            {
                _logger.Warning("[VM] ApplyFreeConfig rejected: entry not Verified (status={Status}, {Host}:{Port})",
                    entry.Status, entry.Host, entry.Port);
                return false;
            }

            // v2.13.19 — one-time privacy warning before first-ever Free Config Connect.
            // User can dismiss once via the dialog's confirm button; reset via Settings.
            if (!_settings.App.FreeConfigSecurityWarningAcked)
            {
                var proceed = await ShowFreeConfigSecurityWarningAsync();
                if (!proceed) return false;
                _settings.App.FreeConfigSecurityWarningAcked = true;
                SaveSettings();
            }

            var newEntry = entry.ToVlessServerEntry();

            // Does the Free config already exist in the user's Server list? Match by host:port:uuid.
            var existingVm = Servers.FirstOrDefault(s =>
                string.Equals(s.Server, newEntry.Server, StringComparison.OrdinalIgnoreCase) &&
                s.Port == newEntry.Port &&
                string.Equals(s.Uuid, newEntry.Uuid, StringComparison.OrdinalIgnoreCase));

            ServerViewModel target;
            if (existingVm != null)
            {
                target = existingVm;
            }
            else
            {
                // Ensure display name is unique in the VM collection.
                var displayName = newEntry.Name;
                var baseName = string.IsNullOrWhiteSpace(displayName) ? "⚡ free" : displayName;
                displayName = baseName;
                var suffix = 2;
                while (Servers.Any(s => string.Equals(s.Name, displayName, StringComparison.OrdinalIgnoreCase)))
                    displayName = $"{baseName} #{suffix++}";
                newEntry.Name = displayName;

                target = new ServerViewModel(newEntry);
                Servers.Add(target);
            }

            // Make it the active server for the Manual/VLESS mode.
            // SaveSettings() reads SelectedServer + the Servers OC and persists them correctly.
            //
            // v2.28.3-r4 critical fix: also clear IsSubscribeMode. SaveSettings
            // line 1544 picks ConfigMode by checking IsSubscribeMode FIRST, then
            // IsVlessMode. If the user was on the Subscribe tab when they hit
            // Apply on a free config, IsSubscribeMode stayed true and SaveSettings
            // persisted ConfigMode='subscribe' — which made the engine pick
            // subscription servers on Start instead of the freshly-applied free
            // config. User report: "я подключаюсь а на самом деле к моей подписке
            // подключает а не к выбранному конфигу". Explicitly flipping both
            // mode flags so SaveSettings writes ConfigMode='generated' regardless
            // of which tab the user came from.
            //
            // v2.30.7-r2 (VM-3 audit fix): the explicit `_settings.App.ConfigMode
            // = "generated"` line below was dead code — SaveSettings recomputes
            // ConfigMode from VM flags (IsSubscribeMode/IsVlessMode) and overwrites
            // any direct assignment. Only the flag flips below matter. Dropped
            // the redundant assignment.
            SelectedServer = target;
            IsSubscribeMode = false;
            IsVlessMode = true;
            SelectedServerModeIndex = 0;

            SaveSettings();
            _settings = _settingsStore.Load(AppPaths.ConfigYamlPath);

            // Stop current VPN if running.
            if (IsConnected)
            {
                try { await Task.Run(() => _engine.Stop()); } catch { }
                IsConnected = false;
            }

            // Start with the new active server.
            // v2.35.2 Stage 2 (PinkuDani 2026-05-21) — two-phase start timer.
            // Same Phase A (60s) + Phase B (20s) budgets as
            // ToggleConnectionAsync. Phase A diagnostic returns false (caller
            // is the Free Configs page Apply button; UI just turns off the
            // spinner). Phase B reuses StatusText for the user-visible
            // failure note.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(
                Internals.TwoPhaseStartCoordinator.DefaultPhaseABudget.TotalSeconds +
                Internals.TwoPhaseStartCoordinator.DefaultPhaseBBudget.TotalSeconds));
            var startTask = Task.Run(
                // Carry the session "ignore conflict" decision (reconnect fix
                // 2026-06-15) so a Free Configs connect after the user ignored a
                // tolerated VPN doesn't re-throw ConflictingVpnException.
                () => _engine.StartAsync(_settings, cts.Token, _skipVpnConflictThisSession),
                cts.Token);

            var outcome = await Internals.TwoPhaseStartCoordinator.RunAsync(
                startTask: startTask,
                subscribeStarted: handler =>
                {
                    void Wrapper(int pid) => handler(pid);
                    _engine.SingBoxStarted += Wrapper;
                    return () => _engine.SingBoxStarted -= Wrapper;
                },
                subscribeConnected: handler =>
                {
                    void Wrapper(int pid) => handler(pid);
                    _engine.Connected += Wrapper;
                    return () => _engine.Connected -= Wrapper;
                },
                cancellationToken: cts.Token);

            if (outcome == Internals.TwoPhaseStartOutcome.PhaseATimeout)
            {
                _logger.Warning("[VM] ApplyFreeConfig: Phase A (sing-box launch) timed out after {N}s",
                    (int)Internals.TwoPhaseStartCoordinator.DefaultPhaseABudget.TotalSeconds);
                try { await Task.Run(() => _engine.Stop()); } catch { }
                StatusText = Strings.StartTimeoutPhaseA;
                return false;
            }
            if (outcome == Internals.TwoPhaseStartOutcome.PhaseBTimeout)
            {
                _logger.Warning("[VM] ApplyFreeConfig: Phase B (TUN warm-up) timed out after {N}s",
                    (int)Internals.TwoPhaseStartCoordinator.DefaultPhaseBBudget.TotalSeconds);
                try { await Task.Run(() => _engine.Stop()); } catch { }
                StatusText = Strings.StartTimeoutPhaseB;
                return false;
            }
            // Surface any exception from startTask (Connected / StartTaskCompleted / Cancelled).
            await startTask;
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "ApplyFreeConfig failed");
            return false;
        }
    }

    /// <summary>
    /// v2.13.19 — one-time privacy warning shown before first Connect from Free Configs.
    /// Modal dialog: explains operator can see metadata (not HTTPS content), lists what
    /// to avoid (banking/email/2FA) and what's safe (YouTube/Wikipedia/Discord).
    /// Returns true if user clicked "Proceed", false if user cancelled.
    /// </summary>
    private async Task<bool> ShowFreeConfigSecurityWarningAsync()
    {
        var owner = GetMainWindow();
        if (owner == null) return true; // edge case: no window — proceed silently

        var tcs = new TaskCompletionSource<bool>();

        // v2.31.6-r8: replaced 6 hardcoded hex colours with semantic design
        // tokens from Tokens.axaml. Pre-r8 the dialog used Avalonia.Media.Brush.
        // Parse("#059669"), "#FEF3C7", "#F59E0B", "#78350F", "#DCFCE7",
        // "#14532D", "#B45309" — Rule B3 violation (no raw hex in code) AND
        // the dialog rendered identically in Light/Dark themes because the
        // hex literals don't follow theme switching. Tokens
        // (SuccessBg/Fg/Solid/Border, WarningBg/Fg/Border) auto-resolve per
        // theme. TryFindResource returns null on test/design-time AppBuilder
        // setups; falling back to a sensible default (Brushes.Transparent
        // for backgrounds, default foreground) keeps the dialog renderable
        // in headless tests.
        IBrush Tok(string key, IBrush fallback)
            => owner.TryFindResource(key, owner.ActualThemeVariant, out var v)
                && v is IBrush b
                ? b
                : fallback;

        var proceedBtn = new Button
        {
            Content = Strings.FcSecWarnProceed,
            Padding = new Thickness(12, 6),
            FontWeight = FontWeight.SemiBold,
            Background = Tok("SuccessSolidBrush", Avalonia.Media.Brushes.SeaGreen),
            Foreground = Tok("AccentOnSolidBrush", Avalonia.Media.Brushes.White),
            CornerRadius = new CornerRadius(4),
        };
        var cancelBtn = new Button
        {
            Content = Strings.FcSecWarnCancel,
            Padding = new Thickness(12, 6),
            CornerRadius = new CornerRadius(4),
        };

        var dialog = new Window
        {
            Title = Strings.FcSecWarnTitle,
            Width = 520,
            Height = 440,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
            Content = new Border
            {
                Padding = new Thickness(20),
                Child = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "⚠ " + Strings.FcSecWarnHeader,
                            FontSize = 15,
                            FontWeight = FontWeight.Bold,
                            Foreground = Tok("WarningFgBrush", Avalonia.Media.Brushes.DarkOrange),
                            TextWrapping = TextWrapping.Wrap,
                        },
                        new TextBlock
                        {
                            Text = Strings.FcSecWarnBody,
                            FontSize = 11,
                            TextWrapping = TextWrapping.Wrap,
                        },
                        new Border
                        {
                            Padding = new Thickness(10, 8),
                            Background = Tok("WarningBgBrush", Avalonia.Media.Brushes.LightYellow),
                            BorderBrush = Tok("WarningBorderBrush", Avalonia.Media.Brushes.Goldenrod),
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(4),
                            Child = new TextBlock
                            {
                                Text = Strings.FcSecWarnDontUseList,
                                FontSize = 11,
                                TextWrapping = TextWrapping.Wrap,
                                Foreground = Tok("WarningFgBrush", Avalonia.Media.Brushes.SaddleBrown),
                            },
                        },
                        new Border
                        {
                            Padding = new Thickness(10, 8),
                            Background = Tok("SuccessBgBrush", Avalonia.Media.Brushes.Honeydew),
                            BorderBrush = Tok("SuccessBorderBrush", Avalonia.Media.Brushes.SeaGreen),
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(4),
                            Child = new TextBlock
                            {
                                Text = Strings.FcSecWarnGoodFor,
                                FontSize = 11,
                                TextWrapping = TextWrapping.Wrap,
                                Foreground = Tok("SuccessFgBrush", Avalonia.Media.Brushes.DarkGreen),
                            },
                        },
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Margin = new Thickness(0, 10, 0, 0),
                            Children = { cancelBtn, proceedBtn },
                        },
                    },
                },
            },
        };

        proceedBtn.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        cancelBtn.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);

        await dialog.ShowDialog(owner);
        return await tcs.Task;
    }
}
