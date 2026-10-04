using VPNRouter.Core.Services.Diagnostics;
using Xunit;

namespace VPNRouter.Tests;

public sealed class DiagnosticsRedactorTests
{
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
        Assert.Contains("1.2.3.4", outp);
        Assert.Contains("www.microsoft.com", outp);
        Assert.Contains("443", outp);
        Assert.Contains("subscribe", outp);
        Assert.Contains(PublicKey, outp);
        Assert.Contains("ninitux.com", outp);
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
        Assert.Contains("include", outp);
        Assert.Contains("Discord.exe", outp);
        Assert.Contains("chrome.exe", outp);
        Assert.DoesNotContain(Uuid, outp);
    }

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
        Assert.Contains("xtls-rprx-vision", outp);
        Assert.Contains("Discord.exe", outp);
        Assert.Contains(PublicKey, outp);
        Assert.Contains("\"final\"", outp);
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
    public void Json_NumericSecretValues_AreRedacted_NotPassedThroughAsNumbers()
    {
        var outp = DiagnosticsRedactor.RedactSingboxJson(
            @"{ ""short_id"": ""01234567"", ""password"": ""86753099"", ""server_port"": 443 }");
        Assert.DoesNotContain("01234567", outp);
        Assert.DoesNotContain("86753099", outp);
        Assert.Contains("443", outp);
        Assert.Contains(DiagnosticsRedactor.Redacted, outp);
    }

    [Fact]
    public void Json_NonAllowlistedKeyWithNumericString_IsRedacted()
    {
        var outp = DiagnosticsRedactor.RedactSingboxJson(
            @"{ ""auth_token"": ""12345678"", ""pin"": ""9999"", ""server_port"": 443 }");
        Assert.DoesNotContain("12345678", outp);
        Assert.DoesNotContain("9999", outp);
        Assert.Contains("443", outp);
        Assert.Contains(DiagnosticsRedactor.Redacted, outp);
    }

    [Fact]
    public void Json_UrlKey_KeepsHostDropsToken()
    {
        var outp = DiagnosticsRedactor.RedactSingboxJson(
            $@"{{ ""url"": ""https://ninitux.com/api/v1/app/config/{SubToken}"" }}");
        Assert.Contains("ninitux.com", outp);
        Assert.DoesNotContain(SubToken, outp);
    }

    [Fact]
    public void Json_ParseFailure_OmitsRatherThanLeaks()
    {
        var outp = DiagnosticsRedactor.RedactSingboxJson("{ broken json " + SubToken);
        Assert.DoesNotContain(SubToken, outp);
        Assert.Equal(DiagnosticsRedactor.OmittedOnParseFailure, outp);
    }

    [Fact]
    public void Logs_ScrubProxyUrisAndUuids()
    {
        var outp = DiagnosticsRedactor.RedactLogText(
            $"connecting vless://{Uuid}@1.2.3.4:443?flow=xtls error\nnext line {Uuid}");
        Assert.DoesNotContain(Uuid, outp);
        Assert.Contains("vless://[redacted]", outp);
    }

    [Fact]
    public void Logs_RedactKeyValueSecrets_TheShapeScrubberMisses()
    {
        var outp = DiagnosticsRedactor.RedactLogText(
            "[DBG] auth password=hunter2 ok\n[DBG] reality short_id: abcd1234 set\n" +
            "[DBG] reality sid=78ca7952 set\n[INF] token=ZsecretTok99");
        Assert.DoesNotContain("hunter2", outp);
        Assert.DoesNotContain("abcd1234", outp);
        Assert.DoesNotContain("78ca7952", outp);
        Assert.DoesNotContain("ZsecretTok99", outp);
        Assert.Contains("password=", outp);
        Assert.Contains(DiagnosticsRedactor.Redacted, outp);
    }

    [Fact]
    public void Logs_RedactJsonStyleKeyValueSecrets()
    {
        var outp = DiagnosticsRedactor.RedactLogText(
            "sing-box: {\"short_id\":\"0123456789abcdef\",\"token\":\"deadbeef00112233445566778899aabbcc\"}");

        Assert.DoesNotContain("0123456789abcdef", outp);
        Assert.DoesNotContain("deadbeef00112233445566778899aabbcc", outp);
        Assert.Contains(DiagnosticsRedactor.Redacted, outp);
    }

    [Fact]
    public void Yaml_NumericObfsPassword_IsRedacted()
    {
        var outp = DiagnosticsRedactor.RedactConfigYaml(
            "vless:\n  servers:\n  - server: 1.2.3.4\n    obfs_password: 86753099\n    plugin_opts: 12345678\n");
        Assert.DoesNotContain("86753099", outp);
        Assert.DoesNotContain("12345678", outp);
        Assert.Contains("1.2.3.4", outp);
    }

    [Fact]
    public void Url_WithUserInfo_DropsCredentials()
    {
        var outp = DiagnosticsRedactor.RedactSingboxJson(
            @"{ ""url"": ""https://user:p4ssw0rd@ninitux.com/api/v1/config/abc"" }");
        Assert.DoesNotContain("p4ssw0rd", outp);
        Assert.DoesNotContain("user:", outp);
        Assert.Contains("ninitux.com", outp);
    }

    [Fact]
    public void Logs_RedactAuthorizationHeaderTokens()
    {
        var outp = DiagnosticsRedactor.RedactLogText(
            "[DBG] Authorization: Bearer mySecretValue123 sent\n" +
            "[DBG] proxy-authorization: Basic dXNlcjpwYXNzd29yZA== ok");
        Assert.DoesNotContain("mySecretValue123", outp);
        Assert.DoesNotContain("dXNlcjpwYXNzd29yZA==", outp);
    }

    [Fact]
    public void Logs_RedactPrefixedSecretKeys()
    {
        var outp = DiagnosticsRedactor.RedactLogText(
            "[DBG] access_token=mySecretAccess123\n" +
            "[DBG] refresh_token: mySecretRefresh456\n" +
            "[DBG] client_secret=mySecretClient789\n" +
            "[DBG] auth_token=mySecretAuthABC\n" +
            "[DBG] secret_key=mySecretKeyDEF\n" +
            "[DBG] tg_proxy_secret=myTelegramProxySecret987\n" +
            "[DBG] access_key=mySecretAccessKey123\n" +
            "[DBG] enc_key: mySecretEncKey456\n" +
            "[DBG] encryption_key=mySecretEncryptionKey789\n" +
            "[DBG] session_key=mySecretSessionKeyABC\n" +
            "[DBG] auth_key=mySecretAuthKeyPQR\n" +
            "[DBG] client_key=mySecretClientKeySTU\n" +
            "[DBG] user_key: mySecretUserKeyDEF");
        Assert.DoesNotContain("mySecretAccess123", outp);
        Assert.DoesNotContain("mySecretRefresh456", outp);
        Assert.DoesNotContain("mySecretClient789", outp);
        Assert.DoesNotContain("mySecretAuthABC", outp);
        Assert.DoesNotContain("mySecretKeyDEF", outp);
        Assert.DoesNotContain("myTelegramProxySecret987", outp);
        Assert.DoesNotContain("mySecretAccessKey123", outp);
        Assert.DoesNotContain("mySecretEncKey456", outp);
        Assert.DoesNotContain("mySecretEncryptionKey789", outp);
        Assert.DoesNotContain("mySecretSessionKeyABC", outp);
        Assert.DoesNotContain("mySecretAuthKeyPQR", outp);
        Assert.DoesNotContain("mySecretClientKeySTU", outp);
        Assert.DoesNotContain("mySecretUserKeyDEF", outp);
        Assert.Contains("access_token=", outp);
        Assert.Contains("refresh_token:", outp);
        Assert.Contains("access_key=", outp);
        Assert.Contains("enc_key:", outp);
        Assert.Contains("encryption_key=", outp);
    }

    [Fact]
    public void Logs_RedactSeparatorlessSecretKeys()
    {
        var outp = DiagnosticsRedactor.RedactLogText(
            "[DBG] clientsecret=mySecretClientNoSep123\n" +
            "[DBG] clientpassword: mySecretClientPass456\n" +
            "[DBG] clientpass=mySecretPass789\n" +
            "[DBG] refreshtoken=mySecretRefreshNoSepABC\n" +
            "[DBG] accesstoken: mySecretAccessNoSepDEF\n" +
            "[DBG] idtoken=mySecretIdTokenGHI\n" +
            "[DBG] id_token: mySecretIdTokenJKL\n" +
            "[DBG] appsecret=mySecretAppSecretMNO\n" +
            "[DBG] app_secret: mySecretAppSecretPQR\n" +
            "[DBG] usersecret=mySecretUserSecretSTU\n" +
            "[DBG] userpassword: mySecretUserPassVWX\n" +
            "[DBG] authsecret=mySecretAuthSecretYZ1\n" +
            "[DBG] authtoken: mySecretAuthToken234\n" +
            "[DBG] bypass=true\n" +
            "[DBG] bypass_russian_traffic=true");
        Assert.DoesNotContain("mySecretClientNoSep123", outp);
        Assert.DoesNotContain("mySecretClientPass456", outp);
        Assert.DoesNotContain("mySecretPass789", outp);
        Assert.DoesNotContain("mySecretRefreshNoSepABC", outp);
        Assert.DoesNotContain("mySecretAccessNoSepDEF", outp);
        Assert.DoesNotContain("mySecretIdTokenGHI", outp);
        Assert.DoesNotContain("mySecretIdTokenJKL", outp);
        Assert.DoesNotContain("mySecretAppSecretMNO", outp);
        Assert.DoesNotContain("mySecretAppSecretPQR", outp);
        Assert.DoesNotContain("mySecretUserSecretSTU", outp);
        Assert.DoesNotContain("mySecretUserPassVWX", outp);
        Assert.DoesNotContain("mySecretAuthSecretYZ1", outp);
        Assert.DoesNotContain("mySecretAuthToken234", outp);
        Assert.Contains("clientsecret=", outp);
        Assert.Contains("refreshtoken=", outp);
        Assert.Contains("accesstoken:", outp);
        Assert.Contains("idtoken=", outp);
        Assert.Contains("id_token:", outp);
        Assert.Contains("appsecret=", outp);
        Assert.Contains("app_secret:", outp);
        Assert.Contains("usersecret=", outp);
        Assert.Contains("userpassword:", outp);
        Assert.Contains("authsecret=", outp);
        Assert.Contains("authtoken:", outp);
        Assert.Contains("bypass=true", outp);
        Assert.Contains("bypass_russian_traffic=true", outp);
    }

    [Fact]
    public void RedactConfigYaml_RedactsAwgSecrets_KeepsHostPort()
    {
        var yaml =
            "vless:\n  servers:\n    - name: AWG\n      protocol: amneziawg\n" +
            "      server: 1.2.3.4\n      port: 51820\n      awg:\n" +
            "        private_key: SECRETAWGPRIV\n        preshared_key: SECRETAWGPSK\n";
        var outp = DiagnosticsRedactor.RedactConfigYaml(yaml);
        Assert.DoesNotContain("SECRETAWGPRIV", outp);
        Assert.DoesNotContain("SECRETAWGPSK", outp);
        Assert.Contains("1.2.3.4", outp);
        Assert.Contains("51820", outp);
    }

    [Fact]
    public void RedactSingboxJson_RedactsWireguardPrivateKey_KeepsPeerPublicKey()
    {
        var json = "{\"endpoints\":[{\"type\":\"wireguard\",\"tag\":\"proxy\"," +
                   "\"private_key\":\"SECRETAWGPRIV\"," +
                   "\"peers\":[{\"address\":\"1.2.3.4\",\"port\":51820,\"public_key\":\"PUBOK\"}]}]}";
        var outp = DiagnosticsRedactor.RedactSingboxJson(json);
        Assert.DoesNotContain("SECRETAWGPRIV", outp);
        Assert.Contains("1.2.3.4", outp);
    }

    [Fact]
    public void Yaml_MalformedYaml_RedactsSecretsAndPreservesStructure()
    {
        var malformedYaml =
            "app:\n" +
            "  routing_mode: split\n" +
            "  subscription_url: https://ninitux.com/api/v1/app/config/" + SubToken + "\n" +
            "  uuid: " + Uuid + "\n" +
            "  password: " + Password + "\n" +
            "  bad_line: [unclosed quote \"foo\n" +
            "  unknown_secret_key: " + PrivKey + "\n";

        var outp = DiagnosticsRedactor.RedactConfigYaml(malformedYaml);

        Assert.DoesNotContain(SubToken, outp);
        Assert.DoesNotContain(Uuid, outp);
        Assert.DoesNotContain(Password, outp);
        Assert.DoesNotContain(PrivKey, outp);

        Assert.Contains("routing_mode: split", outp);
        Assert.Contains("ninitux.com", outp);
        Assert.Contains("bad_line: ***", outp);
        Assert.Contains("unknown_secret_key: ***", outp);
    }

    [Fact]
    public void Logs_RedactQuotedSecretWithSpaces()
    {
        var outp = DiagnosticsRedactor.RedactLogText(
            "password=\"alpha beta\"\n" +
            "token='x y'\n" +
            "userpassword=\"alpha beta\"");
        Assert.DoesNotContain("alpha", outp);
        Assert.DoesNotContain("beta", outp);
        Assert.DoesNotContain("x y", outp);
        Assert.Contains("password=\"***", outp);
        Assert.Contains("token='***", outp);
        Assert.Contains("userpassword=\"***", outp);
    }
}
