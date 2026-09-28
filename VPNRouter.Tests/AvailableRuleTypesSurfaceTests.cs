using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;
public class AvailableRuleTypesSurfaceTests
{
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void AvailableRuleTypes_Contains_DomainRegex_And_ProcessPath()
    {
        var vm = new VPNRouter.App.ViewModels.MainWindowViewModel();
        Assert.Contains("domain_regex", vm.AvailableRuleTypes);
        Assert.Contains("process_path", vm.AvailableRuleTypes);
        Assert.Contains("domain", vm.AvailableRuleTypes);
        Assert.Contains("ip_cidr", vm.AvailableRuleTypes);
    }
}
