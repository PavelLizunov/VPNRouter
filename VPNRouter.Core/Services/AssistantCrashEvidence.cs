using System.Text;

namespace VPNRouter.Core.Services;

internal enum AssistantCrashFault
{
    Unknown, NullReference, InvalidOperation, Io, AccessDenied, OutOfMemory, Network, Configuration, Native,
}

internal enum AssistantCrashArea { Unknown, Ui, Startup, Routing, Settings }
internal enum AssistantCrashSignal { None, ProxyUnreachable, TunBusy, StopUnconfirmed }

internal sealed record AssistantCrashEvidence(bool Fatal, AssistantCrashFault Fault,
    AssistantCrashArea Area, AssistantCrashSignal Signal)
{
    private const int ReadLimit = 64 * 1024;

    internal static AssistantCrashEvidence? ReadLatest(string dataDirectory)
    {
        var directory = new DirectoryInfo(Path.Combine(dataDirectory, "crashes"));
        try
        {
            if (!directory.Exists) return null;
            foreach (var report in directory.EnumerateFiles("crash-*.txt")
                         .OrderByDescending(file => file.Name, StringComparer.Ordinal))
            {
                if (ReadReport(report) is { } evidence) return evidence;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    private static AssistantCrashEvidence? ReadReport(FileInfo report)
    {
        try
        {
            if ((report.Attributes & FileAttributes.ReparsePoint) != 0 || report.Length == 0) return null;

            using var file = report.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var bytes = new byte[ReadLimit];
            int length = file.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            var head = Encoding.UTF8.GetString(bytes, 0, length);
            if (file.Position >= file.Length) return Parse(head);

            // Keep the header and exception frames from the start and check recent log lines at the end.
            file.Position = Math.Max(file.Position, file.Length - ReadLimit) - 1;
            bool startsAtLine = file.ReadByte() == '\n';
            length = file.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            var tail = Encoding.UTF8.GetString(bytes, 0, length);
            int tailStart = startsAtLine ? 0 : tail.IndexOf('\n') + 1;
            if (!startsAtLine && tailStart == 0) return Parse(head);
            return Parse(string.Concat(head.AsSpan(0, head.LastIndexOf('\n') + 1), tail.AsSpan(tailStart)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static AssistantCrashEvidence? Parse(string report)
    {
        using var lines = new StringReader(report);
        if (lines.ReadLine() != "VPNRouter crash report") return null;
        bool? fatal = null;
        var fault = AssistantCrashFault.Unknown;
        var area = AssistantCrashArea.Unknown;
        var signal = AssistantCrashSignal.None;
        bool inException = false, inLog = false;
        while (lines.ReadLine() is { } line)
        {
            if (line == "──── Exception ────" && !inLog)
            {
                inException = true;
                continue;
            }
            if (line.StartsWith("──── Tail of vpnrouter", StringComparison.Ordinal))
            {
                inLog = true;
                continue;
            }
            if (inLog)
            {
                var entry = ReadSignal(line);
                if (entry != AssistantCrashSignal.None) signal = entry;
            }
            else if (inException)
            {
                if (fault == AssistantCrashFault.Unknown) fault = ReadFault(line.TrimStart());
                if (area == AssistantCrashArea.Unknown) area = ReadArea(line.TrimStart());
            }
            else if (line.StartsWith("Fatal:", StringComparison.Ordinal)
                     && bool.TryParse(line.AsSpan(6).Trim(), out bool terminating))
            {
                fatal = terminating;
            }
        }
        return fatal is { } value ? new(value, fault, area, signal) : null;
    }

    private static AssistantCrashFault ReadFault(string line)
    {
        if (line.StartsWith("---> ", StringComparison.Ordinal)) line = line[5..];
        string type = line.Split(':', 2)[0];
        return type switch
        {
            "System.NullReferenceException" => AssistantCrashFault.NullReference,
            "System.InvalidOperationException" or "System.ObjectDisposedException" => AssistantCrashFault.InvalidOperation,
            "System.IO.IOException" or "System.IO.FileNotFoundException" => AssistantCrashFault.Io,
            "System.UnauthorizedAccessException" => AssistantCrashFault.AccessDenied,
            "System.OutOfMemoryException" => AssistantCrashFault.OutOfMemory,
            "System.Net.Http.HttpRequestException" or "System.Net.Sockets.SocketException" => AssistantCrashFault.Network,
            "System.Text.Json.JsonException" or "YamlDotNet.Core.YamlException" => AssistantCrashFault.Configuration,
            "System.AccessViolationException" or "System.Runtime.InteropServices.SEHException" => AssistantCrashFault.Native,
            _ => AssistantCrashFault.Unknown,
        };
    }

    private static AssistantCrashArea ReadArea(string line)
    {
        if (line.StartsWith("at VPNRouter.App.Program.", StringComparison.Ordinal)
            || line.StartsWith("at VPNRouter.Core.Services.StartupPipeline.", StringComparison.Ordinal))
            return AssistantCrashArea.Startup;
        if (line.StartsWith("at VPNRouter.App.Views.", StringComparison.Ordinal)
            || line.StartsWith("at Avalonia.", StringComparison.Ordinal))
            return AssistantCrashArea.Ui;
        if (line.StartsWith("at VPNRouter.Core.Services.SettingsLoader.", StringComparison.Ordinal)
            || line.StartsWith("at VPNRouter.Core.Services.RealSettingsStore.", StringComparison.Ordinal))
            return AssistantCrashArea.Settings;
        if (line.StartsWith("at VPNRouter.Core.Services.VpnEngine.", StringComparison.Ordinal)
            || line.StartsWith("at VPNRouter.Core.Services.SingBoxManager.", StringComparison.Ordinal))
            return AssistantCrashArea.Routing;
        return AssistantCrashArea.Unknown;
    }

    private static AssistantCrashSignal ReadSignal(string line)
    {
        if (line.Contains("proxy unreachable", StringComparison.OrdinalIgnoreCase)
            || line.Contains("proxy-unreachable", StringComparison.Ordinal))
            return AssistantCrashSignal.ProxyUnreachable;
        if (line.Contains("TUN adapter busy", StringComparison.OrdinalIgnoreCase))
            return AssistantCrashSignal.TunBusy;
        if (line.Contains("Stop unconfirmed", StringComparison.OrdinalIgnoreCase))
            return AssistantCrashSignal.StopUnconfirmed;
        return AssistantCrashSignal.None;
    }
}
