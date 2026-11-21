/*
 * Contract tests for the VPNRouter macOS privileged helper.
 *
 * The production source is included so the test exercises the same parser and
 * validators that root will run. macOS CI builds this with ASan/UBSan.
 */

#define main vpnrouter_helper_main
#include "../vpnrouter-privileged.c"
#undef main

static int failures;

static void expect(int condition, const char *message)
{
    if (condition)
        return;
    fprintf(stderr, "FAIL: %s\n", message);
    failures++;
}

static int config_is_valid(const char *json)
{
    char *copy = strdup(json);
    if (copy == NULL)
        return 0;
    struct doc document = { NULL, 0, NULL, 0 };
    int valid = parse_doc(copy, strlen(copy), &document) == 0 &&
                validate_config(&document, 501) == 0;
    free(document.tokens);
    free(copy);
    return valid;
}

static int request_is_invalid(const char *json)
{
    char *copy = strdup(json);
    if (copy == NULL)
        return 0;
    struct doc document = { NULL, 0, NULL, 0 };
    int duplicate = parse_doc(copy, strlen(copy), &document) != 0 ||
                    validate_unique_keys(&document, 0) != 0;
    free(document.tokens);
    free(copy);
    return duplicate;
}

static void test_json_structure(void)
{
    expect(!request_is_invalid("{\"value\":\"text [ { \\\" } ]\"} \t\r\n"),
           "brackets and escaped quotes inside a value do not affect nesting");
    expect(request_is_invalid("{\"value\":1} {}"),
           "a second JSON object is rejected");

    expect(request_is_invalid("{\"value\":1,}"),
           "a trailing comma is rejected");
    expect(request_is_invalid("{\"value\":1 \"other\":2}"),
           "a missing comma is rejected");
    expect(request_is_invalid("{\"value\":01}"),
           "a leading zero is rejected");
    expect(request_is_invalid("{\"value\":+1}"),
           "a leading plus is rejected");
    expect(request_is_invalid("{\"value\":\"\\q\"}"),
           "an invalid string escape is rejected");
    expect(request_is_invalid("{\"value\":\"line\nbreak\"}"),
           "a raw control character in a string is rejected");
    expect(request_is_invalid("{'value':1}"),
           "a single-quoted key is rejected");
    expect(request_is_invalid("{\"value\":1 /* comment */}"),
           "a comment is rejected");
    expect(request_is_invalid("{\"value\":tru}"),
           "an incomplete literal is rejected");

    char nul_suffix[] = "{\"value\":1}\0{}";
    struct doc document = { 0 };
    expect(parse_doc(nul_suffix, sizeof(nul_suffix) - 1, &document) != 0,
           "a raw NUL inside the input is rejected");
    free(document.tokens);

    for (int levels = MAX_JSON_DEPTH; levels <= MAX_JSON_DEPTH + 1; levels++) {
        char nested[2 * MAX_JSON_DEPTH + 32];
        size_t next = (size_t)snprintf(nested, sizeof(nested), "{\"value\":");
        memset(nested + next, '[', (size_t)levels - 1);
        next += (size_t)levels - 1;
        nested[next++] = '0';
        memset(nested + next, ']', (size_t)levels - 1);
        next += (size_t)levels - 1;
        nested[next++] = '}';
        nested[next] = '\0';
        expect(request_is_invalid(nested) == (levels > MAX_JSON_DEPTH),
               "nesting accepts the limit and rejects a deeper object");
    }
}

static void test_stop_request_identity(void)
{
    const char *valid = "{\"pid\":4321,\"start_time\":1234}";
    const char *invalid[] = {
        "{\"pid\":4321}",
        "{\"pid\":4321,\"start_time\":0}",
        "{\"pid\":4321,\"start_time\":-1}",
        "{\"pid\":4321,\"start_time\":\"1234\"}",
        "{\"pid\":4321,\"start_time\":1234,\"pid\":4321}",
    };
    int pid;
    uint64_t start_time;
    expect(parse_stop_identity(valid, strlen(valid), &pid, &start_time) == 0 &&
               pid == 4321 && start_time == 1234,
           "stop requires both a PID and a positive Unix start time");
    for (size_t i = 0; i < sizeof(invalid) / sizeof(invalid[0]); i++)
        expect(parse_stop_identity(invalid[i], strlen(invalid[i]), &pid, &start_time) == EXIT_INPUT,
               "stop rejects a missing or invalid process start time");
}

