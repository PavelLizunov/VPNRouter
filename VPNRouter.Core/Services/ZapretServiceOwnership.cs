namespace VPNRouter.Core.Services;

internal sealed record ZapretServiceOwnership(string LegacyBin, string ProtectedBin)
{
    internal static readonly string[] DriverNames = ["WinDivert", "WinDivert14", "WinDivert15"];
    internal const string Service = "zapret";
    internal const string EditionName = "VPNRouter";

    internal static ZapretServiceOwnership Current()
    {
        var data = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!Path.IsPathFullyQualified(data)) throw new IOException("Cannot resolve ProgramData.");
        return new(Path.Combine(data, EditionName, "zapret", "bin"),
            Path.Combine(ZapretRuntimeDirectory.Root, "zapret", "bin"));
    }

    internal static bool Matches(string name, string? image, string bin) => FileName(name, image, bin) != null;

    internal static string? FileName(string name, string? image, string bin)
    {
        if (string.IsNullOrWhiteSpace(image)) return null;
        bool process = name.Equals(Service, StringComparison.OrdinalIgnoreCase);
        if (!process && !DriverNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return null;
        foreach (var file in process ? new[] { "winws.exe" } : new[] { "WinDivert64.sys", "WinDivert32.sys" })
            if (IsImage(image, Path.Combine(bin, file), process)) return file;
        return null;
    }

    internal static bool IsImage(string? image, string expected, bool allowArguments)
    {
        if (string.IsNullOrWhiteSpace(image) || image.Any(c => c is '\0' or '\n' or '\r')) return false;
        var value = image.Trim();
        if (!allowArguments)
        {
            // A kernel driver ImagePath is a single filename, even with spaces.
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
            if (value.StartsWith(@"\??\", StringComparison.Ordinal) || value.StartsWith(@"\\?\", StringComparison.Ordinal))
                value = value[4..];
            return value.Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        if (value.StartsWith('"')) expected = "\"" + expected + "\"";
        else if (expected.Any(char.IsWhiteSpace)) return false;
        return value.Equals(expected, StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(expected, StringComparison.OrdinalIgnoreCase)
               && value.Length > expected.Length && value[expected.Length] is ' ' or '\t';
    }

    internal bool Owns(string name, ServiceConfig config)
    {
        bool process = name == Service;
        return config.Type == (process ? 0x10u : 1u)
            && (Matches(name, config.Image, ProtectedBin) || Matches(name, config.Image, LegacyBin));
    }

    internal CleanupAction Remove(string name, Func<string, bool, IWindowsRegistration?> open, bool dryRun)
    {
        bool shared = name != Service;
        using var lookup = open(name, false);
        if (lookup == null) return new("zapret", name, CleanupOutcome.NotPresent);
        if (!Owns(name, lookup.Config))
            return new("zapret", name, CleanupOutcome.Skipped, "service owner is another program or could not be verified");
        if (shared) return new("zapret", name, CleanupOutcome.Skipped, "shared WinDivert driver may be in use");
        if (dryRun) return new("zapret", name, CleanupOutcome.WouldRemove);
        using var registration = open(name, true);
        if (registration == null || !Owns(name, registration.Config))
            return new("zapret", name, CleanupOutcome.Skipped, "service ownership changed");
        registration.Stop();
        if (!Owns(name, registration.Config))
            return new("zapret", name, CleanupOutcome.Skipped, "service ownership changed");
        registration.Delete();
        return new("zapret", name, CleanupOutcome.Removed);
    }
}
