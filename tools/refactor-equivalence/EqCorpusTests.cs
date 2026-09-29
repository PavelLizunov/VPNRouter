#nullable enable

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public class EqCorpusTests
{
    private static T Pick<T>(Random r, params T[] xs) => xs[r.Next(xs.Length)];

    [Fact]
    public void StripCorpus()
    {
        var prev = AppPaths.DataDir;
        AppPaths.OverrideDataDir(Path.Combine(Path.GetTempPath(), "eqcorpus-data-strip"));
        try
        {
            var m = typeof(CustomConfigInjector).GetMethod("StripUnsupportedFeatures",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            var r = new Random(12345);
            for (int i = 0; i < 20000; i++)
            {
                var cfg = MakeConfig(r);
                var excl = Pick<List<string>?>(r, null, new List<string>(), new List<string> { "10.0.0.0/8" },
                    new List<string> { "10.0.0.0/8", "192.168.0.0/16", "fd00::/8" });
                bool f4 = r.Next(4) == 0, strict = r.Next(3) == 0, v6 = r.Next(2) == 0;
                string result;
                try
                {
                    m.Invoke(null, new object?[] { cfg, excl, f4, strict, v6 });
                    result = "OK " + cfg.ToJsonString();
                }
                catch (TargetInvocationException ex)
                {
                    result = "EX " + ex.InnerException!.GetType().Name + " " + ex.InnerException.Message;
                }
                EqRec.Write($"S {i} {EqRec.Hash(result)}");
            }
        }
        finally { AppPaths.OverrideDataDir(prev); }
    }

    private static JsonObject MakeConfig(Random r)
    {
        var root = new JsonObject();
        if (r.Next(10) > 0) root["dns"] = MakeDns(r);
        if (r.Next(10) > 1) root["outbounds"] = MakeOutbounds(r);
        if (r.Next(5) == 0)
            root["endpoints"] = new JsonArray { new JsonObject { ["type"] = "wireguard", ["tag"] = Pick(r, "wg", "dns-direct", "proxy") } };
        if (r.Next(10) > 1) root["route"] = MakeRoute(r);
        if (r.Next(10) > 2) root["inbounds"] = MakeInbounds(r);
        switch (r.Next(4))
        {
            case 1: root["log"] = new JsonObject { ["level"] = "info" }; break;
            case 2: root["log"] = new JsonObject { ["output"] = "x.log", ["disabled"] = true }; break;
            case 3: root["log"] = "text"; break;
        }
        return root;
    }

    private static string? Detour(Random r) => Pick<string?>(r, null, "", "proxy", "direct", "dns-direct", "wg", "missing", "block", "sel");

    private static JsonNode? FakeIpGlobal(Random r)
    {
        switch (r.Next(9))
        {
            case 0: return null;
            case 1: return JsonValue.Create("bad");
            default:
                var o = new JsonObject();
                var en = r.Next(6);
                if (en <= 2) o["enabled"] = true;
                else if (en == 3) o["enabled"] = false;
                else if (en == 4) o["enabled"] = "yes";
                var v4 = r.Next(7);
                if (v4 == 0) o["inet4_range"] = "198.18.0.0/15";
                else if (v4 == 1) o["inet4_range"] = "198.19.0.0/16";
                else if (v4 == 2) o["inet4_range"] = "bad";
                else if (v4 == 3) o["inet4_range"] = "10.0.0.0/33";
                else if (v4 == 4) o["inet4_range"] = "fc00::/18";
                else if (v4 == 5) o["inet4_range"] = 123;
                var v6 = r.Next(6);
                if (v6 == 0) o["inet6_range"] = "fc00::/18";
                else if (v6 == 1) o["inet6_range"] = "bad";
                else if (v6 == 2) o["inet6_range"] = "198.18.0.0/15";
                else if (v6 == 3) o["inet6_range"] = "fd00::/8";
                return o;
        }
    }

    private static JsonNode? DnsServer(Random r, int idx)
    {
        var tag = "t" + idx;
        switch (r.Next(14))
        {
            case 0: return JsonValue.Create("str");
            case 1: return null;
            case 2:
            case 3:
            {
                var o = new JsonObject { ["tag"] = tag, ["address"] = "fakeip" };
                if (r.Next(4) == 0) o["strategy"] = "ipv4_only";
                if (r.Next(6) == 0) o["address_resolver"] = "local";
                if (r.Next(6) == 0) o["detour"] = "direct";
                if (r.Next(8) == 0) o["type"] = Pick(r, "legacy", "");
                return o;
            }
            case 4:
            case 5:
            {
                var o = new JsonObject { ["type"] = "fakeip", ["tag"] = tag };
                if (r.Next(2) == 0) o["inet4_range"] = Pick(r, "198.18.0.0/15", "198.19.0.0/16", "bad", "10.0.0.0/33");
                if (r.Next(2) == 0) o["inet6_range"] = Pick(r, "fc00::/18", "bad", "fd00::/8");
                return o;
            }
            case 6:
            case 7:
            {
                var o = new JsonObject { ["tag"] = tag, ["type"] = Pick(r, "udp", "https", "tls", "local", "dhcp", "quic", "h3") };
                if (r.Next(2) == 0) o["server"] = Pick(r, "1.1.1.1", "8.8.8.8", "dns.google");
                if (r.Next(2) == 0) o["path"] = "/x";
                var d = Detour(r);
                if (d != null) o["detour"] = d;
                return o;
            }
            default:
            {
                var o = new JsonObject
                {
                    ["tag"] = tag,
                    ["address"] = Pick(r, "local", "dhcp://auto", "tls://1.1.1.1", "https://dns.google/dns-query", "https://1.1.1.1:8443/x",
                        "https://dns.google", "udp://8.8.8.8", "tcp://9.9.9.9:5353", "1.1.1.1", "not a uri://", "quic://x.y", "h3://z",
                        "tls://1.1.1.1:853", "rcode://success", "://x"),
                };
                var d = Detour(r);
                if (d != null) o["detour"] = d;
                if (r.Next(5) == 0) o["address_resolver"] = Pick(r, "local", "t0", "t1");
                if (r.Next(8) == 0) o["strategy"] = "prefer_ipv4";
                return o;
            }
        }
    }

    private static JsonObject MakeDns(Random r)
    {
        var dns = new JsonObject();
        if (r.Next(10) > 0)
        {
            var servers = new JsonArray();
            int n = r.Next(6);
            for (int i = 0; i < n; i++) servers.Add(DnsServer(r, i));
            dns["servers"] = servers;
        }
        if (r.Next(3) == 0) dns["fakeip"] = FakeIpGlobal(r);
        if (r.Next(2) == 0)
        {
            var rules = new JsonArray();
            int n = r.Next(5);
            for (int i = 0; i < n; i++)
            {
                switch (r.Next(6))
                {
                    case 0: rules.Add(new JsonObject { ["geosite"] = "ru", ["server"] = "t0" }); break;
                    case 1: rules.Add(new JsonObject { ["geoip"] = "ru", ["server"] = "t1" }); break;
                    case 2: rules.Add(new JsonObject { ["outbound"] = "any", ["server"] = "t0" }); break;
                    case 3: rules.Add(new JsonObject { ["domain_suffix"] = new JsonArray("a.com"), ["server"] = "t1" }); break;
                    case 4: rules.Add("str"); break;
                    default: rules.Add(new JsonObject { ["query_type"] = new JsonArray("A"), ["action"] = "reject" }); break;
                }
            }
            dns["rules"] = rules;
        }
        if (r.Next(2) == 0) dns["final"] = Pick(r, "t0", "t1", "t2", "remote", "local", "missing");
        if (r.Next(3) == 0) dns["strategy"] = Pick(r, "ipv4_only", "prefer_ipv4", "prefer_ipv6");
        return dns;
    }

    private static JsonArray MakeOutbounds(Random r)
    {
        var a = new JsonArray();
        int n = r.Next(7);
        for (int i = 0; i < n; i++)
        {
            switch (r.Next(9))
            {
                case 0: a.Add(new JsonObject { ["type"] = "block", ["tag"] = Pick(r, "block", "blk") }); break;
                case 1: a.Add(new JsonObject { ["type"] = "dns", ["tag"] = Pick(r, "dns-out", "dns") }); break;
                case 2: a.Add(new JsonObject { ["type"] = "direct", ["tag"] = Pick(r, "direct", "dns-direct", "d2") }); break;
                case 3: a.Add(new JsonObject { ["type"] = "selector", ["tag"] = Pick(r, "proxy", "sel"), ["outbounds"] = new JsonArray("a", "b") }); break;
                case 4: a.Add(new JsonObject { ["type"] = "vless", ["tag"] = Pick(r, "proxy", "a", "b"), ["server"] = "1.2.3.4" }); break;
                case 5: a.Add(new JsonObject { ["type"] = "wireguard", ["tag"] = Pick(r, "wg", "proxy") }); break;
                case 6: a.Add(new JsonObject { ["type"] = "urltest", ["tag"] = "auto" }); break;
                case 7: a.Add("str"); break;
                default: a.Add(new JsonObject { ["type"] = "socks", ["tag"] = "s" }); break;
            }
        }
        return a;
    }

    private static JsonObject MakeRoute(Random r)
    {
        var route = new JsonObject();
        if (r.Next(10) > 0)
        {
            var rules = new JsonArray();
            int n = r.Next(7);
            for (int i = 0; i < n; i++)
            {
                switch (r.Next(11))
                {
                    case 0: rules.Add(new JsonObject { ["geosite"] = "ru", ["outbound"] = "direct" }); break;
                    case 1: rules.Add(new JsonObject { ["geoip"] = "ru", ["outbound"] = "direct" }); break;
                    case 2: rules.Add(new JsonObject { ["protocol"] = "dns", ["outbound"] = Pick(r, "dns-out", "dns", "block") }); break;
                    case 3: rules.Add(new JsonObject { ["outbound"] = Pick(r, "block", "blk", "dns-out") }); break;
                    case 4: rules.Add(new JsonObject { ["action"] = "sniff" }); break;
                    case 5: rules.Add(new JsonObject { ["ip_is_private"] = true, ["outbound"] = "direct" }); break;
                    case 6: rules.Add(new JsonObject { ["clash_mode"] = "direct", ["outbound"] = "direct" }); break;
                    case 7: rules.Add("str"); break;
                    case 8: rules.Add(new JsonObject { ["process_name"] = new JsonArray("x.exe"), ["outbound"] = "proxy" }); break;
                    case 9: rules.Add(new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" }); break;
                    default: rules.Add(new JsonObject { ["domain"] = new JsonArray("a.com"), ["outbound"] = Pick(r, "proxy", "direct") }); break;
                }
            }
            route["rules"] = rules;
        }
        if (r.Next(2) == 0) route["final"] = Pick(r, "proxy", "direct");
        return route;
    }

    private static JsonArray MakeInbounds(Random r)
    {
        var a = new JsonArray();
        int n = r.Next(4);
        for (int i = 0; i < n; i++)
        {
            switch (r.Next(6))
            {
                case 0: a.Add("str"); break;
                case 1: a.Add(new JsonObject { ["type"] = "mixed", ["tag"] = "m", ["listen_port"] = 2080, ["sniff"] = true, ["sniff_timeout"] = "200ms" }); break;
                default:
                {
                    var o = new JsonObject { ["type"] = "tun", ["tag"] = "tun-in" };
                    if (r.Next(2) == 0) o["inet4_address"] = "172.19.0.1/30";
                    if (r.Next(2) == 0) o["inet6_address"] = "fdfe:dcba:9876::1/126";
                    if (r.Next(4) == 0) o["address"] = new JsonArray("172.18.0.1/30");
                    if (r.Next(2) == 0) o["sniff"] = r.Next(2) == 0;
                    if (r.Next(4) == 0) o["sniff_override_destination"] = true;
                    if (r.Next(4) == 0) o["domain_strategy"] = "ipv4_only";
                    if (r.Next(3) == 0) o["stack"] = Pick(r, "gvisor", "mixed");
                    if (r.Next(3) == 0) o["strict_route"] = true;
                    if (r.Next(3) == 0) o["route_exclude_address"] = new JsonArray(Pick(r, "10.0.0.0/8", "1.2.3.4/32"), "192.168.0.0/16");
                    a.Add(o);
                    break;
                }
            }
        }
        return a;
    }

    [Fact]
    public void OutboundsCorpus()
    {
        var prev = AppPaths.DataDir;
        var dataDir = Path.Combine(Path.GetTempPath(), "eqcorpus-data-out");
        try { Directory.Delete(dataDir, true); } catch { }
        AppPaths.OverrideDataDir(dataDir);
        var prevNaive = ServerUriParser.NaiveRuntimeAvailable;
        try
        {
            var bo = typeof(ConfigGenerator).GetMethod("BuildOutbounds", BindingFlags.NonPublic | BindingFlags.Static)!;
            var opts = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
            var r = new Random(777);
            var verdicts = new[]
            {
                ServerHealthVerdict.Healthy, ServerHealthVerdict.HostUnreachable, ServerHealthVerdict.ProtocolHandshakeBlockedLikely,
                ServerHealthVerdict.ProtocolHandshakeBlockedLikely, ServerHealthVerdict.TcpOpenProtocolUntested, ServerHealthVerdict.OnlyControlWorks,
            };
            for (int i = 0; i < 6000; i++)
            {
                ServerHealthStore.ResetForTests();
                ServerUriParser.NaiveRuntimeAvailable = r.Next(4) != 0;
                SingBoxFeatures.OverrideAwg = r.Next(4) != 0;
                SingBoxFeatures.OverrideXhttp = r.Next(4) != 0;
                var servers = MakeServers(r);
                var settings = new AppSettings
                {
                    App = new AppConfig { LogLevel = "info", ConfigMode = "generated", RoutingMode = "full" },
                    Tun = new TunSettings(),
                    Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
                    SingBox = new SingBoxSettings(),
                    Vless = new VlessConfig
                    {
                        Servers = servers,
                        ActiveServer = r.Next(5) switch
                        {
                            0 => "",
                            1 => "missing",
                            _ => servers[r.Next(servers.Count)].Name,
                        },
                        AutoSelectBestServer = r.Next(2) == 0,
                    },
                };
                foreach (var s in servers)
                    if (r.Next(3) == 0)
                        ServerHealthStore.Record(s, verdicts[r.Next(verdicts.Length)], providerKey: Pick<string?>(r, null, "asnA", "asnB", "asnC"));
                Func<VlessServerEntry, bool>? alive = r.Next(3) == 0 ? null : (r.Next(2) == 0 ? (_ => true) : (s => s.Name.Length % 2 == 0));
                var args = new object?[] { settings, false, false, null, null, false, alive };
                string result;
                try
                {
                    var ret = bo.Invoke(null, args);
                    result = "OK " + JsonSerializer.Serialize(ret, opts) + "|" + args[1] + "|" + args[2] + "|"
                        + JsonSerializer.Serialize(args[3], opts) + "|" + JsonSerializer.Serialize(args[4], opts) + "|" + args[5];
                }
                catch (TargetInvocationException ex)
                {
                    result = "EX " + ex.InnerException!.GetType().Name + " " + ex.InnerException.Message;
                }
                EqRec.Write($"O {i} {EqRec.Hash(result)}");
            }
        }
        finally
        {
            ServerUriParser.NaiveRuntimeAvailable = prevNaive;
            SingBoxFeatures.OverrideAwg = null;
            SingBoxFeatures.OverrideXhttp = null;
            ServerHealthStore.ResetForTests();
            AppPaths.OverrideDataDir(prev);
        }
    }

    private static List<VlessServerEntry> MakeServers(Random r)
    {
        var list = new List<VlessServerEntry>();
        int n = 1 + r.Next(5);
        bool chain = r.Next(8) == 0;
        for (int i = 0; i < n; i++)
        {
            var proto = Pick(r, "vless", "vless", "vless", "hysteria2", "tuic", "shadowsocks", "naive", "naive", "amneziawg", "awg", "dns-tunnel");
            var s = new VlessServerEntry
            {
                Name = Pick(r, "a", "b", "c", "d", "e", "dup", "dup", "n" + i),
                Server = $"10.0.{i}.{r.Next(4)}",
                Port = 443 + r.Next(3),
                Protocol = proto,
                Uuid = "11111111-2222-3333-4444-55555555555" + r.Next(10),
                Flow = Pick(r, "", "xtls-rprx-vision"),
                Security = "reality",
                Reality = new VlessRealityConfig { PublicKey = "pk" + r.Next(3), ShortId = "ab" },
                Password = "pw",
                Username = "u",
                Method = "aes-128-gcm",
            };
            if (r.Next(5) == 0) s.Transport = new VlessTransportConfig { Type = Pick(r, "xhttp", "ws", "grpc", "tcp") };
            if (proto is "amneziawg" or "awg")
                s.Awg = new AwgConfig { PrivateKey = "priv", PeerPublicKey = "peer", Address = new List<string> { "10.9.0.2/32" } };
            if (proto == "dns-tunnel")
            {
                s.DnsDomain = "t.example.org";
                s.DnsResolvers = new List<string> { "1.1.1.1:53", "8.8.8.8" };
            }
            if (proto is "naive" or "hysteria2" or "tuic" && r.Next(2) == 0) s.PairGroup = "pg1";
            list.Add(s);
        }
        if (chain && n >= 2)
        {
            list[1].OutboundId = "up1";
            list[0].DetourVia = Pick(r, "up1", "up1", "nope");
            if (r.Next(3) == 0 && n >= 3) list[2].DetourVia = "up1";
        }
        return list;
    }

    [Fact]
    public void HealthCorpus()
    {
        var prev = AppPaths.DataDir;
        var root = Path.Combine(Path.GetTempPath(), "eqcorpus-data-health");
        var self = Environment.ProcessId;
        var r = new Random(4242);
        var names = new[] { "Discord_Privacy", "Messengers", "AI_Tools", "Browsers", "Work_Suite", "Streaming", "Gaming", "Privacy_Shell", "Extra1", "Extra2" };
        try
        {
            for (int i = 0; i < 60; i++)
            {
                try { Directory.Delete(root, true); } catch { }
                Directory.CreateDirectory(root);
                AppPaths.OverrideDataDir(root);
                if (r.Next(3) > 0) { Directory.CreateDirectory(AppPaths.ConfigDir); Directory.CreateDirectory(AppPaths.LogsDir); }
                if (r.Next(3) > 0) { Directory.CreateDirectory(AppPaths.CacheDir); Directory.CreateDirectory(AppPaths.BinDir); Directory.CreateDirectory(AppPaths.ProfilesDir); }

                var customFile = Path.Combine(root, "custom.json");
                var cfg = r.Next(9);
                if (cfg == 1) File.WriteAllText(AppPaths.ConfigYamlPath, "garbage: [");
                else if (cfg >= 2 && r.Next(10) < 4 || cfg == 2)
                {
                    var st = new AppSettings();
                    switch (cfg)
                    {
                        case 3:
                            st.App.ConfigMode = "subscribe";
                            st.App.Subscriptions = new List<SubscriptionEntry>
                            {
                                new() { Name = "s1", Url = "https://x", Enabled = true, Servers = new List<VlessServerEntry> { new() { Name = "a", Server = "1.1.1.1" }, new() { Name = "b", Server = "2.2.2.2" } } },
                                new() { Name = "s2", Url = "https://y", Enabled = r.Next(2) == 0, Servers = new List<VlessServerEntry> { new() { Name = "c", Server = "3.3.3.3" } } },
                            };
                            break;
                        case 4: st.App.ConfigMode = "subscribe"; break;
                        case 5: st.App.ConfigMode = "custom"; st.App.CustomConfig = ""; break;
                        case 6: st.App.ConfigMode = "custom"; st.App.CustomConfig = Path.Combine(root, "nope.json"); break;
                        case 7: st.App.ConfigMode = "custom"; st.App.CustomConfig = customFile; File.WriteAllText(customFile, "{}"); break;
                        case 8: st.Vless.Servers = new List<VlessServerEntry> { new() { Name = "m", Server = "9.9.9.9" } }; break;
                    }
                    if (r.Next(5) == 0) st.SchemaVersion = 1;
                    else if (r.Next(6) == 0) st.SchemaVersion = 99;
                    SettingsLoader.Save(st, AppPaths.ConfigYamlPath);
                }

                var cat = r.Next(6);
                Directory.CreateDirectory(AppPaths.ProfilesDir);
                var catPath = Path.Combine(AppPaths.ProfilesDir, "default.json");
                if (cat == 1) File.WriteAllText(catPath, "{");
                else if (cat == 2) File.WriteAllText(catPath, "null");
                else if (cat >= 3)
                {
                    var col = new ProfileCollection { Profiles = names.Take(r.Next(names.Length + 1)).Select(n => new Profile { Name = n }).ToList() };
                    File.WriteAllText(catPath, JsonSerializer.Serialize(col, VPNRouter.Core.Json.AppJsonContext.Default.ProfileCollection));
                }

                if (r.Next(3) > 0)
                {
                    Directory.CreateDirectory(AppPaths.BinDir);
                    File.WriteAllBytes(AppPaths.SingBoxExePath, new byte[r.Next(3) * 1024 * 1024 + r.Next(2000)]);
                }

                var stt = r.Next(7);
                if (stt == 1) File.WriteAllText(AppPaths.StatePath, "{{");
                else if (stt == 2) File.WriteAllText(AppPaths.StatePath, "{\"sing_box_pid\": 0}");
                else if (stt == 3) File.WriteAllText(AppPaths.StatePath, "{\"sing_box_pid\": 999999}");
                else if (stt == 4) File.WriteAllText(AppPaths.StatePath, "{\"SingBoxPid\": " + self + "}");
                else if (stt == 5) File.WriteAllText(AppPaths.StatePath, "{\"sing_box_pid\": \"x\"}");
                else if (stt == 6) File.WriteAllText(AppPaths.StatePath, "{\"SingBoxPid\": 999998}");

                var lk = r.Next(5);
                var lockPath = Path.Combine(AppPaths.DataDir, "running.lock");
                if (lk == 1) File.WriteAllText(lockPath, "999999\n");
                else if (lk == 2) File.WriteAllText(lockPath, self + "\nx");
                else if (lk == 3) File.WriteAllText(lockPath, "abc");
                else if (lk == 4) File.WriteAllText(lockPath, "");

                if (r.Next(3) == 0 && Directory.Exists(AppPaths.LogsDir) && Directory.Exists(AppPaths.ConfigDir))
                {
                    File.WriteAllText(AppPaths.CurrentConfigPath,
                        "{\"outbounds\":[{\"type\":\"vless\",\"tag\":\"proxy\",\"server\":\"104.194.156.93\",\"server_port\":443}]}");
                    var lines = new List<string>();
                    for (int k = 0; k < r.Next(30); k++)
                        lines.Add("+0300 2026-07-08 ERROR connection: open connection to 1.2.3.4:443 using outbound/vless[proxy]: dial tcp "
                            + (r.Next(2) == 0 ? "104.194.156.93:443" : "5.5.5.5:443") + ": i/o timeout");
                    File.WriteAllLines(AppPaths.SingBoxLogPath, lines);
                }

                var report = HealthCheck.FormatReport(HealthCheck.RunAll());
                report = report.Replace(root, "<D>").Replace("PID " + self, "PID <SELF>").Replace("PIDs " + self, "PIDs <SELF>");
                report = string.Join("\n", report.Split('\n').Select(l => l.Contains("8.8.8.8") ? "<MTU>" : l));
                EqRec.Write($"H {i} {EqRec.Hash(report)}");
                EqRec.Full($"H {i} {EqRec.Hash(report)}", report);
            }
        }
        finally
        {
            AppPaths.OverrideDataDir(prev);
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static JsonObject MakeInjectConfig(Random r)
    {
        var cfg = MakeConfig(r);
        // make sure there is usually a real proxy outbound, sometimes a placeholder one
        var outs = cfg["outbounds"] as JsonArray;
        if (outs == null) { outs = new JsonArray(); cfg["outbounds"] = outs; }
        switch (r.Next(8))
        {
            case 0: break;
            case 1:
                outs.Insert(0, new JsonObject { ["type"] = "vless", ["tag"] = "proxy", ["server"] = "example.com", ["server_port"] = 443, ["uuid"] = "u",
                    ["tls"] = new JsonObject { ["enabled"] = true, ["reality"] = new JsonObject { ["enabled"] = true, ["public_key"] = "REPLACE_ME", ["short_id"] = "ab" } } });
                break;
            default:
                outs.Insert(r.Next(outs.Count + 1), new JsonObject { ["type"] = Pick(r, "vless", "vless", "hysteria2", "trojan"), ["tag"] = Pick(r, "proxy", "proxy", "main", "vless-out"), ["server"] = "203.0.113." + r.Next(1, 200), ["server_port"] = 443, ["uuid"] = "11111111-2222-3333-4444-555555555555",
                    ["flow"] = Pick(r, "", "xtls-rprx-vision"),
                    ["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = "www.example.org", ["reality"] = new JsonObject { ["enabled"] = true, ["public_key"] = "gDawCMB0X6iGXZkG8nZIFW5TaaW29x0DMzWijN-gc2A", ["short_id"] = "d86e92a0c6dd2271" } } });
                if (r.Next(3) == 0)
                    outs.Add(new JsonObject { ["type"] = "hysteria2", ["tag"] = "proxy-udp", ["server"] = "203.0.113.7", ["server_port"] = 8443, ["password"] = "x" });
                break;
        }
        if (r.Next(6) == 0)
            outs.Add(new JsonObject { ["type"] = "urltest", ["tag"] = "auto", ["outbounds"] = new JsonArray("proxy") });
        if (r.Next(4) == 0 && cfg["route"] is JsonObject rt && rt["rules"] is JsonArray rr)
            rr.Add(new JsonObject { ["process_name"] = new JsonArray("stale.exe"), ["outbound"] = "proxy" });
        if (r.Next(10) == 0) cfg["experimental"] = new JsonObject { ["clash_api"] = new JsonObject { ["external_controller"] = "127.0.0.1:1234" } };
        if (r.Next(12) == 0) cfg["unknown_fork_key"] = 1;
        return cfg;
    }

    [Fact]
    public void InjectCorpus()
    {
        var prev = AppPaths.DataDir;
        var root = Path.Combine(Path.GetTempPath(), "eqcorpus-data-inject");
        try { Directory.Delete(root, true); } catch { }
        Directory.CreateDirectory(root);
        AppPaths.OverrideDataDir(root);
        try
        {
            var r = new Random(31337);
            var procs = new[] { "chrome.exe", "discord.exe", "game.exe", "My App.exe", "steam.exe", "tg.exe" };
            for (int i = 0; i < 8000; i++)
            {
                Directory.CreateDirectory(AppPaths.GeoDir);
                if (r.Next(2) == 0) { File.WriteAllBytes(AppPaths.GeoIpRuPath, new byte[2048]); File.WriteAllBytes(AppPaths.GeoSiteRuPath, new byte[2048]); }
                else { try { File.Delete(AppPaths.GeoIpRuPath); File.Delete(AppPaths.GeoSiteRuPath); } catch { } }
                var st = new AppSettings();
                st.App.RoutingMode = Pick(r, "split", "split", "full");
                st.App.RoutingAppsMode = Pick(r, "include", "exclude", "include");
                st.App.StrictDns = r.Next(3) == 0;
                st.App.ForceIpv4Only = r.Next(4) == 0;
                st.App.BypassRussianTraffic = r.Next(2) == 0;
                st.Tun.Ipv6Enabled = r.Next(2) == 0;
                st.SingBox.ClashApi = Pick(r, "127.0.0.1:9090", "", "0.0.0.0:9999");
                st.SingBox.ClashApiSecret = Pick(r, "", "s3cret");
                var names = procs.Where(_ => r.Next(3) == 0).ToList();
                if (r.Next(3) == 0) st.App.RoutingAppsInclude = new List<string> { "extra.exe" };
                if (r.Next(3) == 0) st.App.RoutingAppsExclude = new List<string> { "chrome.exe" };
                var cfg = MakeInjectConfig(r);
                string raw = r.Next(60) == 0 ? "[1,2]" : cfg.ToJsonString();
                string result;
                try { result = "OK " + CustomConfigInjector.Inject(raw, names, st); }
                catch (Exception ex) { result = "EX " + ex.GetType().Name + " " + ex.Message; }
                EqRec.Write($"E {i} {EqRec.Hash(result.Replace(root, "<D>"))}");
            }
        }
        finally
        {
            AppPaths.OverrideDataDir(prev);
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static string ValidateSummary(SingBoxConfig cfg, AppSettings st)
    {
        try
        {
            var v = LeakProtection.ValidateConfig(cfg, st);
            return "E:" + string.Join("|", v.Errors) + "##W:" + string.Join("|", v.Warnings);
        }
        catch (Exception ex) { return "EX " + ex.GetType().Name + " " + ex.Message; }
    }

    [Fact]
    public void GenerateCorpus()
    {
        var prev = AppPaths.DataDir;
        var root = Path.Combine(Path.GetTempPath(), "eqcorpus-data-generate");
        try { Directory.Delete(root, true); } catch { }
        Directory.CreateDirectory(root);
        AppPaths.OverrideDataDir(root);
        var prevNaive = ServerUriParser.NaiveRuntimeAvailable;
        try
        {
            var r = new Random(2024);
            var procs = new[] { "chrome.exe", "discord.exe", "game.exe", "My App.exe", "steam.exe", "tg.exe", "svc.exe" };
            for (int i = 0; i < 6000; i++)
            {
                ServerHealthStore.ResetForTests();
                Directory.CreateDirectory(AppPaths.GeoDir);
                if (r.Next(2) == 0) { File.WriteAllBytes(AppPaths.GeoIpRuPath, new byte[2048]); File.WriteAllBytes(AppPaths.GeoSiteRuPath, new byte[2048]); }
                else { try { File.Delete(AppPaths.GeoIpRuPath); File.Delete(AppPaths.GeoSiteRuPath); } catch { } }
                ServerUriParser.NaiveRuntimeAvailable = r.Next(5) != 0;
                SingBoxFeatures.OverrideAwg = r.Next(4) != 0;
                SingBoxFeatures.OverrideXhttp = r.Next(4) != 0;
                var servers = MakeServers(r);
                var st = new AppSettings
                {
                    App = new AppConfig { LogLevel = Pick(r, "info", "debug", "warn"), ConfigMode = "generated" },
                    Tun = new TunSettings { Mtu = Pick(r, 1280, 1420, 1500), Ipv6Enabled = r.Next(3) == 0 },
                    Dns = new DnsSettings { VpnDns = Pick(r, "https://1.1.1.1/dns-query", "https://8.8.8.8/dns-query", "tls://9.9.9.9", "1.1.1.1", "https://dns.example.org:8443/x"), LocalDns = Pick(r, "local", "77.88.8.8", "https://dns.yandex.ru/dns-query"), Strategy = Pick(r, "ipv4_only", "prefer_ipv4") },
                    SingBox = new SingBoxSettings { ClashApi = Pick(r, "127.0.0.1:9090", "") },
                    Vless = new VlessConfig { Servers = servers, ActiveServer = r.Next(3) == 0 ? "" : servers[r.Next(servers.Count)].Name, AutoSelectBestServer = r.Next(3) == 0 },
                };
                st.App.RoutingMode = Pick(r, "split", "split", "full");
                st.App.RoutingAppsMode = Pick(r, "include", "exclude");
                st.App.BypassRussianTraffic = r.Next(2) == 0;
                st.App.StrictMode = r.Next(3) == 0;
                st.App.ForceIpv4Only = r.Next(4) == 0;
                st.App.StrictDns = r.Next(3) == 0;
                st.App.BlockAds = r.Next(4) == 0;
                st.App.DnsLeakLockdown = r.Next(2) == 0;
                st.App.ResolveLanViaSystemDns = r.Next(2) == 0;
                if (r.Next(2) == 0) st.App.LanDnsSuffixes = new List<string> { "corp", ".lan", "com", "my.home", "", "  ", "Local" };
                st.App.ConnectionIntent = Pick(r, "general", "gaming", "privacy", "compatibility");
                st.App.CustomRulesPriority = Pick(r, "toggles_first", "custom_first");
                int nr = r.Next(5);
                for (int k = 0; k < nr; k++)
                    st.App.CustomRules.Add(new CustomRule { Action = Pick(r, "direct", "proxy", "block"), Type = Pick(r, "domain", "domain_suffix", "domain_keyword", "ip_cidr", "port", "port_range", "network", "process_name", "geosite", "geoip"), Value = Pick(r, "a.com", "1.2.3.0/24", "443", "1000-2000", "tcp", "x.exe", "ru", "b,c.org"), Enabled = r.Next(5) > 0 });
                int nd = r.Next(3);
                for (int k = 0; k < nd; k++)
                    st.App.CustomDirectRules.Add(new CustomDirectRule { Type = Pick(r, "domain", "domain_suffix", "ip_cidr"), Value = Pick(r, "corp.local", "10.1.0.0/16"), Enabled = r.Next(4) > 0 });
                if (r.Next(3) == 0) st.App.RoutingAppsInclude = new List<string> { "extra.exe" };
                if (r.Next(3) == 0) st.App.RoutingAppsExclude = new List<string> { "chrome.exe" };
                var names = procs.Where(_ => r.Next(3) == 0).ToList();
                var profile = new Profile
                {
                    Name = "P", DnsMode = Pick(r, "vpn_only", "smart", "split"), BlockOnVpnFail = r.Next(3) == 0,
                    Processes = names.Select(n => new ProcessRule { Name = n, IncludeChildren = r.Next(2) == 0, ScanPatterns = new[] { n } }).ToList(),
                };
                bool? strict = Pick<bool?>(r, null, null, true, false);
                Func<VlessServerEntry, bool>? alive = r.Next(3) == 0 ? null : (r.Next(2) == 0 ? (_ => true) : (sv => sv.Name.Length % 2 == 0));
                string result;
                SingBoxConfig? gen = null;
                try { gen = ConfigGenerator.Generate(profile, names, st, strict, alive); result = "OK " + ConfigGenerator.Serialize(gen); }
                catch (Exception ex) { result = "EX " + ex.GetType().Name + " " + ex.Message; }
                EqRec.Write($"C {i} {EqRec.Hash(result.Replace(root, "<D>"))}");
                if (gen != null)
                {
                    EqRec.Write($"L {i} {EqRec.Hash(ValidateSummary(gen, st))}");
                    switch (r.Next(9))
                    {
                        case 0: gen.Dns.Strategy = "prefer_ipv4"; break;
                        case 1: gen.Outbounds?.RemoveAll(o => o.Tag == "direct"); break;
                        case 2: gen.Outbounds?.RemoveAll(o => o.Tag == "proxy"); break;
                        case 3: foreach (var ib in gen.Inbounds) ib.StrictRoute = true; break;
                        case 4: gen.Route.Final = gen.Route.Final == "proxy" ? "direct" : "proxy"; break;
                        case 5: gen.Route.Rules.RemoveAll(rl => rl.Action == "hijack-dns"); break;
                        case 6: gen.Dns.Final = "local-dns"; break;
                        case 7: if (gen.Outbounds != null) foreach (var o in gen.Outbounds) o.Flow = null; break;
                        default: gen.Outbounds = null!; break;
                    }
                    EqRec.Write($"M {i} {EqRec.Hash(ValidateSummary(gen, st))}");
                }
            }
        }
        finally
        {
            ServerUriParser.NaiveRuntimeAvailable = prevNaive;
            SingBoxFeatures.OverrideAwg = null;
            SingBoxFeatures.OverrideXhttp = null;
            ServerHealthStore.ResetForTests();
            AppPaths.OverrideDataDir(prev);
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
