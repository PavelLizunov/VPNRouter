package com.ninitux.vpnrouter;

import java.io.File;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.List;
import org.json.JSONArray;
import org.json.JSONObject;

public final class AndroidTunnelPolicyTest {
    private static final File FILES = new File("/private/app/files");
    private static final String PROFILE = """
            {"inbounds":[{"type":"tun","address":"172.19.0.1/30","include_package":["profile.app"]}],
             "experimental":{"clash_api":{"external_controller":"127.0.0.1:9090",
                 "secret":"test-fixture","default_mode":"rule","external_ui":"dashboard"},
                 "v2ray_api":{"listen":"127.0.0.1:10085"},"debug":{"listen":"127.0.0.1:6060"},
                 "cache_file":{"enabled":true,"path":"/outside/cache.db","store_dns":true}},
             "outbounds":[{"type":"direct","tag":"direct"}]}
            """;

    public static void main(String[] args) throws Exception {
        checkVpnInboundCount();
        checkPrivatePathsAndListeners();
        checkPackageSelection();
        checkRulesAndDns();
        checkDisabledVlessTls();
        checkRestorePreferences();
    }

    private static void checkVpnInboundCount() throws Exception {
        JSONObject tun = new JSONObject().put("type", "tun").put("address", "172.19.0.1/30");
        JSONObject http = new JSONObject().put("type", "http").put("listen", "127.0.0.1").put("listen_port", 8080);
        for (String mode : Arrays.asList(null, "off", "include", "exclude")) {
            rejected(() -> prepare("{}", mode, false));
            for (JSONArray inbounds : Arrays.asList(new JSONArray(), new JSONArray().put(http),
                    new JSONArray().put(tun).put(new JSONObject(tun.toString()).put("tag", "second-tun")))) {
                String json = new JSONObject().put("inbounds", inbounds).toString();
                rejected(() -> prepare(json, mode, false));
            }
        }
        JSONArray inbounds = new JSONArray().put(http).put(tun);
        String json = new JSONObject().put("inbounds", inbounds).toString();
        String accepted = prepare(json, "off", false);
        require(inbounds.similar(new JSONObject(accepted).getJSONArray("inbounds")));
        require(inbounds.similar(new JSONObject(prepare(accepted, "off", false)).getJSONArray("inbounds")));
        rejected(() -> prepare(json, "include", false));
    }

