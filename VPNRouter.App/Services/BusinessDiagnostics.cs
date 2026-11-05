using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VPNRouter.Core;

namespace VPNRouter.App.Services;

internal interface IBusinessDiagnostics
{
    Task<string> CollectAsync(CancellationToken ct);
    Task SendAsync(string path, CancellationToken ct);
    async Task<BusinessSupportReply> SendWithReplyAsync(string path, Action acknowledged, CancellationToken ct)
    {
        await SendAsync(path, ct);
        acknowledged();
        return BusinessSupportReply.Legacy;
    }
}

internal sealed class BusinessDiagnostics(BusinessClientConfiguration configuration, BusinessClientHttp http,
    string? dataDirectory = null, string? reportRoot = null) : IBusinessDiagnostics
{
    // The elevated child receives immutable code over its private stdin pipe.
    // No lower-privilege writable file is ever selected for execution.
    private const string Bootstrap = """
        $ErrorActionPreference = 'Stop'
        $payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
        & ([scriptblock]::Create([string]$payload.Script)) -DataDir ([string]$payload.Data) -DestinationDir ([string]$payload.Output) -NoOpen
        if (!$?) { exit 1 }
        """;

    public async Task<string> CollectAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var root = reportRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VPNRouterBusinessSupport", "Client");
        BusinessPrivateFiles.CreateDirectory(root);
        var reportDirectory = Path.Combine(root, "Reports", Guid.NewGuid().ToString("N"));
        BusinessPrivateFiles.CreateDirectory(reportDirectory);
        string script;
        using (var source = typeof(BusinessDiagnostics).Assembly.GetManifestResourceStream("VPNRouter.Business.Collector")
            ?? throw new IOException("Diagnostic collector missing."))
        using (var reader = new StreamReader(source))
            script = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        var shellDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0");
        var start = new ProcessStartInfo(Path.Combine(shellDirectory, "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = shellDirectory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        start.Environment["PSModulePath"] = Path.Combine(shellDirectory, "Modules");
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Bootstrap)) })
            start.ArgumentList.Add(arg);
        var payload = JsonSerializer.Serialize(new { Script = script, Data = dataDirectory ?? AppPaths.DataDir, Output = reportDirectory });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        ct.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new IOException("Collector did not start.");
        // Drain privately; never copy raw process errors or paths to UI/logs.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteAsync(payload.AsMemory(), timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            throw;
        }
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        if (process.ExitCode != 0) throw new IOException("Collection failed.");
        var reports = Directory.GetFiles(reportDirectory, "VPNRouterBusiness-diagnostics-*.zip");
        if (reports.Length != 1) throw new IOException("Report missing.");
        BusinessPrivateFiles.RefuseLinks(reports[0]);
        return reports[0];
    }

    public Task SendAsync(string path, CancellationToken ct) => SendWithReplyAsync(path, static () => { }, ct);

    public Task<BusinessSupportReply> SendWithReplyAsync(string path, Action acknowledged, CancellationToken ct) => http.SendAsync(
        configuration.SupportEndpoint ?? throw new InvalidOperationException("Support is not configured."),
        configuration.SupportToken ?? throw new InvalidOperationException("Support is not configured."), path, ct, acknowledged);
}
