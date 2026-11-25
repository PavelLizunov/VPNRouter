using System;

namespace VPNRouter.Core.Services;

internal static class DeepVerifyConstants
{
    public const string DetourNote = "Verification requires the configured upstream chain";
    public const string ProbeUrl = "https://www.cloudflare.com/cdn-cgi/trace";
    public const string NoContentProbeUrl = "https://www.gstatic.com/generate_204";
    public const string StunHost = "stun.cloudflare.com";
    public const int StunPort = 3478;

    public static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(12);
}
