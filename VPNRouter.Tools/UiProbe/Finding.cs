namespace VPNRouter.Tools.UiProbe;

public sealed record Finding(string Severity, string Rule, string Element, string Message);
