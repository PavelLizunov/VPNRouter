package com.ninitux.vpnrouter;

import java.io.File;
import java.math.BigInteger;
import java.net.InetAddress;
import java.net.UnknownHostException;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.TreeSet;
import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

/** Applies Android's app-selection boundary to libbox's canonical configuration. */
final class AndroidTunnelPolicy {
    private AndroidTunnelPolicy() { }

    private static boolean split(String mode) {
        return "include".equalsIgnoreCase(mode) || "exclude".equalsIgnoreCase(mode);
    }

    static boolean usesLegacyAllowlist(String mode, String[] packages, String[] allowed) {
        if (allowed == null || allowed.length == 0) return false;
        if (mode == null) return true;
        if (!"include".equalsIgnoreCase(mode) || packages == null
                || !new HashSet<>(Arrays.asList(allowed)).equals(new HashSet<>(Arrays.asList(packages)))) {
            throw new IllegalArgumentException("Conflicting Android application selections");
        }
        return false;
    }

    // formatConfig omits empty lists. Check the declarations before that information is lost.
    static void checkDeclaredPackages(String json, String mode) throws JSONException {
        JSONArray inbounds = new JSONObject(json).optJSONArray("inbounds");
        for (int i = 0; inbounds != null && i < inbounds.length(); i++) {
            JSONObject tun = inbounds.getJSONObject(i);
            if ("tun".equals(tun.optString("type"))) checkPackageFields(tun, mode);
        }
    }

    private static void checkPackageFields(JSONObject tun, String mode) throws JSONException {
        if (tun.has("include_package") && tun.has("exclude_package")) {
            throw new IllegalArgumentException("TUN cannot combine included and excluded applications");
        }
        if (!split(mode) && tun.has("include_package") && strings(tun, "include_package").isEmpty()) {
            throw new IllegalArgumentException("The TUN application allowlist is empty");
        }
    }

    static String prepare(String canonicalConfig, String mode, boolean usesSlipstream,
                          Iterable<String> selected, PackageOperation installed, File filesDir) throws JSONException {
        JSONObject root = new JSONObject(canonicalConfig);
        JSONObject experimental = root.optJSONObject("experimental");
        if (experimental == null) {
            experimental = new JSONObject();
            root.put("experimental", experimental);
        }
        // libbox's default cache.db is relative to its private files/data working directory.
        JSONObject cache = experimental.optJSONObject("cache_file");
        if (cache != null) cache.remove("path");
        JSONObject oldClash = experimental.optJSONObject("clash_api");
        JSONObject clash = new JSONObject();
        if (oldClash != null && oldClash.has("default_mode")) {
            clash.put("default_mode", oldClash.getString("default_mode"));
        }
        experimental.put("clash_api", clash);

        boolean selectedByUi = split(mode);
        if (!selectedByUi && mode != null && !"off".equalsIgnoreCase(mode)) {
            throw new IllegalArgumentException("Unknown Android app selection mode");
        }
        boolean filtered = selectedByUi;
        if (selectedByUi) {
            for (String name : selected) {
                if (name != null && (name.indexOf('\n') >= 0 || name.indexOf('\r') >= 0)) {
                    throw new IllegalArgumentException("Invalid package in the saved application selection");
                }
            }
            if ("include".equalsIgnoreCase(mode)) requireAllowedPackage(selected, installed);
            else requireExcludedPackages(selected, installed);
        }
        JSONArray inbounds = root.optJSONArray("inbounds");
        if (inbounds == null) inbounds = new JSONArray();
        int tunCount = 0;
        for (int i = 0; i < inbounds.length(); i++) {
            JSONObject inbound = inbounds.getJSONObject(i);
            if (!"tun".equals(inbound.getString("type"))) continue;
            tunCount++;
            checkPackageFields(inbound, mode);
            for (String key : new String[]{"include_uid", "exclude_uid", "include_uid_range",
                    "exclude_uid_range", "include_android_user"}) {
                if (inbound.has(key)) throw new IllegalArgumentException("Unsupported Android TUN selector: " + key);
            }
            checkDns(inbound);
            if (selectedByUi) {
                inbound.remove("include_package");
                inbound.remove("exclude_package");
            } else if (inbound.has("include_package") || inbound.has("exclude_package")) {
                filtered = true;
                if (inbound.has("include_package")) {
                    requireAllowedPackage(strings(inbound, "include_package"), installed);
                } else {
                    requireExcludedPackages(strings(inbound, "exclude_package"), installed);
                }
            }
        }
        if (tunCount != 1) {
            throw new IllegalArgumentException("Android VPN requires exactly one TUN inbound");
        }
        if (filtered) {
            if (inbounds.length() != 1) {
                throw new IllegalArgumentException("Per-app VPN requires a single TUN inbound");
            }
            if (usesSlipstream) {
                throw new IllegalArgumentException("The DNS tunnel's local listener is incompatible with per-app VPN");
            }
            checkSplitListeners(root);
            experimental.remove("v2ray_api");
            JSONObject debug = experimental.optJSONObject("debug");
            if (debug != null) debug.remove("listen");
        }
        JSONObject route = root.optJSONObject("route");
        if (route != null) {
            checkRules(route.optJSONArray("rules"), usesSlipstream);
            JSONArray sets = route.optJSONArray("rule_set");
            for (int i = 0; sets != null && i < sets.length(); i++) {
                JSONObject set = sets.getJSONObject(i);
                String type = set.optString("type");
                if (type.isEmpty() || "inline".equals(type)) {
                    checkRules(set.optJSONArray("rules"), false);
                } else if (!managedRuleSet(set, filesDir)) {
                    throw new IllegalArgumentException("Cannot verify application predicates in an external rule-set");
                }
            }
        }
        JSONObject dns = root.optJSONObject("dns");
        if (dns != null) checkRules(dns.optJSONArray("rules"), false);
        return root.toString();
    }

