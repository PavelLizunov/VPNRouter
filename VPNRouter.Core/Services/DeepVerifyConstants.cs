using System;

namespace VPNRouter.Core.Services;

/// <summary>
/// Single source of truth for deep-verify probe constants that were previously
/// duplicated literally across the deep verifiers (plan T2-A/B). Shared by
/// <see cref="FreeConfigs.FreeConfigDeepVerifier"/> and
/// <see cref="VlessDeepVerifier"/>. (AndroidFreeConfigDeepVerifier keeps its own
/// copy for now — it compiles under the separate Android .NET 10 toolchain;
/// folding it in is a follow-up, see plans/codebase-reduction-and-split-plan.md.)
/// </summary>
internal static class DeepVerifyConstants
{
    /// <summary>URL probed for verification. Cloudflare's trace endpoint — small, fast, globally distributed.</summary>
    public const string ProbeUrl = "https://www.cloudflare.com/cdn-cgi/trace";

    /// <summary>Overall per-config deep-verify timeout.</summary>
    public static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(12);
}
