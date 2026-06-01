using VPNRouter.Core.Services.Diagnostics;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Cardinal safety tests for the diagnostics redactor. A miss here is a
/// credential leak, so these prove that known secrets never survive redaction
/// while genuinely-diagnostic values are preserved.
/// </summary>
public sealed class DiagnosticsRedactorTests
{
    // Distinctive planted secrets — easy to assert absence of.
    private const string Uuid = "2d54442d-158f-49e2-b225-67ba1a5b77f4";
    private const string Password = "superSecretPass123";
    private const string ShortId = "0123abcd";
    private const string SubToken = "SECRETTOKEN123456";
    private const string PrivKey = "aPrivateKeyValueXYZ";
    private const string PublicKey = "Vl1n5kEXAMPLEpublicKeyBase64DataAbcdefghij0123456789";

    private const string Yaml = $@"
app:
  config_mode: subscribe
  subscriptions:
    - name: main
      url: https://ninitux.com/api/v1/app/config/{SubToken}
      enabled: true
vless:
  servers:
    - name: srv1
      server: 1.2.3.4
      port: 443
      uuid: {Uuid}
      password: {Password}
      server_name: www.microsoft.com
      reality:
        public_key: {PublicKey}
        short_id: {ShortId}
        private_key: {PrivKey}
";

    private const string Json = $@"{{
  ""log"": {{ ""level"": ""info"" }},
  ""outbounds"": [
    {{ ""type"": ""vless"", ""tag"": ""proxy"", ""server"": ""1.2.3.4"", ""server_port"": 443,
       ""uuid"": ""{Uuid}"", ""flow"": ""xtls-rprx-vision"",
       ""tls"": {{ ""server_name"": ""www.microsoft.com"", ""reality"": {{
           ""public_key"": ""{PublicKey}"", ""short_id"": ""{ShortId}"" }} }} }},
    {{ ""type"": ""direct"", ""tag"": ""direct"" }}
  ],
  ""route"": {{ ""rules"": [ {{ ""process_name"": ""Discord.exe"", ""outbound"": ""proxy"" }} ], ""final"": ""direct"" }}
}}";

    // ── YAML ──

    [Fact]
    public void Yaml_RedactsAllKnownSecrets()
    {
        var outp = DiagnosticsRedactor.RedactConfigYaml(Yaml);
        Assert.DoesNotContain(Uuid, outp);
        Assert.DoesNotContain(Password, outp);
        Assert.DoesNotContain(ShortId, outp);
        Assert.DoesNotContain(SubToken, outp);
        Assert.DoesNotContain(PrivKey, outp);
    }

    [Fact]
    public void Yaml_KeepsDiagnosticValues()
    {
        var outp = DiagnosticsRedactor.RedactConfigYaml(Yaml);
        Assert.Contains("1.2.3.4", outp);               // server host
        Assert.Contains("www.microsoft.com", outp);     // server_name
        Assert.Contains("443", outp);                   // port
        Assert.Contains("subscribe", outp);             // config_mode
        Assert.Contains(PublicKey, outp);               // reality public_key is public-by-design
        Assert.Contains("ninitux.com", outp);           // url host kept (token dropped)
    }

    [Fact]
    public void Yaml_KeepsRoutingAppList_TheCoreSplitTunnelDiagnostic()
    {
        const string yaml = @"
app:
  routing_apps_mode: include
  routing_apps_include:
    - Discord.exe
    - chrome.exe
  routing_mode: split
  uuid: " + Uuid;
        var outp = DiagnosticsRedactor.RedactConfigYaml(yaml);
        Assert.Contains("include", outp);          // mode kept
        Assert.Contains("Discord.exe", outp);      // app list kept (diagnostic, non-secret)
        Assert.Contains("chrome.exe", outp);
        Assert.DoesNotContain(Uuid, outp);         // sibling secret still redacted
    }

    // ── JSON ──

    [Fact]
    public void Json_RedactsAllKnownSecrets()
    {
        var outp = DiagnosticsRedactor.RedactSingboxJson(Json);
        Assert.DoesNotContain(Uuid, outp);
        Assert.DoesNotContain(ShortId, outp);
    }

    [Fact]
    public void Json_KeepsRoutingAndTlsDiagnostics()
    {
        var outp = DiagnosticsRedactor.RedactSingboxJson(Json);
        Assert.Contains("1.2.3.4", outp);
        Assert.Contains("www.microsoft.com", outp);
        Assert.Contains("xtls-rprx-vision", outp);      // flow
        Assert.Contains("Discord.exe", outp);           // process_name (routing diagnostic)
        Assert.Contains(PublicKey, outp);               // public_key kept
        Assert.Contains("\"final\"", outp);             // route structure kept
    }

    [Fact]
    public void Json_UnknownKeyWithSecretValue_IsRedacted()
    {
        var outp = DiagnosticsRedactor.RedactSingboxJson(
            @"{ ""weird_new_secret_field"": ""leakMe123SecretValue"" }");
        Assert.DoesNotContain("leakMe123SecretValue", outp);
        Assert.Contains(DiagnosticsRedactor.Redacted, outp);
    }

    [Fact]
    public void Json_NumbersAndBools_AreKeptRegardlessOfKey()
    {
        var outp = DiagnosticsRedactor.RedactSingboxJson(
            @"{ ""some_unknown_count"": 42, ""some_unknown_flag"": true }");
        Assert.Contains("42", outp);
        Assert.Contains("true", outp);
    }

    [Fact]
    public void Json_UrlKey_KeepsHostDropsToken()
    {
        var outp = DiagnosticsRedactor.RedactSingboxJson(
            $@"{{ ""url"": ""https://ninitux.com/api/v1/app/config/{SubToken}"" }}");
        Assert.Contains("ninitux.com", outp);
        Assert.DoesNotContain(SubToken, outp);
    }

    // ── fail-closed ──

    [Fact]
    public void Json_ParseFailure_OmitsRatherThanLeaks()
    {
        // Invalid JSON containing a secret-looking token must NOT pass through raw.
        var outp = DiagnosticsRedactor.RedactSingboxJson("{ broken json " + SubToken);
        Assert.DoesNotContain(SubToken, outp);
        Assert.Equal(DiagnosticsRedactor.OmittedOnParseFailure, outp);
    }

    // ── logs ──

    [Fact]
    public void Logs_ScrubProxyUrisAndUuids()
    {
        var outp = DiagnosticsRedactor.RedactLogText(
            $"connecting vless://{Uuid}@1.2.3.4:443?flow=xtls error\nnext line {Uuid}");
        Assert.DoesNotContain(Uuid, outp);
        Assert.Contains("vless://[redacted]", outp);
    }
}
