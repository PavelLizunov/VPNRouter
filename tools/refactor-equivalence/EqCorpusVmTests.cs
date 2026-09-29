#nullable enable

using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using VPNRouter.App.ViewModels;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

public class EqCorpusVmTests
{
    private static readonly Regex Ts = new(@"\d{4}-\d\d-\d\d[T ][\d:.]+(?:Z|[+-]\d\d:\d\d)?", RegexOptions.Compiled);

    private static T Pick<T>(Random r, params T[] xs) => xs[r.Next(xs.Length)];

    private static string Dump(object vm)
    {
        var sb = new StringBuilder();
        foreach (var p in vm.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (p.GetIndexParameters().Length > 0 || !p.CanRead) continue;
            object? v;
            try { v = p.GetValue(vm); } catch (Exception ex) { sb.Append(p.Name).Append("=<throws ").Append(ex.GetType().Name).Append(">\n"); continue; }
            switch (v)
            {
                case null: sb.Append(p.Name).Append("=null\n"); break;
                case string or bool or int or long or double or float or decimal or Enum:
                    sb.Append(p.Name).Append('=').Append(Convert.ToString(v, CultureInfo.InvariantCulture)).Append('\n');
                    break;
                case IEnumerable e:
                {
                    var items = e.Cast<object?>().Take(60).ToList();
                    sb.Append(p.Name).Append(".count=").Append(items.Count).Append(':');
                    foreach (var it in items)
                    {
                        if (it == null) { sb.Append("null;"); continue; }
                        var np = it.GetType().GetProperty("Name");
                        sb.Append(np?.GetValue(it) ?? it.ToString()).Append(';');
                    }
                    sb.Append('\n');
                    break;
                }
            }
        }
        return sb.ToString();
    }

    private static string Yaml(AppSettings s)
    {
        var path = Path.Combine(Path.GetTempPath(), "eqcorpus-vm-yaml.yaml");
        SettingsLoader.Save(s, path);
        return Ts.Replace(File.ReadAllText(path), "<T>");
    }

    private static AppSettings MakeSettings(Random r)
    {
        var st = new AppSettings();
        st.App.Language = Pick(r, "", "ru", "en", "  ");
        st.App.Theme = Pick(r, "light", "dark", "system", "auto", "junk");
        st.App.ConfigMode = Pick(r, "generated", "subscribe", "custom", "SUBSCRIBE", "junk");
        st.App.RoutingMode = Pick(r, "split", "full", "FULL");
        st.App.RoutingAppsMode = Pick(r, "include", "exclude", " Exclude ", "weird");
        st.App.BypassRussianTraffic = r.Next(2) == 0;
        st.App.CustomRulesPriority = Pick(r, "custom_first", "toggles_first", "x");
        st.App.StrictMode = r.Next(2) == 0;
        st.App.ForceIpv4Only = r.Next(2) == 0;
        st.App.FlushDnsOnStart = r.Next(2) == 0;
        st.App.StrictDns = r.Next(2) == 0;
        st.App.BlockAds = r.Next(2) == 0;
        st.App.ConnectionIntent = Pick(r, "general", "gaming", "privacy", "compatibility", "junk");
        st.App.ZapretCustomArgs = Pick(r, "", "--foo bar");
        st.App.TgProxyPort = Pick(r, 0, 1443, 8080);
        st.App.TgProxySecret = Pick(r, "", "abcdef");
        st.App.UiMode = Pick(r, "simple", "advanced");
        st.Tun.Mtu = Pick(r, 1280, 1400, 1500, 900);
        st.Tun.Ipv6Enabled = r.Next(2) == 0;
        st.Update.Channel = Pick(r, "stable", "experimental");
        st.Vless.AutoSelectBestServer = r.Next(2) == 0;

        int nRules = r.Next(4);
        st.App.CustomRules = new List<CustomRule>();
        for (int i = 0; i < nRules; i++)
            st.App.CustomRules.Add(new CustomRule { Action = Pick(r, "direct", "proxy", "block"), Type = Pick(r, "domain", "domain_suffix", "ip_cidr"), Value = Pick(r, "a.com", "1.2.3.0/24", "b.org"), Enabled = r.Next(4) > 0 });

        int nServers = r.Next(4);
        st.Vless.Servers = new List<VlessServerEntry>();
        for (int i = 0; i < nServers; i++)
            st.Vless.Servers.Add(new VlessServerEntry { Name = "srv" + i, Server = $"10.0.0.{i + 1}", Port = 443, Uuid = "11111111-2222-3333-4444-55555555555" + i, Flow = Pick(r, "", "xtls-rprx-vision"), Reality = new VlessRealityConfig { PublicKey = "pk", ShortId = "ab" } });
        st.Vless.ActiveServer = nServers == 0 ? Pick(r, "", "nope") : Pick(r, "", "srv0", "SRV" + (nServers - 1), "nope");

        int nSubs = r.Next(3);
        st.App.Subscriptions = new List<SubscriptionEntry>();
        for (int i = 0; i < nSubs; i++)
            st.App.Subscriptions.Add(new SubscriptionEntry { Name = "sub" + i, Url = Pick(r, "https://a.example/s", "", "https://b.example/t"), Enabled = r.Next(3) > 0, Servers = new List<VlessServerEntry> { new() { Name = "ss" + i, Server = $"20.0.0.{i + 1}", Port = 443 } } });
        if (nSubs == 0 && r.Next(2) == 0)
        {
            st.App.SubscriptionUrl = "https://legacy.example/sub";
            st.App.SubscriptionServers = new List<VlessServerEntry> { new() { Name = "legacy1", Server = "30.0.0.1", Port = 443 } };
        }
        st.App.ActiveSubscriptionServer = Pick(r, "", "ss0", "nope");

        int nCfg = r.Next(3);
        st.App.CustomConfigs = new List<CustomConfigEntry>();
        for (int i = 0; i < nCfg; i++)
            st.App.CustomConfigs.Add(new CustomConfigEntry { Name = "cfg" + i, Path = $"C:\\nope\\cfg{i}.json" });
        st.App.ActiveCustomConfig = nCfg == 0 ? Pick(r, "", "nope") : Pick(r, "", "cfg0", "nope");
        return st;
    }

