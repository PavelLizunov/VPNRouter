#nullable enable

using Xunit;

namespace VPNRouter.Tests.Fakes;

/// <summary>
/// Serializes tests that replace the process-global <c>SubscriptionFetcher.Http</c> seam.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SubscriptionFetcherCollection
{
    public const string Name = "SubscriptionFetcher-global-http";
}
