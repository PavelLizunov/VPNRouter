using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace VPNRouter.Tests;

public sealed class BusinessSupportCollectorTests
{
    private static readonly string SupportDir = Path.Combine(FindRoot(), "packaging", "windows", "support");

    [Fact]
    public async Task Collector_StandaloneZipMasksSecretsAndExcludesConfiguration()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.Data, "logs"));
        Directory.CreateDirectory(Path.Combine(fixture.Data, "crashes"));
        File.WriteAllText(Path.Combine(fixture.Data, "config.yaml"), "password: do-not-copy-config");
        File.WriteAllText(Path.Combine(fixture.Data, "logs", "vpnrouter20261102.log"), """
            [2026-11-02 12:00:00 INF] VPN connected split direct
            password="quoted first second"
            token='single first second'
            "private_key": "line one
            line two"
            password: |
              block first
              block second
            Authorization: Bearer header-sensitive
            vless://uri-sensitive@192.0.2.1:443
            https://example.invalid/private-subscription
            naive+quic://bob:shortpass@example.invalid:443
            custom.protocol+variant://bob:otherpass@example.invalid
            sid=shortsidvalue
            12345678-1234-1234-1234-123456789abc
            -----BEGIN PRIVATE KEY-----
            pem-sensitive
            -----END PRIVATE KEY-----
            C:\Users\ExampleEmployee\AppData
            C:\Users\Spaced Employee\AppData
            C:/Users/Forward Employee/AppData
            [2026-11-02 12:00:01 INF] VPN disconnected cleanly
            """);
        File.WriteAllText(Path.Combine(fixture.Data, "logs", "vpnrouter-arbitrary-secret.log"), "unlisted-sensitive");
        File.WriteAllText(Path.Combine(fixture.Data, "crashes", "crash-20261102-120000-001.txt"), "Exception: synthetic crash stack");
        var result = await Collect(fixture);
        Assert.Equal(0, result.Code);
        var entries = ReadBundle(fixture);
        var text = string.Join('\n', entries.Values);
        Assert.Contains("VPN connected split direct", text);
        Assert.Contains("VPN disconnected cleanly", text);
        Assert.Contains("synthetic crash stack", text);
        foreach (var secret in new[] { "quoted first", "second", "single first", "line one", "line two", "block first", "block second", "header-sensitive", "uri-sensitive", "private-subscription", "shortpass", "otherpass", "shortsidvalue", "12345678-1234", "pem-sensitive", "ExampleEmployee", "Spaced Employee", "Forward Employee", "unlisted-sensitive", "do-not-copy-config" })
            Assert.DoesNotContain(secret, text);
        Assert.All(entries.Keys, name => Assert.DoesNotContain("config", name));
        using var manifest = JsonDocument.Parse(entries["summary.json"]);
        Assert.Equal("vpnrouter-business-support/1", manifest.RootElement.GetProperty("format").GetString());
        Assert.Equal(2, manifest.RootElement.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task Collector_MissingDataStillExplainsWhyNoLogsExist()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var result = await Collect(fixture);
        Assert.Equal(0, result.Code);
        var entries = ReadBundle(fixture);
        Assert.Equal(2, entries.Count);
        Assert.Contains("folder missing", entries["summary.json"]);
        Assert.False(Directory.Exists(fixture.Data));
    }

    [Fact]
    public async Task Collector_LegacyPilotElevationErrorIsCollectedWithoutOtherConsumerData()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var logs = Path.Combine(fixture.LegacyData, "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "vpnrouter-launch-error.log"), "VPNRouter failed to elevate to administrator.\nReason: Win32Exception: cancelled by user.\ntoken=legacy-sensitive");
        File.WriteAllText(Path.Combine(logs, "vpnrouter20261102.log"), "consumer-sensitive");
        Assert.Equal(0, (await Collect(fixture)).Code);
        var entries = ReadBundle(fixture);
        Assert.Contains("failed to elevate", entries["legacy-launch-01.txt"]);
        var text = string.Join('\n', entries.Values);
        Assert.DoesNotContain("legacy-sensitive", text);
        Assert.DoesNotContain("consumer-sensitive", text);
    }

    [Fact]
    public async Task Collector_BoundsTailAndFileCountAndOmitsIncompleteEvents()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.Data, "logs"));
        for (var i = 1; i <= 8; i++)
        {
            var path = Path.Combine(fixture.Data, "logs", $"vpnrouter202611{i:00}.log");
            File.WriteAllText(path, "password: |\n" + new string('x', 2 * 1024 * 1024) + "tail-sensitive\n[2026-11-02 12:00:00 INF] recent-event");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(i));
        }
        Assert.Equal(0, (await Collect(fixture)).Code);
        var entries = ReadBundle(fixture);
        Assert.Equal(5, entries.Keys.Count(k => k.StartsWith("app-")));
        foreach (var text in entries.Where(kv => kv.Key.StartsWith("app-")).Select(kv => kv.Value))
        {
            Assert.Contains("recent-event", text);
            Assert.DoesNotContain("tail-sensitive", text);
            Assert.True(text.Length < 200);
        }
    }

    [Fact]
    public async Task Collector_Utf8BoundaryInsideCyrillicCharacterRetainsCompleteEvents()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.Data, "logs"));
        const string ending = "\n[2026-11-02 12:00:00 INF] multibyte-tail-event";
        var prefix = Encoding.UTF8.GetBytes(new string('\u044f', 1024 * 1024));
        var suffix = Encoding.UTF8.GetBytes(ending);
        // Make the final 1MiB start on the second byte of a two-byte character.
        if (suffix.Length % 2 == 0) suffix = suffix.Concat(new byte[] { (byte)' ' }).ToArray();
        File.WriteAllBytes(Path.Combine(fixture.Data, "logs", "vpnrouter20261102.log"), prefix.Concat(suffix).ToArray());
        Assert.Equal(0, (await Collect(fixture)).Code);
        var entries = ReadBundle(fixture);
        Assert.Contains("multibyte-tail-event", entries["app-01.txt"]);
    }

    [Fact]
    public async Task Collector_UnreadableAndNonUtf8FilesAreOmittedWithoutRawErrors()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var logs = Path.Combine(fixture.Data, "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "singbox.log"), "password: utf16-sensitive", Encoding.Unicode);
        var path = Path.Combine(logs, "vpnrouter20261102.log");
        File.WriteAllText(path, "locked-sensitive");
        using var locked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = await Collect(fixture);
        Assert.Equal(0, result.Code);
        var text = string.Join('\n', ReadBundle(fixture).Values);
        Assert.Contains("omitted", text);
        Assert.DoesNotContain("locked-sensitive", text);
        Assert.DoesNotContain("utf16-sensitive", text);
        Assert.DoesNotContain("UnauthorizedAccessException", text);
    }

    [Fact]
    public async Task Collector_RefusesLinkedInputFolder()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Data);
        var outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "vpnrouter20261102.log"), "linked-sensitive");
        var result = await RunPowerShell("-NoProfile", "-Command",
            """
            & (Join-Path ([Environment]::GetFolderPath('System')) 'cmd.exe') /d /c 'mklink /J "%SUPPORT_TEST_LINK%" "%SUPPORT_TEST_TARGET%"'
            exit $LASTEXITCODE
            """,
            environment: new() { ["SUPPORT_TEST_LINK"] = Path.Combine(fixture.Data, "logs"), ["SUPPORT_TEST_TARGET"] = outside });
        Assert.True(result.Code == 0, result.Output);
        try
        {
            Assert.Equal(0, (await Collect(fixture)).Code);
            var text = string.Join('\n', ReadBundle(fixture).Values);
            Assert.Contains("linked", text);
            Assert.DoesNotContain("linked-sensitive", text);
        }
        finally { Directory.Delete(Path.Combine(fixture.Data, "logs")); }
    }

    [Fact]
    public async Task Collector_UnwritableDestinationFailsWithoutChangingInputs()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Data);
        File.WriteAllText(fixture.Output, "destination-is-a-file");
        var result = await Collect(fixture);
        Assert.Equal(1, result.Code);
        Assert.Equal("destination-is-a-file", File.ReadAllText(fixture.Output));
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Data));
    }

    [Theory]
    [InlineData("http://example.invalid/upload")]
    [InlineData("https://user:password@example.invalid/upload")]
    [InlineData("https://example.invalid/upload#private-fragment")]
    public async Task Sender_RejectsUnsafeEndpointAndKeepsZip(string endpoint)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        Assert.Equal(0, (await Collect(fixture)).Code);
        var zip = Directory.GetFiles(fixture.Output, "*.zip").Single();
        var before = File.ReadAllBytes(zip);
        var result = await RunPowerShell("-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(SupportDir, "Send-VPNRouterBusiness-Diagnostics.ps1"), "-ZipPath", zip, "-UploadEndpoint", endpoint);
        Assert.Equal(1, result.Code);
        Assert.DoesNotContain(endpoint, result.Output);
        Assert.DoesNotContain("password", result.Output);
        Assert.Equal(before, File.ReadAllBytes(zip));
    }

    [Fact]
    public async Task Sender_StreamsExpectedRequestHandlesRefusalAndNeverPrintsReceiverBody()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        Assert.Equal(0, (await Collect(fixture)).Code);
        var zip = Directory.GetFiles(fixture.Output, "*.zip").Single();
        var tokenFile = Path.Combine(fixture.Root, "token.txt");
        File.WriteAllText(tokenFile, "synthetic.bearer-token");
        var test = """"
            $ErrorActionPreference = 'Stop'
            Add-Type -AssemblyName System.Net.Http
            . $env:SUPPORT_SEND_SCRIPT -ZipPath unused.zip -UploadEndpoint https://example.invalid/reports
            Add-Type -ReferencedAssemblies System.Net.Http -TypeDefinition @'
            using System;
            using System.Net;
            using System.Net.Http;
            using System.Threading;
            using System.Threading.Tasks;
            public sealed class SupportTestHandler : HttpMessageHandler {
                public int Status = 201, Calls;
                public byte[] Body;
                public string Method, MediaType, Auth, Format;
                protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) {
                    Calls++;
                    Method = r.Method.Method;
                    MediaType = r.Content.Headers.ContentType.MediaType;
                    Auth = r.Headers.Authorization.ToString();
                    Format = String.Join(",", r.Headers.GetValues("X-VPNRouter-Report-Format"));
                    Body = r.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                    return Task.FromResult(new HttpResponseMessage((HttpStatusCode)Status) { Content = new StringContent("receiver-sensitive") });
                }
            }
            '@
            $handler = New-Object SupportTestHandler
            $client = [Net.Http.HttpClient]::new($handler)
            try {
                $status = Send-SupportBundle $env:SUPPORT_ZIP https://example.invalid/reports $env:SUPPORT_TOKEN $client
                if ($status -ne 201 -or $handler.Calls -ne 1 -or $handler.Method -ne 'POST' -or $handler.MediaType -ne 'application/zip' -or $handler.Auth -ne 'Bearer synthetic.bearer-token' -or $handler.Format -ne 'vpnrouter-business-support/1') { throw 'Invalid request.' }
                $expected = [IO.File]::ReadAllBytes($env:SUPPORT_ZIP)
                if ([Convert]::ToBase64String($expected) -ne [Convert]::ToBase64String($handler.Body)) { throw 'Wrong ZIP body.' }
                foreach ($status in @(307, 401, 413, 500)) {
                    $handler.Status = $status
                    $failed = $false
                    try { [void](Send-SupportBundle $env:SUPPORT_ZIP https://example.invalid/reports $env:SUPPORT_TOKEN $client) } catch { $failed = $true }
                    if (!$failed -or !(Test-Path -LiteralPath $env:SUPPORT_ZIP)) { throw 'Failure lost the ZIP.' }
                }
                if ($handler.Calls -ne 5) { throw 'Unexpected retry.' }
                # Reject oversize inputs before invoking transport.
                $large = [IO.File]::Create($env:SUPPORT_LARGE_ZIP)
                try { $large.SetLength(24MB + 1) } finally { $large.Dispose() }
                $failed = $false
                try { [void](Send-SupportBundle $env:SUPPORT_LARGE_ZIP https://example.invalid/reports '' $client) } catch { $failed = $true }
                if (!$failed -or $handler.Calls -ne 5) { throw 'Oversize upload attempted.' }
                Write-Output 'support transport checks passed'
            } finally { $client.Dispose() }
            """";
        var testPath = Path.Combine(fixture.Root, "transport-test.ps1");
        File.WriteAllText(testPath, test);
        var result = await RunPowerShell("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", testPath,
            environment: new()
            {
                ["SUPPORT_SEND_SCRIPT"] = Path.Combine(SupportDir, "Send-VPNRouterBusiness-Diagnostics.ps1"),
                ["SUPPORT_ZIP"] = zip, ["SUPPORT_TOKEN"] = tokenFile,
                ["SUPPORT_LARGE_ZIP"] = Path.Combine(fixture.Root, "large.zip")
            });
        Assert.True(result.Code == 0, result.Output);
        Assert.Contains("support transport checks passed", result.Output);
        Assert.DoesNotContain("receiver-sensitive", result.Output);
    }

    private static Task<(int Code, string Output)> Collect(Fixture fixture) => RunPowerShell(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(SupportDir, "Collect-VPNRouterBusiness-Diagnostics.ps1"),
        "-DataDir", fixture.Data, "-DestinationDir", fixture.Output, "-LegacyDataDir", fixture.LegacyData, "-NoOpen");

    private static Dictionary<string, string> ReadBundle(Fixture fixture)
    {
        using var archive = ZipFile.OpenRead(Directory.GetFiles(fixture.Output, "*.zip").Single());
        return archive.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var reader = new StreamReader(e.Open());
            return reader.ReadToEnd();
        });
    }

    private static Task<(int Code, string Output)> RunPowerShell(params string[] args) => RunPowerShell(args, null);
    private static Task<(int Code, string Output)> RunPowerShell(string arg1, string arg2, string arg3, Dictionary<string, string> environment) =>
        RunPowerShell([arg1, arg2, arg3], environment);
    private static Task<(int Code, string Output)> RunPowerShell(string arg1, string arg2, string arg3, string arg4, string arg5, Dictionary<string, string> environment) =>
        RunPowerShell([arg1, arg2, arg3, arg4, arg5], environment);

    private static async Task<(int Code, string Output)> RunPowerShell(string[] args, Dictionary<string, string>? environment)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        if (environment != null)
            foreach (var (key, value) in environment) start.Environment[key] = value;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await stdout + await stderr);
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "VPNRouter.sln"))) return dir.FullName;
        throw new DirectoryNotFoundException();
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "vpnrouter-support-test [space]&-" + Guid.NewGuid().ToString("N"));
        public string Data => Path.Combine(Root, "data");
        public string LegacyData => Path.Combine(Root, "legacy");
        public string Output => Path.Combine(Root, "output");
        public Fixture() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
