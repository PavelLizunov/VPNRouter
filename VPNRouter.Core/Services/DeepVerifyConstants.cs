using System;

namespace VPNRouter.Core.Services;

internal static class DeepVerifyConstants
{
    public const string ProbeUrl = "https://www.cloudflare.com/cdn-cgi/trace";

    public static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(12);
}