    private static void checkSplitListeners(JSONObject root) throws JSONException {
        JSONArray services = root.optJSONArray("services");
        for (int i = 0; services != null && i < services.length(); i++) {
            if (!"oom-killer".equals(services.getJSONObject(i).getString("type"))) {
                throw new IllegalArgumentException("This service is incompatible with per-app VPN");
            }
        }
        JSONArray endpoints = root.optJSONArray("endpoints");
        for (int i = 0; endpoints != null && i < endpoints.length(); i++) {
            JSONObject ep = endpoints.getJSONObject(i);
            JSONArray peers = ep.optJSONArray("peers");
            // The managed WG/AWG client has one remote peer, a userspace interface and no listening port.
            if (!"wireguard".equals(ep.getString("type")) || ep.optBoolean("system")
                    || ep.optInt("listen_port") != 0 || peers == null || peers.length() != 1) {
                throw new IllegalArgumentException("This endpoint is incompatible with per-app VPN");
            }
            JSONObject peer = peers.getJSONObject(0);
            String address = peer.optString("address").trim();
            if (address.isEmpty() || "0.0.0.0".equals(address) || "::".equals(address) || peer.optInt("port") <= 0) {
                throw new IllegalArgumentException("Per-app WireGuard requires one configured remote peer");
            }
        }
        JSONArray outbounds = root.optJSONArray("outbounds");
        for (int i = 0; outbounds != null && i < outbounds.length(); i++) {
            if ("tor".equals(outbounds.getJSONObject(i).getString("type"))) {
                throw new IllegalArgumentException("Tor's SOCKS listener is incompatible with per-app VPN");
            }
        }
        JSONArray providers = root.optJSONArray("certificate_providers");
        for (int i = 0; providers != null && i < providers.length(); i++) {
            JSONObject provider = providers.getJSONObject(i);
            if ("acme".equals(provider.getString("type"))
                    && !(provider.optBoolean("disable_http_challenge") && provider.optBoolean("disable_tls_alpn_challenge"))) {
                throw new IllegalArgumentException("ACME challenge listeners are incompatible with per-app VPN");
            }
        }
    }

    private static boolean managedRuleSet(JSONObject set, File filesDir) {
        if (!"local".equals(set.optString("type")) || !"binary".equals(set.optString("format", "binary"))) return false;
        String path = set.optString("path");
        return new File(filesDir, "geo/geoip-ru.srs").getPath().equals(path)
                || new File(filesDir, "geo/geosite-ru.srs").getPath().equals(path)
                || new File(filesDir, "cache/rulesets/adblock_reject.srs").getPath().equals(path);
    }

    private static void checkRules(JSONArray rules, boolean usesSlipstream) throws JSONException {
        for (int i = 0; rules != null && i < rules.length(); i++) {
            JSONObject rule = rules.getJSONObject(i);
            // Android runs the sidecar inside the already excluded VPN app. Drop only the exact
            // redundant top-level process exemption emitted by the cross-platform generator.
            if (usesSlipstream && rule.length() == (rule.has("action") ? 3 : 2)
                    && "route".equals(rule.optString("action", "route"))
                    && "direct".equals(rule.optString("outbound"))
                    && strings(rule, "process_name").equals(Collections.singletonList("slipstream-client"))) {
                rules.remove(i--);
                continue;
            }
            for (String key : new String[]{"package_name", "package_name_regex", "process_name",
                    "process_path", "process_path_regex", "user", "user_id"}) {
                if (rule.has(key)) throw new IllegalArgumentException("Unsupported Android routing predicate: " + key);
            }
            checkRules(rule.optJSONArray("rules"), false);
        }
    }

