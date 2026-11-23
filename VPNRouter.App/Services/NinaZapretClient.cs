using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace VPNRouter.App.Services;

internal static class NinaZapretClient
{
    internal const string Discord = "discord_gateway";
    internal const string Stop = "stop";

    private static string Executable => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NINA", "nina.exe");

    internal static async Task<bool> IsAvailableAsync()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(Executable)) return false;
        try
        {
            using var process = Launch("capabilities", "--json");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                using var json = JsonDocument.Parse(output);
                var root = json.RootElement;
                return process.ExitCode == 0 && root.GetProperty("schema").GetInt32() == 1
                    && root.GetProperty("platform").GetString() == "windows"
                    && root.GetProperty("operations").EnumerateArray().Any(op =>
                        op.GetString() == $"assistant repair-zapret {Discord}");
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill();
                return false;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or
            JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return false;
        }
    }

    internal static async Task<Reply> RunAsync(
        string target, string? subscription, IProgress<string> progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (subscription != null && (subscription.Any(char.IsControl) || Encoding.UTF8.GetByteCount(subscription) > 4094))
            return new("result", "invalid_subscription", null);
        using var process = Launch("assistant", "repair-zapret", target, "--json");
        using var input = process.StandardInput;
        if (subscription != null)
        {
            // Keep the pipe open after the subscription. EOF tells NINA to roll back.
            await input.WriteLineAsync(subscription);
            await input.FlushAsync();
        }
        else input.Close();

        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        using var rollback = new CancellationTokenSource();
        void RequestRollback()
        {
            try { input.Close(); }
            catch (IOException) { }
            rollback.CancelAfter(TimeSpan.FromSeconds(90));
        }
        using var cancel = ct.Register(RequestRollback);
        using var expire = deadline.Token.Register(RequestRollback);

        Reply? result = null;
        try
        {
            while (await process.StandardOutput.ReadLineAsync(rollback.Token) is { } line)
            {
                if (TryReadEvent(line) is not { } response || result != null) continue;
                if (response.Event == "result") result = response;
                else if (response.Profile is { } profile) progress.Report(profile);
            }
            await process.WaitForExitAsync(rollback.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill();
            return new("result", "run_failed", null);
        }
        if (process.ExitCode != 0 && result?.Code is ("repaired" or "already_working" or "stopped"))
            result = null;
        return result ?? new("result", "run_failed", null);
    }

    private static Process Launch(params string[] args)
    {
        var start = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(Executable)!,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new IOException("NINA did not start.");
    }

    internal sealed record Reply(string Event, string? Code, string? Profile);

    internal static Reply? TryReadEvent(string line)
    {
        if (line.Length > 2048) return null;
        try
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (root.GetProperty("schema").GetInt32() != 1) return null;
            var type = root.GetProperty("event").GetString();
            var profile = root.GetProperty("profile").GetString();
            if (profile != null && (profile.Length is < 1 or > 32
                || profile[0] is < 'a' or > 'z'
                || profile.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')))) return null;

            if (type == "trying" && profile != null) return new(type, null, profile);
            if (type == "result" && root.GetProperty("code").GetString() is { } code)
                return new(type, code, profile);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { }
        return null;
    }
}