static void test_legacy_policy_state(void)
{
    char template[] = "/tmp/vpnrouter-legacy-XXXXXX";
    char *directory = mkdtemp(template);
    expect(directory != NULL, "legacy fixture directory is available");
    if (directory == NULL)
        return;

    char path[PATH_MAX];
    int written = snprintf(path, sizeof(path), "%s/vpnrouter", directory);
    expect(written > 0 && (size_t)written < sizeof(path),
           "legacy fixture path fits");
    expect(legacy_policy_state_path(path) == LEGACY_ABSENT,
           "legacy state distinguishes absence");

    FILE *file = fopen(path, "w");
    expect(file != NULL, "legacy fixture file is created");
    if (file != NULL)
        fclose(file);
    expect(legacy_policy_state_path(path) == LEGACY_PRESENT,
           "legacy state distinguishes presence");
    unlink(path);

    if (geteuid() != 0) {
        chmod(directory, 0);
        expect(legacy_policy_state_path(path) == LEGACY_UNKNOWN,
               "legacy state distinguishes an unreadable path from absence");
        chmod(directory, 0700);
    }
    rmdir(directory);
}

static void test_root_directory_metadata(void)
{
    struct stat st = {0};
    st.st_mode = S_IFDIR | 0755 | S_ISVTX;
    expect(root_directory_stat_is_valid(&st, 0755, 1),
           "the protected helper directory may have the sticky bit");
    st.st_gid = 80;
    expect(root_directory_stat_is_valid(&st, 0755, 0),
           "root:admin Application Support is accepted with protected modes");
    expect(!root_directory_stat_is_valid(&st, 0755, 1),
           "directories requiring wheel still reject a different group");
    st.st_mode = S_IFDIR | 0775;
    expect(!root_directory_stat_is_valid(&st, 0755, 0),
           "a group-writable system directory is rejected");
    st.st_mode = S_IFDIR | 0755 | S_ISGID;
    expect(!root_directory_stat_is_valid(&st, 0755, 0),
           "a setgid system directory is rejected");
    st.st_mode = S_IFDIR | 0755 | S_ISUID;
    expect(!root_directory_stat_is_valid(&st, 0755, 0),
           "a setuid system directory is rejected");
    st.st_mode = S_IFDIR | 0755;
    st.st_uid = 501;
    expect(!root_directory_stat_is_valid(&st, 0755, 0),
           "a user-owned system directory is rejected");
}

static void test_session_locks(void)
{
    char path[PATH_MAX];
    expect(session_lock_path(path, sizeof(path), 501) == 0 &&
               strcmp(path, ROOT_RUN "/501/session.lock") == 0,
           "session lease path is per-uid");
    expect(state_lock_path(path, sizeof(path), 501) == 0 &&
               strcmp(path, ROOT_RUN "/501/state.lock") == 0,
           "state lock path is per-uid");

    char template[] = "/tmp/vpnrouter-lock-XXXXXX";
    int first = mkstemp(template);
    expect(first >= 0, "synthetic lock file is available");
    if (first < 0)
        return;
    int second = open(template, O_RDWR);
    expect(second >= 0, "second synthetic lock handle is available");
    if (second < 0) {
        close(first);
        unlink(template);
        return;
    }

    expect(try_lock_exclusive(first) == 0,
           "first exclusive kernel lock succeeds");
    errno = 0;
    expect(try_lock_exclusive(second) != 0 && errno == EWOULDBLOCK,
           "a concurrent exclusive start cannot enter the state lock");
    close(first);
    expect(try_lock_exclusive(second) == 0,
           "the kernel lock is released when the owner closes or exits");
    close(second);
    unlink(template);
}