    private static void checkPrivatePathsAndListeners() throws Exception {
        JSONObject cfg = new JSONObject(prepare(PROFILE, "include", false));
        JSONObject exp = cfg.getJSONObject("experimental");
        JSONObject clash = exp.getJSONObject("clash_api");
        require(clash.length() == 1 && "rule".equals(clash.getString("default_mode")));
        require(!exp.has("v2ray_api") && !exp.getJSONObject("debug").has("listen"));
        require(!cfg.getJSONArray("inbounds").getJSONObject(0).has("include_package"));
        require(cfg.getJSONArray("outbounds").length() == 1);
        require(cfg.similar(new JSONObject(prepare(cfg.toString(), "include", false))));
        for (String mode : Arrays.asList("off", "include", "exclude")) {
            String raw = PROFILE.replace(",\"include_package\":[\"profile.app\"]", "");
            for (String path : Arrays.asList("/outside/cache.db", "../old.db")) {
                JSONObject cache = new JSONObject(prepare(raw.replace("/outside/cache.db", path), mode, false))
                        .getJSONObject("experimental").getJSONObject("cache_file");
                require(!cache.has("path") && cache.getBoolean("store_dns") && cache.getBoolean("enabled"));
            }
        }

        String tun = "{\"type\":\"tun\",\"address\":\"172.19.0.1/30\"}";
        String custom = "{\"inbounds\":[" + tun + ",{\"type\":\"http\"}]}";
        rejected(() -> prepare(custom, "exclude", false));
        require(new JSONObject(prepare(custom, "off", false)).getJSONArray("inbounds").length() == 2);
        rejected(() -> prepare(custom.replace("\"type\":\"tun\"",
                "\"type\":\"tun\",\"exclude_package\":\"profile.app\""), "off", false));
        rejected(() -> prepare(PROFILE, "include", true));
        for (String type : Arrays.asList("openvpn-server", "tailscale", "unknown")) {
            String json = profileWith("endpoints", new JSONObject().put("type", type));
            rejected(() -> prepare(json, "off", false)); // profile allowlist with no UI selection
            rejected(() -> prepare(json, "include", false));
        }
        String peer = "{\"address\":\"peer.example\",\"port\":51820}";
        JSONObject wg = new JSONObject().put("type", "wireguard").put("jc", 4)
                .put("peers", new JSONArray().put(new JSONObject(peer)));
        JSONObject awg = new JSONObject(profileWith("endpoints", wg))
                .put("services", new JSONArray().put(new JSONObject().put("type", "oom-killer")));
        JSONObject accepted = new JSONObject(prepare(awg.toString(), "exclude", false));
        require(accepted.getJSONArray("endpoints").getJSONObject(0).getInt("jc") == 4);
        require(accepted.getJSONArray("services").getJSONObject(0).getString("type").equals("oom-killer"));
        for (JSONObject unsafe : Arrays.asList(
                new JSONObject(wg.toString()).put("listen_port", 51820),
                new JSONObject(wg.toString()).put("system", true),
                new JSONObject(wg.toString()).put("peers", new JSONArray().put(new JSONObject(peer).put("address", "::"))),
                new JSONObject(wg.toString()).put("peers", new JSONArray()),
                new JSONObject(wg.toString()).put("peers", new JSONArray().put(new JSONObject(peer)).put(new JSONObject(peer))),
                new JSONObject(wg.toString()).put("peers", new JSONArray().put(new JSONObject().put("port", 51820))),
                new JSONObject(wg.toString()).put("peers", new JSONArray().put(new JSONObject().put("address", "peer.example"))))) {
            rejected(() -> prepare(profileWith("endpoints", unsafe), "include", false));
        }
        rejected(() -> prepare(profileWith("services", new JSONObject().put("type", "api")), "off", false));
        rejected(() -> prepare(profileWith("outbounds", new JSONObject().put("type", "tor")), "include", false));
        rejected(() -> prepare(profileWith("certificate_providers", new JSONObject().put("type", "acme")), "include", false));
    }

    private static void checkPackageSelection() throws Exception {
        String[] legacy = {"one.app", "two.app"};
        require(AndroidTunnelPolicy.usesLegacyAllowlist(null, null, legacy));
        require(!AndroidTunnelPolicy.usesLegacyAllowlist("off", legacy, new String[0]));
        require(!AndroidTunnelPolicy.usesLegacyAllowlist("include", new String[]{"two.app", "one.app"}, legacy));
        rejected(() -> AndroidTunnelPolicy.usesLegacyAllowlist("off", null, legacy));
        rejected(() -> AndroidTunnelPolicy.usesLegacyAllowlist("exclude", legacy, legacy));
        rejected(() -> AndroidTunnelPolicy.usesLegacyAllowlist("include", new String[]{"one.app"}, legacy));
        rejected(() -> prepare(PROFILE, "invalid", false));
        for (String mode : Arrays.asList("include", "exclude", "off")) {
            String both = PROFILE.replace("\"include_package\"", "\"exclude_package\":[],\"include_package\"");
            rejected(() -> AndroidTunnelPolicy.checkDeclaredPackages(both, mode));
            rejected(() -> prepare(both, mode, false));
            for (String key : Arrays.asList("include_uid", "exclude_uid", "include_uid_range",
                    "exclude_uid_range", "include_android_user")) {
                String json = PROFILE.replace("\"include_package\"", "\"" + key + "\":[0],\"include_package\"");
                rejected(() -> prepare(json, mode, false));
            }
        }
        String empty = PROFILE.replace("[\"profile.app\"]", "[]");
        rejected(() -> AndroidTunnelPolicy.checkDeclaredPackages(empty, "off"));
        rejected(() -> prepare(empty, "off", false));
        rejected(() -> AndroidTunnelPolicy.requireAllowedPackage(Collections.emptyList(), name -> true));
        rejected(() -> AndroidTunnelPolicy.prepare(PROFILE, "include", false, Collections.emptyList(), name -> true, FILES));
        rejected(() -> AndroidTunnelPolicy.prepare(PROFILE, "include", false,
                Collections.singletonList("removed.app"), name -> false, FILES));
        rejected(() -> AndroidTunnelPolicy.prepare(PROFILE, "off", false, Collections.emptyList(), name -> false, FILES));
        rejected(() -> AndroidTunnelPolicy.prepare(PROFILE, "exclude", false,
                Arrays.asList("present.app", "removed.app"), "present.app"::equals, FILES));
        rejected(() -> AndroidTunnelPolicy.prepare(PROFILE.replace("include_package", "exclude_package"),
                "off", false, Collections.emptyList(), name -> false, FILES));
        rejected(() -> AndroidTunnelPolicy.prepare(PROFILE, "include", false,
                Arrays.asList("present.app", "first.app\nsecond.app"), name -> true, FILES));
        List<String> visited = new ArrayList<>();
        AndroidTunnelPolicy.requireAllowedPackage(Arrays.asList("", "first.app", "second.app"), name -> {
            visited.add(name);
            return name.equals("first.app");
        });
        require(visited.equals(Arrays.asList("first.app", "second.app")));
    }

