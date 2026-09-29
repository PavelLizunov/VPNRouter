using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using VPNRouter.Core.Models;
using VPNRouter.Core.Localization;
using VPNRouter.Core.Services.UpdateSources;

namespace VPNRouter.Core.Services;

public class UpdateChecker : IDesktopInstaller
{
    private readonly IHttpClient _http;
    private readonly UpdateSettings _settings;
    private readonly string _currentVersion;
    private readonly string _stagingDir;

    public event Action<int>? DownloadProgress;
    public event Action<string>? StatusChanged;

    public UpdateChecker(UpdateSettings settings, string currentVersion)
        : this(settings, currentVersion, PolicyHttpClient.Shared)
    {
    }

    public UpdateChecker(UpdateSettings settings, string currentVersion, IHttpClient http)
    {
        _settings = settings;
        _currentVersion = currentVersion;
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _stagingDir = Path.Combine(AppPaths.DataDir, "update-staging");
    }

    public Task<UpdateSourceInfo?> CheckAsync(CancellationToken ct = default)
    {
        var source = new GitHubReleaseSource(_settings, _currentVersion, _http, this);
        return source.CheckAsync(ct);
    }

    private static (int exit, string stdout, string stderr) RunWithCapture(
        string fileName, IEnumerable<string> args, int timeoutMs)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {fileName}");
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return (-1, "", "timeout");
        }
        try { outTask.Wait(500); } catch { }
        try { errTask.Wait(500); } catch { }
        return (proc.ExitCode,
                outTask.IsCompletedSuccessfully ? outTask.Result : "",
                errTask.IsCompletedSuccessfully ? errTask.Result : "");
    }

    internal readonly struct SemVer : IComparable<SemVer>, IEquatable<SemVer>
    {
        public readonly Version Core;
        public readonly int? Rc;

        public SemVer(Version core, int? rc) { Core = core; Rc = rc; }

        public int CompareTo(SemVer other)
        {
            var c = Core.CompareTo(other.Core);
            if (c != 0) return c;
            if (!Rc.HasValue && !other.Rc.HasValue) return 0;
            if (!Rc.HasValue) return 1;
            if (!other.Rc.HasValue) return -1;
            return Rc.Value.CompareTo(other.Rc.Value);
        }

        public bool Equals(SemVer other) => CompareTo(other) == 0;
        public override bool Equals(object? obj) => obj is SemVer v && Equals(v);
        public override int GetHashCode() => HashCode.Combine(Core, Rc);
        public override string ToString() => Rc.HasValue ? $"{Core}-r{Rc.Value}" : Core.ToString();
    }

    internal static bool TryParseSemVer(string? tag, out SemVer result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(tag)) return false;

        var s = tag.StartsWith('v') || tag.StartsWith('V') ? tag.Substring(1) : tag;

        var dash = s.IndexOf('-');
        string corePart = dash < 0 ? s : s.Substring(0, dash);
        string? suffix  = dash < 0 ? null : s.Substring(dash + 1);

        if (!Version.TryParse(corePart, out var core))
            return false;

        if (suffix is null)
        {
            result = new SemVer(core, null);
            return true;
        }

        if (suffix.Length < 2 || (suffix[0] != 'r' && suffix[0] != 'R'))
            return false;
        if (!int.TryParse(suffix.AsSpan(1), out var rc) || rc < 0)
            return false;

        result = new SemVer(core, rc);
        return true;
    }
}
