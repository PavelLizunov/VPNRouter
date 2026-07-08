namespace VPNRouter.Core.Models;

public enum HealthAdviceSeverity
{
    Info,
    Warning,
    Critical
}

public enum HealthAdviceAction
{
    None,
    ChangeTransport,
    ChangeServer,
    BypassApp,
    TuneMtu,
    OpenDiagnostics
}

public sealed record HealthAdvice(
    HealthAdviceSeverity Severity,
    string Problem,
    string Why,
    string ActionText,
    HealthAdviceAction Action);
