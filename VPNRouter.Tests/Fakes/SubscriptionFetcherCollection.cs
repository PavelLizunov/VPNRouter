#nullable enable

using Xunit;

namespace VPNRouter.Tests.Fakes;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SubscriptionFetcherCollection
{
    public const string Name = "SubscriptionFetcher-global-http";
}