static void test_legacy_process_detection(void)
{
    char path[PATH_MAX];
    expect(legacy_runtime_path("/Users/alice", path, sizeof(path)) == 0 &&
               strcmp(path, "/Users/alice/Library/Application Support/VPNRouter/bin/sing-box") == 0,
           "legacy runtime path is derived from the exact user home");
    expect(legacy_process_identity(0, path, path) == 1,
           "root-owned exact legacy runtime is detected");
    expect(legacy_process_identity(501, path, path) == 0,
           "user-owned exact path is not a privileged legacy process");
    expect(legacy_process_identity(0, "/Users/alice/other/sing-box", path) == 0,
           "a different executable is not a legacy runtime match");
    expect(find_legacy_runtime_process(NULL) < 0,
           "missing home is not treated as legacy absence");
    expect(find_legacy_runtime_process("/nonexistent-vpnrouter-home") >= 0,
           "passive process scan returns a known result");
}

static void test_controller_secret(void)
{
    const char *secret = "0123456789abcdef0123456789abcdef";
    char json[1024];
    snprintf(json, sizeof(json),
             "{\"log\":{\"output\":\"\"},"
             "\"experimental\":{\"clash_api\":{\"external_controller\":\"127.0.0.1:9090\","
             "\"secret\":\"%s\"}},"
             "\"inbounds\":[{\"type\":\"tun\"}],"
             "\"outbounds\":[{\"type\":\"direct\"}]}", secret);
    expect(config_is_valid(json), "loopback controller with a long secret is accepted");

    snprintf(json, sizeof(json),
             "{\"log\":{\"output\":\"\"},"
             "\"experimental\":{\"clash_api\":{\"external_controller\":\"127.0.0.1:9090\"}},"
             "\"inbounds\":[{\"type\":\"tun\"}],"
             "\"outbounds\":[{\"type\":\"direct\"}]}");
    expect(!config_is_valid(json), "controller without a secret is rejected");

    snprintf(json, sizeof(json),
             "{\"log\":{\"output\":\"\"},"
             "\"experimental\":{\"clash_api\":{\"external_controller\":\"127.0.0.1:9090\","
             "\"secret\":\"short\"}},"
             "\"inbounds\":[{\"type\":\"tun\"}],"
             "\"outbounds\":[{\"type\":\"direct\"}]}");
    expect(!config_is_valid(json), "controller with a short secret is rejected");

    snprintf(json, sizeof(json),
             "{\"log\":{\"output\":\"\"},"
             "\"experimental\":{\"v2ray_api\":{}},"
             "\"inbounds\":[{\"type\":\"tun\"}],"
             "\"outbounds\":[{\"type\":\"direct\"}]}");
    expect(!config_is_valid(json), "unmanaged experimental APIs are rejected");
}

static void test_wide_object_keys(void)
{
    char json[16384] = "{";
    size_t next = 1;
    for (int i = 0; i < 512; i++)
        next += (size_t)snprintf(json + next, sizeof(json) - next,
                                "%s\"field%d\":0", i == 0 ? "" : ",", i);
    snprintf(json + next, sizeof(json) - next, "}");
    expect(!request_is_invalid(json), "a wide object with unique keys is accepted");
    snprintf(json + next, sizeof(json) - next, ",\"field0\":1}");
    expect(request_is_invalid(json), "a duplicate in a wide object is rejected");
}

