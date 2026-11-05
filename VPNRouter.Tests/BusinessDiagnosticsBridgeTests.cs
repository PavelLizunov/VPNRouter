using System.IO.Compression;
using System.Net.Http;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using VPNRouter.App.Services;

namespace VPNRouter.Tests;

public sealed class BusinessDiagnosticsBridgeTests
{
    [Fact]
    public async Task EmbeddedCollector_ProducesRedactedAllowlistedReport_WithoutStartingVpnOrUploading()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows PowerShell bridge only.");
        var root = Path.Combine(Path.GetTempPath(), "business-client-collector-" + Guid.NewGuid().ToString("N"));
        BusinessPrivateFiles.CreateDirectory(root);
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(Path.Combine(data, "logs"));
        await File.WriteAllTextAsync(Path.Combine(data, "logs", "vpnrouter.log"),
            "2026-11-04 12:00:00 fixture-event password=fixture-password-must-be-masked\n" +
            "2026-11-04 12:00:01 C:\\Users\\Fixture Bridge User\\AppData\\Local\\fixture\n");
        await File.WriteAllTextAsync(Path.Combine(data, "config.yaml"), "must-not-copy-this-configuration");
        using var client = new HttpClient(new BusinessClientHttpTests.Handler((_, _) =>
            throw new InvalidOperationException("Collection must not upload")));
        using var http = new BusinessClientHttp(client);
        try
        {
            var reports = Path.Combine(root, "reports");
            var diagnostics = new BusinessDiagnostics(BusinessClientConfiguration.Empty, http, data, reports);
            var zip = await diagnostics.CollectAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(100));
            Assert.True(File.Exists(zip));
            Assert.False(Directory.Exists(Path.Combine(reports, "Staging")));
            Assert.Empty(Directory.EnumerateFiles(reports, "*.ps1", SearchOption.AllDirectories));
            using var archive = ZipFile.OpenRead(zip);
            Assert.Contains(archive.Entries, entry => entry.FullName == "summary.json");
            Assert.Contains(archive.Entries, entry => entry.FullName == "app-01.txt");
            using (var reader = new StreamReader(archive.GetEntry("summary.json")!.Open()))
            using (var summary = JsonDocument.Parse(await reader.ReadToEndAsync()))
            {
                var device = summary.RootElement.GetProperty("device");
                Assert.Equal(new[] { "computer_name", "user_name" }, device.EnumerateObject()
                    .Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
                Assert.True(device.GetProperty("computer_name").GetString() == Environment.MachineName,
                    "The embedded collector must report the collecting OS computer.");
                Assert.True(device.GetProperty("user_name").GetString() == Environment.UserName,
                    "The embedded collector must report the collecting OS user.");
                Assert.All(device.EnumerateObject(), property => Assert.InRange(property.Value.GetString()!.Length, 1, 128));
            }
            foreach (var entry in archive.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                var text = await reader.ReadToEndAsync();
                Assert.DoesNotContain("fixture-password-must-be-masked", text);
                Assert.DoesNotContain("must-not-copy-this-configuration", text);
                Assert.DoesNotContain("Fixture Bridge User", text);
                if (entry.FullName == "README.txt") Assert.Contains("not the authenticated employee-key identity", text);
            }
            var acl = new DirectoryInfo(reports).GetAccessControl();
            Assert.True(acl.AreAccessRulesProtected);
            using var identity = WindowsIdentity.GetCurrent();
            var allowed = new[] { identity.User!.Value, "S-1-5-18", "S-1-5-32-544" };
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow)
                    Assert.Contains(((SecurityIdentifier)rule.IdentityReference).Value, allowed);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
