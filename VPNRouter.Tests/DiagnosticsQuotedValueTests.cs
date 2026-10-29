using System.IO.Compression;
using VPNRouter.Core;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.Diagnostics;

namespace VPNRouter.Tests;

public sealed class DiagnosticsQuotedValueTests
{
    [Theory]
    [InlineData("password=\"alpha beta\" status=ok", "password=\"***\" status=ok")]
    [InlineData("token='alpha beta' status=ok", "token='***' status=ok")]
    [InlineData("password=\"alpha,beta\"", "password=\"***\"")]
    [InlineData("token='alpha,beta'", "token='***'")]
    [InlineData("password=\"alpha 'beta' gamma\"", "password=\"***\"")]
    [InlineData("token='alpha \"beta\" gamma'", "token='***'")]
    [InlineData("password=\"alpha\\\" beta\" status=ok", "password=\"***\" status=ok")]
    [InlineData("token='alpha\\' beta' status=ok", "token='***' status=ok")]
    [InlineData("password=\"alpha\\\\\" status=ok", "password=\"***\" status=ok")]
    [InlineData("token='alpha\\\\' status=ok", "token='***' status=ok")]
    [InlineData("password=\"alpha\\\\\\\" beta\" status=ok", "password=\"***\" status=ok")]
    [InlineData("password=\"\" status=ok", "password=\"***\" status=ok")]
    [InlineData("token='' status=ok", "token='***' status=ok")]
    [InlineData("password=\"alpha beta", "password=\"***")]
    [InlineData("token='alpha beta", "token='***")]
    [InlineData("password=\"alpha beta\\", "password=\"***")]
    [InlineData("token='alpha beta\\", "token='***")]
    [InlineData("password=\"", "password=\"***")]
    [InlineData("token='", "token='***")]
    [InlineData("{\"password\":\"alpha beta\",\"token\":'gamma delta',\"port\":443}",
        "{\"password\":\"***\",\"token\":'***',\"port\":443}")]
    [InlineData("password=\"alpha beta\" token='gamma delta' status=ok",
        "password=\"***\" token='***' status=ok")]
    [InlineData("Authorization: \"Bearer alpha beta\" status=ok", "Authorization: \"***\" status=ok")]
    [InlineData("password=\"vless://user@example.test:443\" status=ok", "password=\"***\" status=ok")]
    [InlineData("password=\"https://example.test/private\" status=ok", "password=\"***\" status=ok")]
    [InlineData("password=\"alpha beta\"\r\nstatus=ok", "password=\"***\"\nstatus=ok")]
    [InlineData("password=\"alpha beta\nstatus=ok", "password=\"***\nstatus=ok")]
    public void RedactLogText_QuotedValues_MasksWholeValuePreservesBoundaries(string input, string expected)
    {
        Assert.Equal(expected.Replace("\n", Environment.NewLine), DiagnosticsRedactor.RedactLogText(input));
    }

    [Theory]
    [InlineData("password=alpha status=ok", "password=*** status=ok")]
    [InlineData("password: alpha, port=443", "password: ***, port=443")]
    [InlineData("Authorization: Bearer abc status=ok", "Authorization: *** status=ok")]
    [InlineData("Proxy-Authorization: Basic abc status=ok", "Proxy-Authorization: *** status=ok")]
    [InlineData("authorization: Digest abc status=ok", "authorization: *** status=ok")]
    [InlineData("authorization: Negotiate abc status=ok", "authorization: *** status=ok")]
    [InlineData("authorization: Token abc status=ok", "authorization: *** status=ok")]
    [InlineData("bypass=true public_key=PUBOK key=benign port=443", "bypass=true public_key=PUBOK key=benign port=443")]
    public void RedactLogText_UnquotedAndBenignValues_PreservesExistingContract(string input, string expected)
    {
        Assert.Equal(expected, DiagnosticsRedactor.RedactLogText(input));
    }

    [Theory]
    [InlineData("password=vless://alpha,beta@example.test:443 status=ok")]
    [InlineData("password=https://alpha,beta@example.test/private status=ok")]
    [InlineData("token=trojan://alpha'beta@example.test:443 status=ok")]
    [InlineData("Authorization: Bearer vless://alpha,beta@example.test:443 status=ok")]
    public void RedactLogText_UnquotedUriWithValueDelimiters_StillScrubsEntireUri(string input)
    {
        var redacted = DiagnosticsRedactor.RedactLogText(input);
        Assert.DoesNotContain("alpha", redacted);
        Assert.DoesNotContain("beta", redacted);
        Assert.Contains("status=ok", redacted);
    }

    [Theory]
    [InlineData("header_protection_key")]
    [InlineData("header-protection-key")]
    [InlineData("headerprotectionkey")]
    [InlineData("HeaderProtectionKey")]
    [InlineData("hpk")]
    [InlineData("HPK")]
    public void RedactLogText_HpkAliases_MasksEvenShortMalformedValues(string key)
    {
        Assert.Equal($"{key}=*** status=ok", DiagnosticsRedactor.RedactLogText($"{key}=short! status=ok"));
        Assert.Equal($"\"{key}\":\"***\"", DiagnosticsRedactor.RedactLogText($"\"{key}\":\"short malformed!\""));
    }

    [Fact]
    public void RedactionConsumers_ExportAndCrashReport_MaskQuotedExceptionAndLogSecrets()
    {
        var previous = AppPaths.DataDir;
        var data = Directory.CreateTempSubdirectory("vpnrouter-quoted-data-");
        var output = Directory.CreateTempSubdirectory("vpnrouter-quoted-export-");
        const string log = "password=\"logAlpha logBeta\" hpk='tiny key!' status=ok";
        const string message = "token='exceptionAlpha exceptionBeta' port=443";
        try
        {
            AppPaths.OverrideDataDir(data.FullName);
            AppPaths.EnsureDirectories();
            File.WriteAllText(Path.Combine(AppPaths.LogsDir, "vpnrouter-quoted.log"), log);
            var crashPath = CrashReporter.WriteReport(new InvalidOperationException(message));
            Assert.NotNull(crashPath);
            var crash = File.ReadAllText(crashPath!);
            Assert.Contains("token='***' port=443", crash);
            Assert.Contains("password=\"***\" hpk='***' status=ok", crash);
            AssertNoSyntheticSecrets(crash);

            var export = DiagnosticsExporter.Export(new DateTime(2026, 10, 29), false, output.FullName);
            using var zip = ZipFile.OpenRead(export.ZipPath);
            var logEntry = Assert.Single(zip.Entries.Where(e => e.Name == "vpnrouter-quoted.log"));
            using (var reader = new StreamReader(logEntry.Open()))
                Assert.Equal("password=\"***\" hpk='***' status=ok", reader.ReadToEnd());
            var crashEntry = Assert.Single(zip.Entries.Where(e => e.Name == Path.GetFileName(crashPath)));
            using (var reader = new StreamReader(crashEntry.Open()))
            {
                var exportedCrash = reader.ReadToEnd();
                Assert.Contains("token='***' port=443", exportedCrash);
                AssertNoSyntheticSecrets(exportedCrash);
            }
        }
        finally
        {
            AppPaths.OverrideDataDir(previous);
            data.Delete(recursive: true);
            output.Delete(recursive: true);
        }
    }

    private static void AssertNoSyntheticSecrets(string text)
    {
        foreach (var secret in new[] { "logAlpha", "logBeta", "tiny key!", "exceptionAlpha", "exceptionBeta" })
            Assert.DoesNotContain(secret, text);
    }
}