    private static void SetProp(object vm, string name, object? value)
    {
        var p = vm.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        if (p == null || !p.CanWrite) return;
        try { p.SetValue(vm, value); } catch { }
    }

    [AvaloniaFact]
    public void VmCorpus()
    {
        var prev = AppPaths.DataDir;
        var root = Path.Combine(Path.GetTempPath(), "eqcorpus-data-vm");
        try { Directory.Delete(root, true); } catch { }
        Directory.CreateDirectory(root);
        AppPaths.OverrideDataDir(root);
        try
        {
            var r = new Random(9001);
            for (int i = 0; i < 40; i++)
            {
                var store = new InMemorySettingsStore();
                var st = MakeSettings(r);
                store.Save(st, AppPaths.ConfigYamlPath);
                MainWindowViewModel vm;
                try { vm = new MainWindowViewModel(store); }
                catch (Exception ex) { EqRec.Write($"V {i} CTOR-EX {EqRec.Hash(ex.GetType().Name + ex.Message)}"); continue; }

                var loaded = Dump(vm) + "---\n" + Yaml(store.Load(AppPaths.ConfigYamlPath));
                loaded = loaded.Replace(root, "<D>");
                EqRec.Write($"V {i} L {EqRec.Hash(loaded)}");
                EqRec.Full($"V {i} L {EqRec.Hash(loaded)}", loaded);

                for (int k = 0; k < 6; k++)
                {
                    switch (r.Next(15))
                    {
                        case 0: SetProp(vm, "IsSplitTunnel", r.Next(2) == 0); break;
                        case 1: SetProp(vm, "IsSubscribeMode", r.Next(2) == 0); break;
                        case 2: SetProp(vm, "IsVlessMode", r.Next(2) == 0); break;
                        case 3: SetProp(vm, "StrictMode", r.Next(2) == 0); break;
                        case 4: SetProp(vm, "ForceIpv4Only", r.Next(2) == 0); break;
                        case 5: SetProp(vm, "FlushDnsOnStart", r.Next(2) == 0); break;
                        case 6: SetProp(vm, "StrictDns", r.Next(2) == 0); break;
                        case 7: SetProp(vm, "BlockAds", r.Next(2) == 0); break;
                        case 8: SetProp(vm, "AutoSelectBestServer", r.Next(2) == 0); break;
                        case 9: SetProp(vm, "ReceivePrereleases", r.Next(2) == 0); break;
                        case 10: SetProp(vm, "IsSimpleMode", r.Next(2) == 0); break;
                        case 11: SetProp(vm, "BypassRussianTraffic", r.Next(2) == 0); break;
                        case 12: SetProp(vm, "TunMtu", Pick(r, 1200, 1280, 1400, 1500, 2000)); break;
                        case 13: SetProp(vm, "RoutingAppsMode", Pick(r, "include", "exclude", "weird", " Exclude ")); break;
                        default: SetProp(vm, "CustomRulesText", Pick(r, "", "domain,a.com,direct", "ip_cidr,1.2.3.0/24,block\ngarbage", "domain_suffix,b.org,proxy")); break;
                    }
                }

                var save = typeof(MainWindowViewModel).GetMethod("SaveSettings", BindingFlags.Instance | BindingFlags.NonPublic)!;
                string after;
                try
                {
                    save.Invoke(vm, null);
                    after = Dump(vm) + "---\n" + Yaml(store.Load(AppPaths.ConfigYamlPath));
                }
                catch (TargetInvocationException ex) { after = "EX " + ex.InnerException!.GetType().Name + " " + ex.InnerException.Message; }
                after = after.Replace(root, "<D>");
                EqRec.Write($"V {i} S {EqRec.Hash(after)}");
                EqRec.Full($"V {i} S {EqRec.Hash(after)}", after);
            }
        }
        finally
        {
            AppPaths.OverrideDataDir(prev);
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
