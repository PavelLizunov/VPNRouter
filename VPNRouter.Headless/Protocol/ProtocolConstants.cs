namespace VPNRouter.Headless.Protocol;

public static class ProtocolConstants
{
    public const int ProtocolVersion = 1;
    public const int MaxInputFrameBytes = 256 * 1024;   // 256 KiB BEFORE accumulation
    public const int MaxOutputFrameBytes = 256 * 1024;  // 256 KiB max output frame
    public const int MaxJsonDepth = 32;                 // Max JSON nesting depth
    public const int MaxOutputQueueFrames = 8;          // Bounded output queue capacity
    public const int MaxIdLength = 64;                  // Maximum length of request ID
    public const int PageSize = 100;                    // Default pagination row limit

    public const int MaxUrgentOperations = 4;           // Max concurrent urgent/read-only operations
    public const int HandlerDisposalTimeoutSeconds = 3; // Deadline for handler disposal on shutdown
    public const int OutputDrainTimeoutSeconds = 3;     // Deadline for output queue drain
    public const int InFlightDrainTimeoutSeconds = 2;   // Deadline for in-flight task drain on EOF
    public const int OutputWriteTimeoutSeconds = 3;     // Deadline for per-frame write+flush stall

    private static readonly HashSet<string> s_knownMethods = new(StringComparer.Ordinal)
    {
        "snapshot",
        "connect",
        "disconnect",
        "cancel",
        "servers.list",
        "servers.import",
        "servers.select",
        "servers.remove",
        "servers.test",
        "servers.verify",
        "subscriptions.list",
        "subscriptions.add",
        "subscriptions.remove",
        "subscriptions.refresh",
        "subscriptions.enable",
        "free.list",
        "free.refresh",
        "free.test",
        "free.verify",
        "free.apply",
        "apps.list",
        "apps.set",
        "routing.set",
        "profiles.list",
        "profiles.select",
        "profiles.refresh",
        "rules.get",
        "rules.set",
        "rules.import",
        "rules.export",
        "custom.list",
        "custom.import",
        "custom.select",
        "custom.remove",
        "settings.get",
        "settings.set",
        "diagnostics.check",
        "diagnostics.export"
    };

    public static bool IsKnownMethod(string method) => s_knownMethods.Contains(method);
    public static bool IsUrgentOrReadOnlyMethod(string method) => method is "cancel" or "disconnect" or "snapshot";
}
