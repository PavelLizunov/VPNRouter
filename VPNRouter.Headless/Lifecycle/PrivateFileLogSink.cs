using Serilog.Core;
using Serilog.Events;

namespace VPNRouter.Headless.Lifecycle;

/// <summary>
/// Appends log events to one private file; stdout is reserved for the protocol.
/// Starts over when the file passes <see cref="MaxBytes"/>, keeping one previous file.
/// </summary>
internal sealed class PrivateFileLogSink(string path) : ILogEventSink
{
    internal const long MaxBytes = 5 * 1024 * 1024;
    private readonly object _gate = new();

    public void Emit(LogEvent logEvent)
    {
        var line = $"[{logEvent.Timestamp:yyyy-MM-dd HH:mm:ss}] [{logEvent.Level.ToString()[..3].ToUpperInvariant()}] " +
                   logEvent.RenderMessage() +
                   (logEvent.Exception is null ? "" : Environment.NewLine + logEvent.Exception) +
                   Environment.NewLine;
        lock (_gate)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, line);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
