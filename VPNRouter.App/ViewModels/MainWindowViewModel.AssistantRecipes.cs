using Avalonia.Controls;
using VPNRouter.App.Services;
using VPNRouter.Core.Services;
using Strings = VPNRouter.Core.Localization.Strings;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel
{
    internal bool IsAssistantGameDetectionSupported { get; init; } = AssistantRouteRecipe.SupportsGameDetection;
    internal Func<IReadOnlyList<string>> ReadAssistantGameProcesses { get; init; }
        = AssistantRouteRecipe.ReadDetectedGameProcesses;
    internal Func<bool> IsAssistantRuRuleSetAvailable { get; init; } = GeoDataDownloader.AreGeoFilesAvailable;

    private async Task<string> RunAssistantRecipeAsync(AssistantRouteRecipe recipe, AssistantSnapshot snapshot,
        Window window, Control source, Func<string, CancellationToken, Task<bool>> confirm, CancellationToken token)
    {
        var settings = snapshot.Settings.App;
        if (settings.ConfigMode is not ("generated" or "subscribe")) return Strings.AssistantRecipeConfigMode;
        if (settings.CustomRules.Any(rule => rule.Enabled)) return Strings.AssistantRuleConflict;

        bool connected = snapshot.Session != null;
        IReadOnlyList<string> detected = [];
        if (recipe.Kind == AssistantRouteRecipeKind.GamesDirect)
        {
            if (!IsAssistantGameDetectionSupported) return Strings.AssistantGamesPlatformUnsupported;
            try
            {
                detected = await Task.Run(ReadAssistantGameProcesses, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warning("Assistant game detection failed ({ExceptionType})", ex.GetType().Name);
                return Strings.AssistantGamesNotFound;
            }
        }

        bool regional = recipe.Kind == AssistantRouteRecipeKind.RussianSitesDirect;
        bool listsReady = regional && await Task.Run(IsAssistantRuRuleSetAvailable, token);
        token.ThrowIfCancellationRequested();
        if (!AssistantSnapshotStillCurrent(snapshot) || !_engine.IsAssistantSessionCurrent(snapshot.Session))
            return Strings.AssistantSettingsChanged;
        var planned = regional
            ? AssistantRouteRecipe.PlanRussianSites(snapshot.Settings, listsReady, connected)
            : AssistantRouteRecipe.PlanGames(snapshot.Settings, detected, SimpleSplitProfile, connected);
        if (planned.Message is { } message) return message;
        var plan = planned.Plan!;

        bool accepted = await confirm(plan.Preview, token);
        token.ThrowIfCancellationRequested();
        if (!accepted) return Strings.AssistantNoChanges;
        bool IsCurrent() => CanRunAssistantChange() && AssistantSnapshotStillCurrent(snapshot)
            && _engine.IsAssistantSessionCurrent(snapshot.Session)
            && ReferenceEquals(TopLevel.GetTopLevel(source), window)
            && ReferenceEquals(source.DataContext, this);
        if (!IsCurrent()) return Strings.AssistantSettingsChanged;
        if (connected && regional && !listsReady)
        {
            bool ready = await new GeoDataDownloader(_logger).EnsureGeoFilesAsync(token);
            token.ThrowIfCancellationRequested();
            if (!ready) return Strings.AssistantRuRuleSetsMissing;
            if (!IsCurrent()) return Strings.AssistantSettingsChanged;
        }

        var result = await ApplyAssistantSettingsAsync(plan.Candidate, snapshot, target: null,
            restoreWithoutStart: !connected, ct: token, verifyRoute: tunnel => regional
                ? tunnel.RoutesRussianTrafficDirect()
                : tunnel.RoutesGamesDirect(plan.VerifiedProcesses));
        if (result.Error != null) return result.Error;
        return AssistantRouteRecipe.CompletedMessage(recipe.Kind, connected, result.SettingsChangedAfterCommit);
    }
}