static void test_structural_key_casing(void)
{
    expect(config_is_valid(
               "{\"log\":{\"output\":\"\"},"
               "\"inbounds\":[{\"type\":\"tun\"}],"
               "\"outbounds\":[{\"type\":\"direct\"}]}"),
           "canonical structural keys are accepted");
    expect(!config_is_valid(
               "{\"Log\":{\"output\":\"\"},"
               "\"inbounds\":[{\"type\":\"tun\"}],"
               "\"outbounds\":[{\"type\":\"direct\"}]}"),
           "a case-variant root structural key is rejected");
    expect(!config_is_valid(
               "{\"log\":{\"Output\":\"\"},"
               "\"inbounds\":[{\"type\":\"tun\"}],"
               "\"outbounds\":[{\"type\":\"direct\"}]}"),
           "a case-variant nested structural key is rejected");
    expect(!config_is_valid(
               "{\"log\":{\"output\":\"\"},"
               "\"Inbounds\":[{\"type\":\"tun\"}],"
               "\"inbounds\":[{\"type\":\"tun\"}],"
               "\"outbounds\":[{\"type\":\"direct\"}]}"),
           "a case-variant key is rejected beside its canonical spelling");
    expect(!config_is_valid(
               "{\"log\":{\"output\":\"\"},"
               "\"inbounds\":[{\"type\":\"tun\"}],"
               "\"route\":{\"Rule_Set\":[]},"
               "\"outbounds\":[{\"type\":\"direct\"}]}"),
           "a case-variant rule_set key is rejected");
    expect(config_is_valid(
               "{\"log\":{\"output\":\"\"},"
               "\"inbounds\":[{\"type\":\"tun\"}],"
               "\"outbounds\":[{\"type\":\"vless\",\"transport\":{"
               "\"type\":\"ws\",\"headers\":{\"X-Trace\":\"MiXeD Inbounds\"}}}]}"),
           "case-sensitive HTTP header names and values are preserved");
    expect(config_is_valid(
               "{\"log\":{\"output\":\"\"},"
               "\"inbounds\":[{\"type\":\"tun\",\"auto_route\":true,\"mtu\":9000}],"
               "\"outbounds\":[{\"type\":\"direct\"}],"
               "\"route\":{\"auto_detect_interface\":false}}"),
           "ordinary scalar values in a generated-style config are accepted");
}

static void test_runtime_components(void)
{
    expect(config_is_valid(
               "{\"inbounds\":[{\"type\":\"tun\"}],"
               "\"outbounds\":[{\"type\":\"dnstt\"},{\"type\":\"ssh\"},"
               "{\"type\":\"direct\"},{\"type\":\"urltest\"}],"
               "\"endpoints\":[{\"type\":\"wireguard\"}],"
               "\"dns\":{\"servers\":[{\"type\":\"https\"},{\"type\":\"local\"}]}}"),
           "generated VPN, WireGuard and DNS components are accepted");

    const char *const rejected[] = {
        "\"outbounds\":[{\"type\":\"fixture-unsupported\"}]",
        "\"outbounds\":[{\"type\":\"direct\",\"private_key_path\":\"\"}]",
        "\"outbounds\":[{\"type\":\"direct\",\"Protect_Path\":\"\"}]",
        "\"outbounds\":[{\"type\":\"direct\",\"type\":\"direct\"}]",
        "\"Outbounds\":[{\"type\":\"direct\"}]",
        "\"outbounds\":[{\"Type\":\"direct\"}]",
        "\"outbounds\":[{}]",
        "\"outbounds\":{}",
        "\"endpoints\":[{\"type\":\"fixture-unsupported\"}]",
        "\"dns\":{\"servers\":[{\"type\":\"fixture-unsupported\"}]}",
        "\"dns\":{\"Servers\":[{\"type\":\"https\"}]}",
        "\"dns\":null",
        "\"certificate_providers\":[]",
        "\"route\":{\"dhcp_lease_files\":[]}",
        "\"experimental\":{\"debug\":{}}",
    };
    for (size_t i = 0; i < sizeof(rejected) / sizeof(rejected[0]); i++) {
        char json[256];
        snprintf(json, sizeof(json), "{\"inbounds\":[{\"type\":\"tun\"}],%s}", rejected[i]);
        expect(!config_is_valid(json), rejected[i]);
    }
}

