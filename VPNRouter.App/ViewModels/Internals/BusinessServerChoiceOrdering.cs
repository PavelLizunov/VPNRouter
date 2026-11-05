using VPNRouter.App.Services;

namespace VPNRouter.App.ViewModels.Internals;

// View order only: a refresh never changes the user's selection or tunnel.
internal static class BusinessServerChoiceOrdering
{
    internal static BusinessServerChoiceViewModel[] Sort(IEnumerable<BusinessServerChoiceViewModel> choices) =>
        choices.Select((choice, index) => (Choice: choice, Index: index))
            .OrderBy(row => row.Choice.ActivityLevel switch
            {
                BusinessNodeActivityLevel.Low => 0,
                BusinessNodeActivityLevel.Medium => 1,
                BusinessNodeActivityLevel.High => 2,
                _ => 3
            })
            .ThenBy(row => row.Index)
            .Select(row => row.Choice).ToArray();
}