    private static void checkRulesAndDns() throws Exception {
        String full = PROFILE.replace(",\"include_package\":[\"profile.app\"]", "");
        for (String field : Arrays.asList("package_name", "package_name_regex", "process_name",
                "process_path", "process_path_regex", "user", "user_id")) {
            JSONObject logical = new JSONObject().put("type", "logical").put("mode", "or")
                    .put("rules", new JSONArray().put(new JSONObject().put(field, "user_id".equals(field) ? 123 : "example")));
            JSONObject rules = new JSONObject().put("rules", new JSONArray().put(logical));
            for (String section : Arrays.asList("route", "dns")) {
                rejected(() -> prepare(new JSONObject(full).put(section, rules).toString(), "off", false));
            }
            JSONObject set = new JSONObject().put("tag", "test").put("rules", new JSONArray().put(logical));
            rejected(() -> prepare(withRuleSet(full, set), "include", false));
        }
        JSONObject local = new JSONObject().put("tag", "test").put("type", "local")
                .put("path", new File(FILES, "geo/geoip-ru.srs").getPath());
        prepare(withRuleSet(full, local), "include", false);
        rejected(() -> prepare(withRuleSet(full, new JSONObject(local.toString()).put("path", "/outside/geoip-ru.srs")), "off", false));
        rejected(() -> prepare(withRuleSet(full, new JSONObject(local.toString()).put("type", "remote")), "off", false));

        JSONObject sidecar = new JSONObject().put("process_name", "slipstream-client").put("outbound", "direct");
        String sidecarProfile = new JSONObject(full).put("route", new JSONObject()
                .put("rules", new JSONArray().put(sidecar).put(new JSONObject().put("action", "hijack-dns")))).toString();
        require(new JSONObject(prepare(sidecarProfile, "off", true)).getJSONObject("route").getJSONArray("rules").length() == 1);
        rejected(() -> prepare(sidecarProfile, "off", false));
        rejected(() -> prepare(sidecarProfile, "include", true));

        require(AndroidTunnelPolicy.usesVpnDns("") && AndroidTunnelPolicy.usesVpnDns("native")
                && AndroidTunnelPolicy.usesVpnDns("hijack") && !AndroidTunnelPolicy.usesVpnDns("disabled"));
        rejected(() -> AndroidTunnelPolicy.usesVpnDns("unknown"));
        String narrow = full.replace("172.19.0.1/30", "172.19.0.1/32");
        rejected(() -> prepare(narrow, "off", false));
        rejected(() -> prepare(full.replace("172.19.0.1/30", "172.19.0.3/30"), "off", false));
        prepare(narrow.replace("\"address\":", "\"dns_mode\":\"disabled\",\"address\":"), "off", false);
        prepare(narrow.replace("\"address\":", "\"dns_address\":\"192.0.2.53\",\"address\":"), "off", false);
        rejected(() -> prepare(narrow.replace("\"address\":", "\"dns_address\":\"2001:db8::53\",\"address\":"), "off", false));
        rejected(() -> prepare(narrow.replace("\"address\":", "\"dns_address\":[\"192.0.2.53\",\"127.0.0.1\"],\"address\":"), "off", false));
        prepare(narrow.replace("\"172.19.0.1/32\"", "[\"172.19.0.1/32\",\"fd00::1/126\"]"), "include", false);
        rejected(() -> prepare(narrow.replace("\"172.19.0.1/32\"", "[\"172.19.0.1/32\",\"172.19.1.1/30\"]"), "off", false));
    }