static int request_op(const char *json, char *out, size_t capacity)
{
    char *copy = strdup(json);
    if (copy == NULL)
        return -1;
    struct doc document = { NULL, 0, NULL, 0 };
    int result = -1;
    if (parse_doc(copy, strlen(copy), &document) == 0 &&
        validate_unique_keys(&document, 0) == 0 &&
        json_string(&document, 0, "op", out, capacity) == 0)
        result = 0;
    free(document.tokens);
    free(copy);
    return result;
}

int main(void)
{
    test_json_structure();
    test_stop_request_identity();
    test_legacy_policy_state();
    test_session_locks();
    test_legacy_process_detection();
    test_root_directory_metadata();
    test_controller_secret();
    test_wide_object_keys();
    test_structural_key_casing();
    test_runtime_components();

    const char *valid =
        "{\"log\":{\"output\":\"\"},"
        "\"inbounds\":[{\"type\":\"tun\",\"tag\":\"tun-in\"}],"
        "\"outbounds\":[{\"type\":\"direct\",\"tag\":\"direct\"}],"
        "\"route\":{\"final\":\"direct\"}}";
    expect(config_is_valid(valid), "minimal privileged config is accepted");

    expect(!config_is_valid(
               "{\"log\":{\"output\":\"/tmp/singbox.log\"},"
               "\"inbounds\":[{\"type\":\"tun\"}],"
               "\"outbounds\":[{\"type\":\"direct\"}]}"),
           "root log output is rejected");
    expect(!config_is_valid(
               "{\"log\":{\"output\":\"\"},"
               "\"inbounds\":[{\"type\":\"mixed\"}],"
               "\"outbounds\":[{\"type\":\"direct\"}]}"),
           "non-TUN inbound is rejected");
    expect(!config_is_valid(
               "{\"log\":{\"output\":\"\"},"
               "\"inbounds\":[{\"type\":\"tun\"}],"
               "\"experimental\":{\"cache_file\":\"/tmp/cache.db\","
               "\"clash_api\":{\"external_controller\":\"127.0.0.1:9090\"}},"
               "\"outbounds\":[{\"type\":\"direct\"}]}"),
           "user cache path is rejected");
    expect(!config_is_valid(
               "{\"log\":{\"output\":\"\"},"
               "\"inbounds\":[{\"type\":\"tun\"}],"
               "\"experimental\":{\"clash_api\":{\"external_controller\":\"0.0.0.0:9090\"}},"
               "\"outbounds\":[{\"type\":\"direct\"}]}"),
           "non-loopback Clash controller is rejected");
    expect(!config_is_valid(
               "{\"log\":{\"output\":\"\"},\"inbounds\":[{\"type\":\"tun\"}],"
               "\"outbounds\":[{\"type\":\"direct\",\"tls\":{\"certificate_path\":\"/tmp/cert.pem\"}}]}"),
           "certificate path is rejected");
    expect(!config_is_valid(
               "{\"log\":{\"output\":\"\"},\"inbounds\":[{\"type\":\"tun\"}],"
               "\"outbounds\":[{\"type\":\"direct\",\"output\":\"x\"}]}"),
           "output outside log is rejected");
    expect(!config_is_valid(
               "{\"log\":{\"output\":\"\"},\"inbounds\":[{\"type\":\"tun\"}],"
               "\"outbounds\":[{\"type\":\"direct\"}],"
               "\"route\":{\"rule_set\":[{\"type\":\"remote\",\"format\":\"binary\",\"path\":\"/tmp/a.srs\"}]}}"),
           "non-local rule set is rejected");

    expect(request_is_invalid(
               "{\"version\":1,\"op\":\"probe\",\"op\":\"start\"}"),
           "duplicate request keys are rejected");
    expect(!request_is_invalid("{\"version\":1,\"op\":\"probe\"}"),
           "unique request keys are accepted");
    expect(request_is_invalid("{\"version\":1,\"o\\u0070\":\"probe\"}"),
           "escaped request key is rejected");
    expect(request_is_invalid(
               "{\"version\":1,\"op\":\"probe\",\"o\\u0070\":\"start\"}"),
           "escaped duplicate request key is rejected");
    expect(!config_is_valid(
               "{\"log\":{\"output\":\"\"},"
               "\"inb\\u006funds\":[{\"type\":\"tun\"}],"
               "\"outbounds\":[{\"type\":\"direct\"}]}"),
           "escaped object key is rejected");
    expect(!config_is_valid(
               "{\"inbounds\":[{\"type\":\"tun\"}],"
               "\"inb\\u006funds\":[{\"type\":\"tun\"}]}"),
           "duplicate after key decoding is rejected");
    expect(!config_is_valid(
               "{\"log\":{\"output\":\"\",\"output\":\"\"},"
               "\"inbounds\":[{\"type\":\"tun\"}]}"),
           "nested duplicate keys are rejected");
    expect(!config_is_valid(
               "{\"log\":{\"output\":\"\"},"
               "\"inbounds\":[{\"type\":\"tun\",\"type\":\"tun\"}],"
               "\"outbounds\":[{\"type\":\"direct\"}]}"),
           "duplicate keys inside array objects are rejected");
    expect(!config_is_valid(
               "{\"log\":{\"output\":\"\"},"
               "\"inb\\u00e9unds\":[{\"type\":\"tun\"}]}"),
           "non-ASCII escaped key is rejected");
    expect(!config_is_valid(
               "{\"log\":{\"output\":\"\"},"
               "\"inb\xc3\xa9unds\":[{\"type\":\"tun\"}]}"),
           "raw non-ASCII key is rejected");
    expect(!config_is_valid(
               "{\"log\":{\"output\":\"\"},"
               "\"bad\tkey\":1,"
               "\"inbounds\":[{\"type\":\"tun\"}]}"),
           "control character in key is rejected");
    expect(!config_is_valid(
               "{\"\":1,\"inbounds\":[{\"type\":\"tun\"}]}"),
           "empty key is rejected");

    char long_key[256];
    memset(long_key, 'k', 200);
    long_key[200] = '\0';
    char long_request[256];
    snprintf(long_request, sizeof(long_request), "{\"%s\":1}", long_key);
    expect(!request_is_invalid(long_request), "long canonical key is accepted");

    char op[16];
    expect(request_op("{\"version\":1,\"op\":\"pro\\u0062e\"}", op, sizeof(op)) == 0 &&
               strcmp(op, "probe") == 0,
           "escaped ASCII value is still decoded");
    expect(request_op("{\"version\":1,\"op\":\"pro\xc3\xa9\"}", op, sizeof(op)) == 0 &&
               strcmp(op, "pro\xc3\xa9") == 0,
           "raw UTF-8 value is preserved");

    expect(is_loopback_controller("127.0.0.1:9090"), "IPv4 loopback controller accepted");
    expect(is_loopback_controller("[::1]:9090"), "IPv6 loopback controller accepted");
    expect(!is_loopback_controller("0.0.0.0:9090"), "wildcard controller rejected");
    expect(!is_loopback_controller("127.0.0.1.example:9090"), "lookalike host rejected");

    expect(validate_pf_rules(
               "block drop out all\n"
               "pass out quick on lo0 all\n"
               "pass out quick inet from any to 10.0.0.0/8\n"
               "pass out quick inet6 from any to fe80::/10\n") == 0,
           "generated pf grammar is accepted");
    expect(validate_pf_rules("include \"/etc/pf.conf\"\n") != 0,
           "pf include is rejected");
    expect(validate_pf_rules("pass out quick inet from any to 10.0.0.0/33\n") != 0,
           "invalid pf prefix is rejected");

    if (failures != 0) {
        fprintf(stderr, "%d contract test(s) failed\n", failures);
        return 1;
    }
    printf("vpnrouter-privileged contract tests passed\n");
    return 0;
}
