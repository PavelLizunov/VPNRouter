namespace VPNRouter.App.ViewModels.Internals;

// View order only: a refresh never changes the user's selection or tunnel.
internal static class BusinessServerChoiceOrdering
{
    internal static BusinessServerChoiceViewModel[] Sort(IEnumerable<BusinessServerChoiceViewModel> choices) =>
        choices.Select((choice, index) => (Choice: choice, Index: index))
            .OrderBy(row => row.Choice.MetadataLoad.HasValue ? 0 : 1)
            .ThenBy(row => row.Choice.MetadataLoad ?? uint.MaxValue)
            .ThenBy(row => row.Choice.MetadataMemoryUsedPercent ?? double.MaxValue)
            .ThenBy(row => row.Index)
            .Select(row => row.Choice).ToArray();
}
