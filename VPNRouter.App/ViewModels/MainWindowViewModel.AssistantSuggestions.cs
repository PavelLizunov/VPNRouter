using VPNRouter.App.Services;
using VPNRouter.Core.Services;
using Strings = VPNRouter.Core.Localization.Strings;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel
{
    // The free-form planner can only return a hint for a separate, explicit request.
    private async Task<string> SuggestAssistantCommandAsync(string goal, string subscription, AssistantSnapshot snapshot,
        AssistantObservation state, CancellationToken ct)
    {
        var controls = new List<AssistantControl>();
        void Add(string name, bool? active = null) =>
            controls.Add(new(controls.Count + 1, name, active.HasValue ? "toggle" : "press", active));

        string? process = AssistantApplicationTarget.MentionsRouting(goal)
            ? AssistantApplicationTarget.ResolveRequested(goal, proposal: null) : null;
        bool scoped = AssistantApplicationTarget.IsAppScopedRoute(goal, process != null);
        var settings = snapshot.Settings.App;
        if (process != null && settings.ConfigMode is ("generated" or "subscribe") && settings.CustomRules.All(rule => !rule.Enabled))
        {
            string suffix = AssistantApplicationTarget.IsDefaultBrowserRequest(goal) ? "ForDefaultBrowser" : "ForResolvedTarget";
            Add("RouteOnlyApplication" + suffix);
            Add("RouteApplicationDirect" + suffix);
        }
        if (!scoped)
        {
            Add("SetVpnConnected", state.Connected);
            if (settings.ConfigMode is "generated" or "subscribe") Add("SetStrictDns", settings.StrictDns);
            Add("SetFlushDnsOnStart", settings.FlushDnsOnStart);
            foreach (string section in AssistantSections)
                if (section != "OpenTools" || IsToolsAvailable) Add(section);
        }
        if (controls.Count == 0) return Strings.AssistantSingleCommand;

        var step = await StepAssistantAsync(subscription, goal, IsRussian, state with { Controls = controls }, ct);
        ct.ThrowIfCancellationRequested();
        if (step.Status != AssistantStepStatus.Act)
            return step.Status is AssistantStepStatus.Finish or AssistantStepStatus.Clarify
                ? Strings.AssistantSingleCommand : AssistantStepMessage(step.Status);
        if (step.Control is not { } target || !controls.Contains(target) || step.Text != null
            || (target.Kind == "toggle") != step.Toggle.HasValue)
            return Strings.AssistantSingleCommand;

        string? command = Strings.AssistantCommandExample(target.Name, step.Toggle, process);
        return command != null && AssistantIntent.Parse(command) != null
            ? Strings.AssistantSuggestedCommand(command) : Strings.AssistantSingleCommand;
    }
}