    static boolean usesVpnDns(String mode) {
        if ("disabled".equals(mode)) return false;
        if ("".equals(mode) || "hijack".equals(mode) || "native".equals(mode)) return true;
        throw new IllegalArgumentException("Unsupported TUN DNS mode");
    }

    private static void checkDns(JSONObject tun) throws JSONException {
        if (!usesVpnDns(tun.optString("dns_mode"))) return;
        List<String> configured = strings(tun, "dns_address");
        boolean seen4 = false, seen6 = false, usable = false;
        for (String prefix : strings(tun, "address")) {
            boolean v6 = prefix.contains(":");
            if ((v6 && seen6) || (!v6 && seen4)) continue;
            if (v6) seen6 = true;
            else seen4 = true;
            if (!configured.isEmpty()) {
                for (String dns : configured) {
                    if (dns.contains(":") != v6) continue;
                    InetAddress address = numericAddress(dns);
                    if (address.isAnyLocalAddress() || address.isLoopbackAddress()) {
                        throw new IllegalArgumentException("Android cannot use a wildcard or loopback VPN DNS server");
                    }
                    usable = true;
                }
            } else {
                // sing-tun derives DNS from the next address within the first prefix of each family.
                int slash = prefix.indexOf('/');
                String host = prefix.substring(0, slash);
                InetAddress address = numericAddress(host);
                int hostBits = (v6 ? 128 : 32) - Integer.parseInt(prefix.substring(slash + 1));
                BigInteger ip = new BigInteger(1, address.getAddress());
                BigInteger next = ip.add(BigInteger.ONE);
                if (ip.shiftRight(hostBits).equals(next.shiftRight(hostBits))) {
                    if (v6 ? next.equals(BigInteger.ONE) : next.shiftRight(24).intValue() == 127) {
                        throw new IllegalArgumentException("Android cannot use a loopback TUN DNS server");
                    }
                    usable = true;
                }
            }
        }
        if (!usable) throw new IllegalArgumentException("TUN needs a usable DNS address for an enabled address family");
    }

    // prepare only receives addresses already decoded and formatted as numeric netip values by libbox.
    private static InetAddress numericAddress(String literal) {
        try {
            return InetAddress.getByName(literal);
        } catch (UnknownHostException e) {
            throw new IllegalArgumentException("Invalid numeric TUN address", e);
        }
    }

    private static List<String> strings(JSONObject obj, String key) throws JSONException {
        if (obj.isNull(key)) return Collections.emptyList();
        JSONArray values = obj.optJSONArray(key);
        if (values == null) return Collections.singletonList(obj.getString(key));
        List<String> result = new ArrayList<>();
        for (int i = 0; i < values.length(); i++) result.add(values.getString(i));
        return result;
    }

    static final class Preferences {
        final String mode;
        final List<String> packages;

        Preferences(String mode, String packagesJson) throws JSONException {
            this.mode = mode == null ? null : mode.toLowerCase(Locale.ROOT);
            if (mode != null && !split(mode) && !"off".equals(this.mode)) {
                throw new IllegalArgumentException("Unknown Android app selection preference");
            }
            TreeSet<String> names = new TreeSet<>();
            if (split(mode)) {
                JSONArray values = new JSONArray(packagesJson);
                for (int i = 0; i < values.length(); i++) names.add(values.getString(i));
            }
            packages = new ArrayList<>(names);
        }

        String snapshot() {
            return new JSONArray().put(mode == null ? JSONObject.NULL : mode).put(new JSONArray(packages)).toString();
        }

        boolean supersedes(String savedSnapshot, String savedMode) {
            // Without a baseline, keep an existing saved split. An active UI selection can narrow an old full tunnel.
            return savedSnapshot == null ? !split(savedMode) && split(mode) : !snapshot().equals(savedSnapshot);
        }
    }

    interface PackageOperation {
        boolean apply(String name);
    }

    static void requireExcludedPackages(Iterable<String> packages, PackageOperation target) {
        for (String name : packages) {
            if (name == null || name.isEmpty() || !target.apply(name)) {
                throw new IllegalArgumentException("Cannot enforce an Android package exclusion");
            }
        }
    }

    static void requireAllowedPackage(Iterable<String> packages, PackageOperation target) {
        boolean added = false;
        for (String name : packages) {
            if (name != null && !name.isEmpty() && target.apply(name)) added = true;
        }
        if (!added) throw new IllegalArgumentException("Select at least one installed app for per-app VPN");
    }
}
