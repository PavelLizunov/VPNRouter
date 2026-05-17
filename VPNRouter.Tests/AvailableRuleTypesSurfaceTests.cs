using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
public class AvailableRuleTypesSurfaceTests
{
    /// <summary>
    /// v2.31.0-r4 (AU-10): the Cards-mode Add-rule ComboBox lists rule
    /// types. Pre-fix it was missing <c>domain_regex</c> and
    /// <c>process_path</c> even though the Edit-mode validator accepted
    /// both — surface mismatch. The fix lives in
    /// <c>MainWindowViewModel.AvailableRuleTypes</c> (initialiser); this
    /// test pins the contents so a future tidy-up doesn't accidentally
    /// drop them again.
    ///
    /// <para>Construction is heavyweight (touches settings + logger), but
    /// we only read a static initialiser, so wrap it in [AvaloniaFact] —
    /// MainWindowViewModel's ApplyTheme path needs the dispatcher.</para>
    /// </summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void AvailableRuleTypes_Contains_DomainRegex_And_ProcessPath()
    {
        var vm = new VPNRouter.App.ViewModels.MainWindowViewModel();
        Assert.Contains("domain_regex", vm.AvailableRuleTypes);
        Assert.Contains("process_path", vm.AvailableRuleTypes);
        // Sanity: existing types still present.
        Assert.Contains("domain", vm.AvailableRuleTypes);
        Assert.Contains("ip_cidr", vm.AvailableRuleTypes);
    }
}
