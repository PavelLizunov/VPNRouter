#nullable enable

using Xunit;

namespace VPNRouter.Tests.Fakes;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SafeModeStateCollection
{
    public const string Name = "SafeMode-global-state";
}
