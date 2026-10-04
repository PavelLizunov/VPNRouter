using Xunit;

namespace VPNRouter.Tests;

// Tests that change the probe's static context (is the tunnel up, which interface to bind) must not run next to each other.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProbeStaticsCollection
{
    public const string Name = "ProbeStatics";
}