    private static void checkDisabledVlessTls() throws Exception {
        JSONObject vless = new JSONObject().put("type", "vless");
        JSONObject tls = new JSONObject().put("enabled", true).put("server_name", "example.org");
        JSONObject enabled = new JSONObject(vless.toString()).put("tls", tls);
        JSONObject vmess = new JSONObject().put("type", "vmess").put("tls", new JSONObject().put("enabled", false));
        JSONArray outbounds = new JSONArray()
                .put(new JSONObject(vless.toString()).put("tls", new JSONObject().put("enabled", false)))
                .put(new JSONObject(vless.toString()).put("tls", new JSONObject()))
                .put(vless).put(enabled).put(vmess);

        JSONObject profile = new JSONObject(PROFILE).put("outbounds", outbounds);
        for (String mode : Arrays.asList("off", "include", "exclude")) {
            JSONArray accepted = new JSONObject(prepare(profile.toString(), mode, false)).getJSONArray("outbounds");
            require(accepted.length() == 5);
            require(!accepted.getJSONObject(0).has("tls"));
            require(!accepted.getJSONObject(1).has("tls"));
            require(accepted.getJSONObject(2).similar(vless));
            require(accepted.getJSONObject(3).similar(enabled));
            require(accepted.getJSONObject(4).similar(vmess));
        }
    }

    private static void checkRestorePreferences() throws Exception {
        AndroidTunnelPolicy.Preferences absent = new AndroidTunnelPolicy.Preferences(null, "[]");
        AndroidTunnelPolicy.Preferences off = new AndroidTunnelPolicy.Preferences("off", "[\"dormant.app\"]");
        AndroidTunnelPolicy.Preferences included = new AndroidTunnelPolicy.Preferences("include", "[\"a.app\",\"b.app\"]");
        AndroidTunnelPolicy.Preferences reordered = new AndroidTunnelPolicy.Preferences("INCLUDE", "[\"b.app\",\"a.app\",\"a.app\"]");
        AndroidTunnelPolicy.Preferences edited = new AndroidTunnelPolicy.Preferences("include", "[\"b.app\"]");
        AndroidTunnelPolicy.Preferences excluded = new AndroidTunnelPolicy.Preferences("exclude", "[\"a.app\",\"b.app\"]");

        // A false decision retains both the saved mode and its explicit package list.
        require(!absent.supersedes(null, "include") && !off.supersedes(null, "include"));
        require(!absent.supersedes(absent.snapshot(), "include") && !off.supersedes(off.snapshot(), "include"));
        require(!off.supersedes(new AndroidTunnelPolicy.Preferences("off", "[]").snapshot(), "include"));
        require(!included.supersedes(null, "exclude") && !excluded.supersedes(null, "include"));
        require(included.supersedes(null, "off") && excluded.supersedes(null, "off"));
        require(off.supersedes(absent.snapshot(), "include") && off.supersedes(included.snapshot(), "include"));
        require(absent.supersedes(included.snapshot(), "include") && included.supersedes(off.snapshot(), "include"));
        require(excluded.supersedes(included.snapshot(), "include") && edited.supersedes(included.snapshot(), "include"));
        require(!reordered.supersedes(included.snapshot(), "include") && !edited.supersedes(edited.snapshot(), "include"));
        require(edited.packages.equals(Collections.singletonList("b.app")));
        rejected(() -> new AndroidTunnelPolicy.Preferences("unknown", "[]"));
    }

    private static String profileWith(String key, JSONObject item) throws Exception {
        return new JSONObject(PROFILE).put(key, new JSONArray().put(item)).toString();
    }

    private static String withRuleSet(String profile, JSONObject set) throws Exception {
        return new JSONObject(profile).put("route", new JSONObject().put("rule_set", new JSONArray().put(set))).toString();
    }

    private static String prepare(String json, String mode, boolean slipstream) throws Exception {
        return AndroidTunnelPolicy.prepare(json, mode, slipstream, Collections.singletonList("selected.app"), name -> true, FILES);
    }

    private interface Check { void run() throws Exception; }

    private static void rejected(Check check) throws Exception {
        try { check.run(); }
        catch (IllegalArgumentException expected) { return; }
        throw new AssertionError("Expected an Android policy rejection");
    }

    private static void require(boolean value) {
        if (!value) throw new AssertionError("Android tunnel policy regression");
    }
}
