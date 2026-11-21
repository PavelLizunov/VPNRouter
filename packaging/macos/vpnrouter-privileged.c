/*
 * VPNRouter macOS privileged helper.
 *
 * This binary is the only command authorized by the managed sudoers policy.
 * It reads one typed JSON request from stdin, validates the invoking user and
 * every input, then performs one fixed operation. It never invokes a shell and
 * never accepts a path or command line from argv.
 *
 * Contract version: 1. See packaging/macos/README.md.
 */

#define _DARWIN_C_SOURCE

#include <sys/types.h>
#include <sys/stat.h>
#include <sys/time.h>
#include <sys/sysctl.h>
#include <sys/proc_info.h>
#include <sys/wait.h>
#include <sys/file.h>

#include <CommonCrypto/CommonDigest.h>
#include <arpa/inet.h>
#include <ctype.h>
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <libproc.h>
#include <limits.h>
#include <poll.h>
#include <pwd.h>
#include <signal.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

#include "jsmn.h"

#define HELPER_PATH "/Library/PrivilegedHelperTools/com.vpnrouter.privileged"
#define ROOT_DIR "/Library/Application Support/VPNRouter"
#define ROOT_BIN ROOT_DIR "/bin/sing-box"
#define ROOT_RUN ROOT_DIR "/run"
#define ROOT_ASSETS ROOT_DIR "/assets"
#define MANIFEST_PATH ROOT_DIR "/install.json"
#define SUDOERS_DIR "/private/etc/sudoers.d"
#define LEGACY_POLICY_PATH SUDOERS_DIR "/vpnrouter"
#define POLICY_HEADER "# vpnrouter policy v2"
#define PF_ANCHOR "com.vpnrouter/killswitch"
#define PF_MAIN_CONF "/etc/pf.conf"
#define PF_MAIN_STAGED ROOT_RUN "/pf-main.conf"
#define PF_RULES_STAGED ROOT_RUN "/pf.rules"
#define PF_TOKEN_PATH ROOT_RUN "/pf.token"
#define SESSION_PATH_FMT ROOT_RUN "/%u/session.json"
#define SESSION_LOCK_FMT ROOT_RUN "/%u/session.lock"
#define STATE_LOCK_FMT ROOT_RUN "/%u/state.lock"
#define HOST_NETWORK_LOCK_PATH ROOT_RUN "/host-network.lock"
#define HOST_SESSION_PATH ROOT_RUN "/host-session"
#define HOST_SESSION_VERSION 3
#define LAUNCH_ID_SIZE 33
#define DNS_STATE_PATH ROOT_RUN "/dns-state"
#define DNS_SNAPSHOT_VERSION 1
#define MAX_DNS_SERVERS 8
#define MAX_PROCESSES 16384
#define STATE_LOCK_WAIT_STEPS 500
#define STATE_LOCK_WAIT_USEC 10000

#define MAX_REQUEST_BYTES (1024u * 1024u)
#define MAX_CONFIG_BYTES (1024u * 1024u)
#define MAX_ASSET_BYTES (64u * 1024u * 1024u)
#define MAX_MANIFEST_BYTES (64u * 1024u)
#define MAX_PF_RULES_BYTES (64u * 1024u)
#define MAX_COMMAND_OUTPUT (1024u * 1024u)
#define MAX_TOKEN_COUNT (262144u)
#define MAX_JSON_DEPTH 64

#define EXIT_USAGE 64
#define EXIT_INPUT 65
#define EXIT_STATE 66
#define EXIT_COMMAND 67

struct doc {
    char *json;
    size_t json_len;
    jsmntok_t *tokens;
    int count;
};

enum pf_release_mode {
    PF_RELEASE_NONE,
    PF_RELEASE_ANCHOR,
    PF_RELEASE_DEFAULT,
};

struct host_session {
    uint32_t version;
    uint32_t uid;
    uint64_t start_time;
    uint64_t boot_time;
    int32_t pid;
    uint32_t pf_mode;
    uint64_t pf_reference;
    char launch_id[LAUNCH_ID_SIZE];
};

enum legacy_policy_state {
    LEGACY_ABSENT = 0,
    LEGACY_PRESENT = 1,
    LEGACY_UNKNOWN = -1,
};

static int verify_fixed_chain(void);
static int verify_runtime_artifact(void);
static int process_is_root_runtime(int pid, uint64_t start_time);
static int read_session_record(uid_t uid, int *pid, uint64_t *start_time);
static int write_session_record(uid_t uid, int pid, uint64_t start_time);
static int delete_session_record(uid_t uid);
static int command_dns_flush(void);
static int parse_stop_identity(const char *json, size_t length, int *pid,
                               uint64_t *start_time);
static int session_lock_path(char *path, size_t capacity, uid_t uid);
static int state_lock_path(char *path, size_t capacity, uid_t uid);
static int try_lock_exclusive(int fd);
static int acquire_state_lock(uid_t uid);
static int acquire_session_lease(uid_t uid);
static int legacy_runtime_path(const char *home, char *path, size_t capacity);
static int legacy_process_identity(uid_t owner, const char *executable,
                                   const char *expected_path);
static int find_legacy_runtime_process(const char *home);
static void require_no_legacy_runtime(const char *home);

static void fail(int code, const char *message) __attribute__((noreturn));

static void fail(int code, const char *message)
{
    fprintf(stderr, "vpnrouter-privileged: %s\n", message);
    exit(code);
}

static void *xcalloc(size_t count, size_t size)
{
    void *p = calloc(count, size);
    if (p == NULL)
        fail(EXIT_STATE, "out of memory");
    return p;
}

static int read_fd_all(int fd, char *buffer, size_t capacity, size_t *length)
{
    size_t used = 0;
    while (used < capacity) {
        ssize_t n = read(fd, buffer + used, capacity - used);
        if (n < 0) {
            if (errno == EINTR)
                continue;
            return -1;
        }
        if (n == 0)
            break;
        used += (size_t)n;
    }
    if (used == capacity)
        return -1;
    buffer[used] = '\0';
    *length = used;
    return 0;
}

static int read_stdin_all(char *buffer, size_t capacity, size_t *length)
{
    return read_fd_all(STDIN_FILENO, buffer, capacity, length);
}

static int read_file_checked(const char *path, uid_t owner, mode_t allowed_bits,
                             size_t capacity, char *buffer, size_t *length)
{
    int fd = open(path, O_RDONLY | O_NOFOLLOW | O_CLOEXEC);
    if (fd < 0)
        return -1;

    struct stat st;
    if (fstat(fd, &st) != 0 || !S_ISREG(st.st_mode) ||
        st.st_uid != owner || (st.st_mode & allowed_bits) != 0 ||
        st.st_size <= 0 || (uint64_t)st.st_size >= (uint64_t)capacity) {
        close(fd);
        return -1;
    }

    size_t used = 0;
    while (used < capacity) {
        ssize_t n = read(fd, buffer + used, capacity - used);
        if (n < 0) {
            if (errno == EINTR)
                continue;
            close(fd);
            return -1;
        }
        if (n == 0)
            break;
        used += (size_t)n;
    }
    close(fd);
    if (used != (size_t)st.st_size || used == capacity)
        return -1;
    buffer[used] = '\0';
    *length = used;
    return 0;
}

static int legacy_policy_state_path(const char *path)
{
    struct stat st;
    if (lstat(path, &st) == 0)
        return LEGACY_PRESENT;
    if (errno == ENOENT || errno == ENOTDIR)
        return LEGACY_ABSENT;
    return LEGACY_UNKNOWN;
}

static int root_directory_stat_is_valid(const struct stat *st, mode_t expected_mode,
                                        int require_wheel)
{
    /* macOS may set the sticky bit on PrivilegedHelperTools. */
    return S_ISDIR(st->st_mode) && st->st_uid == 0 &&
           (!require_wheel || st->st_gid == 0) &&
           (st->st_mode & (07777 & ~S_ISVTX)) == expected_mode;
}

static int verify_root_dir(const char *path, mode_t expected_mode, int require_wheel)
{
    struct stat st;
    return lstat(path, &st) == 0 &&
           root_directory_stat_is_valid(&st, expected_mode, require_wheel) ? 0 : -1;
}

static int verify_etc_alias(void)
{
    struct stat st;
    if (lstat("/etc", &st) != 0 || !S_ISLNK(st.st_mode) || st.st_uid != 0)
        return -1;
    char target[32];
    ssize_t length = readlink("/etc", target, sizeof(target) - 1);
    if (length < 0)
        return -1;
    target[length] = '\0';
    return strcmp(target, "private/etc") == 0 || strcmp(target, "/private/etc") == 0 ? 0 : -1;
}

static int verify_fixed_chain(void)
{
    return verify_etc_alias() == 0 &&
           verify_root_dir("/Library", 0755, 1) == 0 &&
           verify_root_dir("/Library/Application Support", 0755, 0) == 0 &&
           verify_root_dir("/Library/PrivilegedHelperTools", 0755, 1) == 0 &&
           verify_root_dir("/private", 0755, 1) == 0 &&
           verify_root_dir("/private/etc", 0755, 1) == 0 &&
           verify_root_dir(SUDOERS_DIR, 0755, 1) == 0
               ? 0
               : -1;
}

static int ensure_root_dir(const char *path, mode_t mode)
{
    struct stat st;
    if (lstat(path, &st) != 0) {
        if (errno != ENOENT)
            return -1;
        if (mkdir(path, mode) != 0 && errno != EEXIST)
            return -1;
        if (lstat(path, &st) != 0)
            return -1;
    }
    if (!S_ISDIR(st.st_mode) || st.st_uid != 0 || st.st_gid != 0)
        return -1;
    if (chown(path, 0, 0) != 0)
        return -1;
    if (chmod(path, mode) != 0)
        return -1;
    return 0;
}

static int write_root_file(const char *path, const void *data, size_t length,
                           mode_t mode)
{
    char temp[PATH_MAX];
    int written = snprintf(temp, sizeof(temp), "%s.tmp.%ld", path, (long)getpid());
    if (written < 0 || (size_t)written >= sizeof(temp))
        return -1;

    int fd = open(temp, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0600);
    if (fd < 0)
        return -1;

    if (fchown(fd, 0, 0) != 0 || fchmod(fd, mode) != 0) {
        close(fd);
        unlink(temp);
        return -1;
    }

    size_t offset = 0;
    const char *bytes = (const char *)data;
    while (offset < length) {
        ssize_t n = write(fd, bytes + offset, length - offset);
        if (n < 0) {
            if (errno == EINTR)
                continue;
            close(fd);
            unlink(temp);
            return -1;
        }
        offset += (size_t)n;
    }
    if (fsync(fd) != 0) {
        close(fd);
        unlink(temp);
        return -1;
    }
    if (close(fd) != 0) {
        unlink(temp);
        return -1;
    }
    if (rename(temp, path) != 0) {
        unlink(temp);
        return -1;
    }
    return 0;
}

static int sha256_bytes(const void *data, size_t length, char hex[65])
{
    unsigned char digest[CC_SHA256_DIGEST_LENGTH];
    CC_SHA256(data, (CC_LONG)length, digest);
    static const char digits[] = "0123456789abcdef";
    for (int i = 0; i < CC_SHA256_DIGEST_LENGTH; i++) {
        hex[i * 2] = digits[digest[i] >> 4];
        hex[i * 2 + 1] = digits[digest[i] & 0x0f];
    }
    hex[64] = '\0';
    return 0;
}

static int sha256_file(const char *path, char hex[65])
{
    int fd = open(path, O_RDONLY | O_NOFOLLOW | O_CLOEXEC);
    if (fd < 0)
        return -1;
    CC_SHA256_CTX ctx;
    CC_SHA256_Init(&ctx);
    unsigned char buffer[64 * 1024];
    for (;;) {
        ssize_t n = read(fd, buffer, sizeof(buffer));
        if (n < 0) {
            if (errno == EINTR)
                continue;
            close(fd);
            return -1;
        }
        if (n == 0)
            break;
        CC_SHA256_Update(&ctx, buffer, (CC_LONG)n);
    }
    close(fd);
    unsigned char digest[CC_SHA256_DIGEST_LENGTH];
    CC_SHA256_Final(digest, &ctx);
    static const char digits[] = "0123456789abcdef";
    for (int i = 0; i < CC_SHA256_DIGEST_LENGTH; i++) {
        hex[i * 2] = digits[digest[i] >> 4];
        hex[i * 2 + 1] = digits[digest[i] & 0x0f];
    }
    hex[64] = '\0';
    return 0;
}

static int constant_time_equal(const char *a, const char *b, size_t length)
{
    unsigned char diff = 0;
    for (size_t i = 0; i < length; i++)
        diff |= (unsigned char)(a[i] ^ b[i]);
    return diff == 0;
}

/*
 * jsmn is intentionally lenient about syntax when JSMN_STRICT is not set, so
 * the helper validates the RFC 8259 grammar itself before tokenization. This
 * also makes the nesting limit reliable: the strict parser bounds recursion at
 * MAX_JSON_DEPTH containers, so the token walkers that recurse later cannot
 * exceed it. String values keep their raw bytes; only their grammar is checked.
 */
struct json_cursor {
    const char *json;
    size_t length;
    size_t position;
};

static void json_skip_whitespace(struct json_cursor *cursor)
{
    while (cursor->position < cursor->length) {
        char byte = cursor->json[cursor->position];
        if (byte != ' ' && byte != '\t' && byte != '\n' && byte != '\r')
            break;
        cursor->position++;
    }
}

static int json_parse_value(struct json_cursor *cursor, int depth);

static int json_parse_string(struct json_cursor *cursor)
{
    if (cursor->position >= cursor->length ||
        cursor->json[cursor->position] != '"')
        return -1;
    cursor->position++;
    while (cursor->position < cursor->length) {
        unsigned char byte = (unsigned char)cursor->json[cursor->position++];
        if (byte == '"')
            return 0;
        if (byte < 0x20)
            return -1;
        if (byte != '\\')
            continue;
        if (cursor->position >= cursor->length)
            return -1;
        unsigned char escape = (unsigned char)cursor->json[cursor->position++];
        switch (escape) {
        case '"':
        case '\\':
        case '/':
        case 'b':
        case 'f':
        case 'n':
        case 'r':
        case 't':
            break;
        case 'u':
            for (int digit = 0; digit < 4; digit++) {
                if (cursor->position >= cursor->length)
                    return -1;
                unsigned char hex = (unsigned char)cursor->json[cursor->position];
                if (!((hex >= '0' && hex <= '9') ||
                      (hex >= 'a' && hex <= 'f') ||
                      (hex >= 'A' && hex <= 'F')))
                    return -1;
                cursor->position++;
            }
            break;
        default:
            return -1;
        }
    }
    return -1;
}

static int json_parse_digits(struct json_cursor *cursor)
{
    size_t start = cursor->position;
    while (cursor->position < cursor->length &&
           cursor->json[cursor->position] >= '0' &&
           cursor->json[cursor->position] <= '9')
        cursor->position++;
    return cursor->position > start ? 0 : -1;
}

static int json_parse_number(struct json_cursor *cursor)
{
    if (cursor->position < cursor->length &&
        cursor->json[cursor->position] == '-')
        cursor->position++;
    if (cursor->position >= cursor->length)
        return -1;

    char first = cursor->json[cursor->position];
    if (first == '0') {
        cursor->position++;
    } else if (first >= '1' && first <= '9') {
        cursor->position++;
        (void)json_parse_digits(cursor);
    } else {
        return -1;
    }

    if (cursor->position < cursor->length &&
        cursor->json[cursor->position] == '.') {
        cursor->position++;
        if (json_parse_digits(cursor) != 0)
            return -1;
    }
    if (cursor->position < cursor->length &&
        (cursor->json[cursor->position] == 'e' ||
         cursor->json[cursor->position] == 'E')) {
        cursor->position++;
        if (cursor->position < cursor->length &&
            (cursor->json[cursor->position] == '+' ||
             cursor->json[cursor->position] == '-'))
            cursor->position++;
        if (json_parse_digits(cursor) != 0)
            return -1;
    }
    return 0;
}

static int json_parse_literal(struct json_cursor *cursor, const char *literal)
{
    size_t literal_length = strlen(literal);
    if (cursor->length - cursor->position < literal_length)
        return -1;
    if (memcmp(cursor->json + cursor->position, literal, literal_length) != 0)
        return -1;
    cursor->position += literal_length;
    return 0;
}

static int json_parse_array(struct json_cursor *cursor, int depth)
{
    if (depth > MAX_JSON_DEPTH)
        return -1;
    cursor->position++;
    json_skip_whitespace(cursor);
    if (cursor->position < cursor->length &&
        cursor->json[cursor->position] == ']') {
        cursor->position++;
        return 0;
    }
    for (;;) {
        if (json_parse_value(cursor, depth + 1) != 0)
            return -1;
        json_skip_whitespace(cursor);
        if (cursor->position >= cursor->length)
            return -1;
        char byte = cursor->json[cursor->position];
        if (byte == ',') {
            cursor->position++;
            json_skip_whitespace(cursor);
            continue;
        }
        if (byte == ']') {
            cursor->position++;
            return 0;
        }
        return -1;
    }
}

static int json_parse_object(struct json_cursor *cursor, int depth)
{
    if (depth > MAX_JSON_DEPTH)
        return -1;
    cursor->position++;
    json_skip_whitespace(cursor);
    if (cursor->position < cursor->length &&
        cursor->json[cursor->position] == '}') {
        cursor->position++;
        return 0;
    }
    for (;;) {
        if (json_parse_string(cursor) != 0)
            return -1;
        json_skip_whitespace(cursor);
        if (cursor->position >= cursor->length ||
            cursor->json[cursor->position] != ':')
            return -1;
        cursor->position++;
        json_skip_whitespace(cursor);
        if (json_parse_value(cursor, depth + 1) != 0)
            return -1;
        json_skip_whitespace(cursor);
        if (cursor->position >= cursor->length)
            return -1;
        char byte = cursor->json[cursor->position];
        if (byte == ',') {
            cursor->position++;
            json_skip_whitespace(cursor);
            continue;
        }
        if (byte == '}') {
            cursor->position++;
            return 0;
        }
        return -1;
    }
}

static int json_parse_value(struct json_cursor *cursor, int depth)
{
    if (cursor->position >= cursor->length)
        return -1;
    switch (cursor->json[cursor->position]) {
    case '{':
        return json_parse_object(cursor, depth);
    case '[':
        return json_parse_array(cursor, depth);
    case '"':
        return json_parse_string(cursor);
    case 't':
        return json_parse_literal(cursor, "true");
    case 'f':
        return json_parse_literal(cursor, "false");
    case 'n':
        return json_parse_literal(cursor, "null");
    default:
        return json_parse_number(cursor);
    }
}

/* Accepts exactly one strict JSON value, optionally surrounded by whitespace. */
static int json_syntax_is_valid(const char *json, size_t length)
{
    struct json_cursor cursor = { json, length, 0 };
    json_skip_whitespace(&cursor);
    if (json_parse_value(&cursor, 1) != 0)
        return 0;
    json_skip_whitespace(&cursor);
    return cursor.position == length;
}

static int parse_doc(char *json, size_t length, struct doc *doc)
{
    if (!json_syntax_is_valid(json, length))
        return -1;

    jsmn_parser parser;
    jsmn_init(&parser);
    int count = jsmn_parse(&parser, json, length, NULL, 0);
    if (count < 1 || count > (int)MAX_TOKEN_COUNT)
        return -1;

    doc->tokens = xcalloc((size_t)count, sizeof(jsmntok_t));
    jsmn_init(&parser);
    int parsed = jsmn_parse(&parser, json, length, doc->tokens, (size_t)count);
    if (parsed != count)
        return -1;
    doc->json = json;
    doc->json_len = length;
    doc->count = count;
    if (doc->tokens[0].type != JSMN_OBJECT || doc->tokens[0].end <= 0 ||
        (size_t)doc->tokens[0].end > length)
        return -1;
    for (size_t i = (size_t)doc->tokens[0].end; i < length; i++) {
        char byte = json[i];
        if (byte != ' ' && byte != '\n' && byte != '\r' && byte != '\t')
            return -1;
    }
    return 0;
}

static int token_skip(const struct doc *doc, int index)
{
    if (index < 0 || index >= doc->count)
        return -1;
    int next = index + 1;
    if (doc->tokens[index].type == JSMN_OBJECT) {
        // jsmn counts object pairs in `size`; each pair is a key and a value.
        int pairs = doc->tokens[index].size;
        for (int pair = 0; pair < pairs; pair++) {
            next = token_skip(doc, next); // key
            if (next < 0)
                return -1;
            next = token_skip(doc, next); // value
            if (next < 0)
                return -1;
        }
    } else if (doc->tokens[index].type == JSMN_ARRAY) {
        int elements = doc->tokens[index].size;
        for (int element = 0; element < elements; element++) {
            next = token_skip(doc, next);
            if (next < 0)
                return -1;
        }
    }
    return next;
}

static int token_equal(const struct doc *doc, int index, const char *text)
{
    if (index < 0 || index >= doc->count || doc->tokens[index].type != JSMN_STRING)
        return 0;
    size_t length = strlen(text);
    size_t token_length = (size_t)(doc->tokens[index].end - doc->tokens[index].start);
    return length == token_length &&
           memcmp(doc->json + doc->tokens[index].start, text, length) == 0;
}

static int object_get(const struct doc *doc, int object, const char *key)
{
    if (object < 0 || object >= doc->count ||
        doc->tokens[object].type != JSMN_OBJECT)
        return -1;
    int index = object + 1;
    int pairs = doc->tokens[object].size;
    for (int pair = 0; pair < pairs; pair++) {
        int key_token = index;
        int value_token = index + 1;
        if (value_token >= doc->count)
            return -1;
        if (token_equal(doc, key_token, key))
            return value_token;
        index = token_skip(doc, value_token);
        if (index < 0)
            return -1;
    }
    return -1;
}

struct json_key {
    const char *bytes;
    size_t length;
};

static int compare_keys(const void *left, const void *right)
{
    const struct json_key *a = left;
    const struct json_key *b = right;
    size_t common = a->length < b->length ? a->length : b->length;
    int order = memcmp(a->bytes, b->bytes, common);
    return order != 0 ? order : (a->length > b->length) - (a->length < b->length);
}

static int object_has_duplicate_keys(const struct doc *doc, int object)
{
    int index = object + 1;
    int pairs = doc->tokens[object].size;
    if (pairs < 2)
        return 0;
    if (pairs > doc->count / 2)
        return 1;

    struct json_key *keys = xcalloc((size_t)pairs, sizeof(*keys));
    for (int pair = 0; pair < pairs; pair++) {
        if (index + 1 >= doc->count ||
            doc->tokens[index].type != JSMN_STRING) {
            free(keys);
            return 1;
        }
        const jsmntok_t *key = &doc->tokens[index];
        keys[pair].bytes = doc->json + key->start;
        keys[pair].length = (size_t)(key->end - key->start);
        index = token_skip(doc, index + 1);
        if (index < 0) {
            free(keys);
            return 1;
        }
    }
    qsort(keys, (size_t)pairs, sizeof(*keys), compare_keys);
    int duplicate = 0;
    for (int pair = 1; pair < pairs; pair++) {
        if (compare_keys(&keys[pair - 1], &keys[pair]) == 0) {
            duplicate = 1;
            break;
        }
    }
    free(keys);
    return duplicate;
}

/*
 * The helper compares object keys as raw bytes, while sing-box decodes JSON
 * escapes and binds struct fields case-insensitively. An escaped key such as
 * "outp\u0075t" is the same key to sing-box but a different token here, and
 * "Inbounds" binds the same field as "inbounds". Only canonical printable
 * ASCII keys are accepted, and reserved structural names must use their exact
 * lowercase spelling so the helper and sing-box cannot disagree about which
 * field a key selects. Non-structural field names are unaffected.
 */
static const char *const reserved_structural_keys[] = {
    "log", "output", "experimental", "cache_file", "services", "clash_api",
    "external_ui", "external_controller", "secret", "inbounds", "type", "route",
    "rule_set", "format", "path", "certificate_path", "key_path",
    "client_certificate_path", "client_key_path", "ech_file", "include",
    "geosite", "geoip", "v2ray_api", "external_ui_download_url",
    "external_ui_download_detour", "store_mode", "store_selected",
    "store_fake_ip", "store_fakeip", "cache_id", "dns", "servers",
    "endpoints", "outbounds", "debug", "plugin", "plugin_opts",
    "executable_path", "data_directory", "extra_args", "protect_path",
    "private_key_path", "config_path", "certificate_provider", "dhcp_lease_files"
};

static int key_token_is_canonical(const struct doc *doc, int token)
{
    if (token < 0 || token >= doc->count ||
        doc->tokens[token].type != JSMN_STRING)
        return 0;
    int start = doc->tokens[token].start;
    int end = doc->tokens[token].end;
    if (end <= start)
        return 0;
    for (int i = start; i < end; i++) {
        unsigned char c = (unsigned char)doc->json[i];
        if (c < 0x20 || c > 0x7e || c == '\\')
            return 0;
    }

    size_t length = (size_t)(end - start);
    for (size_t reserved = 0;
         reserved < sizeof(reserved_structural_keys) / sizeof(reserved_structural_keys[0]);
         reserved++) {
        const char *canonical = reserved_structural_keys[reserved];
        if (strlen(canonical) != length)
            continue;
        if (memcmp(doc->json + start, canonical, length) == 0)
            return 1;
        int variant = 1;
        for (size_t i = 0; i < length; i++) {
            unsigned char raw = (unsigned char)doc->json[start + (int)i];
            unsigned char lower =
                (raw >= 'A' && raw <= 'Z') ? (unsigned char)(raw - 'A' + 'a') : raw;
            if (lower != (unsigned char)canonical[i]) {
                variant = 0;
                break;
            }
        }
        if (variant)
            return 0;
    }
    return 1;
}

/* Validates canonical object keys and rejects duplicates at every depth. */
static int validate_unique_keys(const struct doc *doc, int token)
{
    if (token < 0 || token >= doc->count)
        return -1;
    if (doc->tokens[token].type == JSMN_OBJECT) {
        if (object_has_duplicate_keys(doc, token))
            return -1;
        int index = token + 1;
        int pairs = doc->tokens[token].size;
        for (int pair = 0; pair < pairs; pair++) {
            int key_token = index;
            int value_token = index + 1;
            if (value_token >= doc->count ||
                !key_token_is_canonical(doc, key_token) ||
                validate_unique_keys(doc, value_token) != 0)
                return -1;
            index = token_skip(doc, value_token);
            if (index < 0)
                return -1;
        }
    } else if (doc->tokens[token].type == JSMN_ARRAY) {
        int index = token + 1;
        int elements = doc->tokens[token].size;
        for (int element = 0; element < elements; element++) {
            if (index >= doc->count || validate_unique_keys(doc, index) != 0)
                return -1;
            index = token_skip(doc, index);
            if (index < 0)
                return -1;
        }
    }
    return 0;
}

static int json_unescape(const struct doc *doc, int token, char *out, size_t capacity)
{
    if (token < 0 || token >= doc->count ||
        doc->tokens[token].type != JSMN_STRING)
        return -1;
    int start = doc->tokens[token].start;
    int end = doc->tokens[token].end;
    size_t used = 0;
    for (int i = start; i < end; i++) {
        unsigned char c = (unsigned char)doc->json[i];
        if (c == '\\') {
            if (++i >= end)
                return -1;
            switch (doc->json[i]) {
            case '"': c = '"'; break;
            case '\\': c = '\\'; break;
            case '/': c = '/'; break;
            case 'b': c = '\b'; break;
            case 'f': c = '\f'; break;
            case 'n': c = '\n'; break;
            case 'r': c = '\r'; break;
            case 't': c = '\t'; break;
            case 'u': {
                if (i + 4 >= end)
                    return -1;
                unsigned int value = 0;
                for (int h = 0; h < 4; h++) {
                    char hex = doc->json[++i];
                    value <<= 4;
                    if (hex >= '0' && hex <= '9')
                        value |= (unsigned int)(hex - '0');
                    else if (hex >= 'a' && hex <= 'f')
                        value |= (unsigned int)(hex - 'a' + 10);
                    else if (hex >= 'A' && hex <= 'F')
                        value |= (unsigned int)(hex - 'A' + 10);
                    else
                        return -1;
                }
                if (value > 0x7f)
                    return -1;
                c = (unsigned char)value;
                break;
            }
            default:
                return -1;
            }
        }
        if (c == '\0' || used + 1 >= capacity)
            return -1;
        out[used++] = (char)c;
    }
    out[used] = '\0';
    return 0;
}

static int json_string(const struct doc *doc, int object, const char *key,
                       char *out, size_t capacity)
{
    int token = object_get(doc, object, key);
    if (token < 0)
        return -1;
    return json_unescape(doc, token, out, capacity);
}

static int json_int(const struct doc *doc, int object, const char *key, long *out)
{
    int token = object_get(doc, object, key);
    if (token < 0 || doc->tokens[token].type != JSMN_PRIMITIVE)
        return -1;
    char buffer[32];
    size_t length = (size_t)(doc->tokens[token].end - doc->tokens[token].start);
    if (length == 0 || length >= sizeof(buffer))
        return -1;
    memcpy(buffer, doc->json + doc->tokens[token].start, length);
    buffer[length] = '\0';
    char *end = NULL;
    errno = 0;
    long value = strtol(buffer, &end, 10);
    if (errno != 0 || end == NULL || *end != '\0')
        return -1;
    *out = value;
    return 0;
}

static int is_loopback_controller(const char *value)
{
    if (value == NULL || *value == '\0')
        return 0;

    const char *host_start = value;
    const char *host_end = NULL;
    const char *port = NULL;
    if (*value == '[') {
        const char *close = strchr(value, ']');
        if (close == NULL)
            return 0;
        host_start = value + 1;
        host_end = close;
        if (close[1] == ':')
            port = close + 2;
        else if (close[1] != '\0')
            return 0;
    } else {
        const char *colon = strrchr(value, ':');
        host_end = colon != NULL ? colon : value + strlen(value);
        if (colon != NULL)
            port = colon + 1;
    }

    if (port != NULL) {
        if (*port == '\0')
            return 0;
        for (const char *p = port; *p != '\0'; p++)
            if (!isdigit((unsigned char)*p))
                return 0;
    }

    size_t length = (size_t)(host_end - host_start);
    return (length == 9 && memcmp(host_start, "127.0.0.1", 9) == 0) ||
           (length == 3 && memcmp(host_start, "::1", 3) == 0) ||
           (length == 9 && memcmp(host_start, "localhost", 9) == 0);
}

static int is_root_asset_path(const char *path, uid_t uid)
{
    char prefix[PATH_MAX];
    int written = snprintf(prefix, sizeof(prefix), ROOT_ASSETS "/%u/", (unsigned)uid);
    if (written < 0 || (size_t)written >= sizeof(prefix))
        return 0;
    size_t prefix_length = (size_t)written;
    if (strncmp(path, prefix, prefix_length) != 0)
        return 0;
    const char *name = path + prefix_length;
    if (*name == '\0' || strchr(name, '/') != NULL || strcmp(name, ".") == 0 ||
        strcmp(name, "..") == 0)
        return 0;

    struct stat st;
    if (lstat(path, &st) != 0 || !S_ISREG(st.st_mode) || S_ISLNK(st.st_mode) ||
        st.st_uid != 0 || (st.st_mode & 0077) != 0 || st.st_size < 8 ||
        st.st_size > (off_t)MAX_ASSET_BYTES)
        return 0;

    int fd = open(path, O_RDONLY | O_NOFOLLOW | O_CLOEXEC);
    if (fd < 0)
        return 0;
    unsigned char header[4] = {0};
    ssize_t n = read(fd, header, sizeof(header));
    close(fd);
    return n == (ssize_t)sizeof(header) && header[0] == 'S' && header[1] == 'R' &&
           header[2] == 'S' && header[3] >= 1 && header[3] <= 5;
}

static int validate_log(const struct doc *doc, int root)
{
    int log = object_get(doc, root, "log");
    if (log < 0)
        return 0;
    if (doc->tokens[log].type != JSMN_OBJECT)
        return -1;
    int output = object_get(doc, log, "output");
    if (output < 0)
        return 0;
    char value[PATH_MAX];
    if (json_unescape(doc, output, value, sizeof(value)) != 0)
        return -1;
    return value[0] == '\0' ? 0 : -1;
}

static int validate_experimental(const struct doc *doc, int root)
{
    int experimental = object_get(doc, root, "experimental");
    if (experimental < 0)
        return 0;
    if (doc->tokens[experimental].type != JSMN_OBJECT)
        return -1;

    int services = object_get(doc, experimental, "services");
    if (services >= 0) {
        if (doc->tokens[services].type == JSMN_ARRAY ||
            doc->tokens[services].type == JSMN_OBJECT) {
            if (doc->tokens[services].size != 0)
                return -1;
        } else {
            return -1;
        }
    }

    int clash = object_get(doc, experimental, "clash_api");
    if (clash < 0)
        return 0;
    if (doc->tokens[clash].type != JSMN_OBJECT)
        return -1;

    int ui = object_get(doc, clash, "external_ui");
    if (ui >= 0) {
        char value[PATH_MAX];
        if (json_unescape(doc, ui, value, sizeof(value)) != 0 || value[0] != '\0')
            return -1;
    }

    int controller = object_get(doc, clash, "external_controller");
    if (controller < 0) {
        return -1;
    }
    {
        char value[PATH_MAX];
        if (json_unescape(doc, controller, value, sizeof(value)) != 0 ||
            !is_loopback_controller(value))
            return -1;
    }

    int secret = object_get(doc, clash, "secret");
    if (secret < 0)
        return -1;
    {
        char value[160];
        if (json_unescape(doc, secret, value, sizeof(value)) != 0)
            return -1;
        size_t length = strlen(value);
        if (length < 32 || length > 128)
            return -1;
        for (size_t i = 0; i < length; i++) {
            unsigned char c = (unsigned char)value[i];
            if (!isalnum(c) && c != '_' && c != '-' && c != '.' && c != '~')
                return -1;
        }
    }
    return 0;
}

static int validate_inbounds(const struct doc *doc, int root)
{
    int inbounds = object_get(doc, root, "inbounds");
    if (inbounds < 0 || doc->tokens[inbounds].type != JSMN_ARRAY ||
        doc->tokens[inbounds].size < 1)
        return -1;
    int index = inbounds + 1;
    int elements = doc->tokens[inbounds].size;
    for (int element = 0; element < elements; element++) {
        if (index >= doc->count || doc->tokens[index].type != JSMN_OBJECT)
            return -1;
        char type[64];
        if (json_string(doc, index, "type", type, sizeof(type)) != 0 ||
            strcmp(type, "tun") != 0)
            return -1;
        index = token_skip(doc, index);
        if (index < 0)
            return -1;
    }
    return 0;
}

static int validate_component_types(const struct doc *doc, int array,
                                    const char *const *allowed_types)
{
    if (array < 0)
        return 0;
    if (doc->tokens[array].type != JSMN_ARRAY)
        return -1;

    int index = array + 1;
    for (int element = 0; element < doc->tokens[array].size; element++) {
        char type[64];
        if (index >= doc->count || doc->tokens[index].type != JSMN_OBJECT ||
            json_string(doc, index, "type", type, sizeof(type)) != 0)
            return -1;

        const char *const *allowed = allowed_types;
        while (*allowed != NULL && strcmp(type, *allowed) != 0)
            allowed++;
        if (*allowed == NULL)
            return -1;

        index = token_skip(doc, index);
        if (index < 0)
            return -1;
    }
    return 0;
}

static int validate_runtime_components(const struct doc *doc, int root)
{
    static const char *const root_keys[] = {
        "$schema", "log", "dns", "inbounds", "outbounds", "endpoints", "route", "experimental", NULL
    };
    static const char *const outbounds[] = {
        "direct", "vless", "hysteria2", "tuic", "shadowsocks", "naive",
        "dnstt", "ssh", "urltest", "selector", NULL
    };
    static const char *const endpoints[] = { "wireguard", NULL };
    static const char *const dns_servers[] = {
        "local", "udp", "tcp", "tls", "https", "quic", "h3", "fakeip", NULL
    };

    int index = root + 1;
    for (int pair = 0; pair < doc->tokens[root].size; pair++) {
        const char *const *allowed = root_keys;
        while (*allowed != NULL && !token_equal(doc, index, *allowed))
            allowed++;
        if (*allowed == NULL)
            return -1;
        index = token_skip(doc, index + 1);
        if (index < 0)
            return -1;
    }

    if (validate_component_types(doc, object_get(doc, root, "outbounds"), outbounds) != 0 ||
        validate_component_types(doc, object_get(doc, root, "endpoints"), endpoints) != 0)
        return -1;

    int dns = object_get(doc, root, "dns");
    if (dns < 0)
        return 0;
    if (doc->tokens[dns].type != JSMN_OBJECT)
        return -1;
    return validate_component_types(doc, object_get(doc, dns, "servers"), dns_servers);
}

static int validate_rule_sets(const struct doc *doc, int root, uid_t uid)
{
    int route = object_get(doc, root, "route");
    if (route < 0)
        return 0;
    if (doc->tokens[route].type != JSMN_OBJECT)
        return -1;
    int sets = object_get(doc, route, "rule_set");
    if (sets < 0)
        return 0;
    if (doc->tokens[sets].type != JSMN_ARRAY)
        return -1;
    int index = sets + 1;
    int elements = doc->tokens[sets].size;
    for (int element = 0; element < elements; element++) {
        if (index >= doc->count || doc->tokens[index].type != JSMN_OBJECT)
            return -1;
        char type[32];
        char format[32];
        char path[PATH_MAX];
        if (json_string(doc, index, "type", type, sizeof(type)) != 0 ||
            strcmp(type, "local") != 0)
            return -1;
        if (json_string(doc, index, "format", format, sizeof(format)) != 0 ||
            strcmp(format, "binary") != 0)
            return -1;
        if (json_string(doc, index, "path", path, sizeof(path)) != 0 ||
            !is_root_asset_path(path, uid))
            return -1;
        index = token_skip(doc, index);
        if (index < 0)
            return -1;
    }
    return 0;
}

static int validate_forbidden_keys(const struct doc *doc, int token, int parent_key)
{
    static const char *forbidden[] = {
        "certificate_path", "key_path", "client_certificate_path",
        "client_key_path", "ech_file", "include", "output", "geosite", "geoip",
        "v2ray_api", "external_ui_download_url", "external_ui_download_detour",
        "store_mode", "store_selected", "store_fake_ip", "store_fakeip",
        "cache_file", "cache_id", "debug",
        /* Shadowsocks plugins can open files through their own option parser. */
        "plugin", "plugin_opts",
        "executable_path", "data_directory", "extra_args", "protect_path",
        "private_key_path", "config_path", "certificate_provider", "dhcp_lease_files"
    };
    if (token < 0 || token >= doc->count)
        return -1;
    if (doc->tokens[token].type == JSMN_OBJECT) {
        int index = token + 1;
        int pairs = doc->tokens[token].size;
        for (int pair = 0; pair < pairs; pair++) {
            int key_token = index;
            int value_token = index + 1;
            if (value_token >= doc->count ||
                !key_token_is_canonical(doc, key_token))
                return -1;
            int output_under_log =
                token_equal(doc, key_token, "output") &&
                parent_key >= 0 && token_equal(doc, parent_key, "log");
            for (size_t i = 0; i < sizeof(forbidden) / sizeof(forbidden[0]); i++) {
                if (!token_equal(doc, key_token, forbidden[i]))
                    continue;
                if (strcmp(forbidden[i], "output") == 0 && output_under_log)
                    break;
                return -1;
            }
            if (validate_forbidden_keys(doc, value_token, key_token) != 0)
                return -1;
            index = token_skip(doc, value_token);
            if (index < 0)
                return -1;
        }
    } else if (doc->tokens[token].type == JSMN_ARRAY) {
        int index = token + 1;
        int elements = doc->tokens[token].size;
        for (int element = 0; element < elements; element++) {
            if (index >= doc->count ||
                validate_forbidden_keys(doc, index, parent_key) != 0)
                return -1;
            index = token_skip(doc, index);
            if (index < 0)
                return -1;
        }
    }
    return 0;
}

static int validate_config(const struct doc *doc, uid_t uid)
{
    if (validate_unique_keys(doc, 0) != 0)
        return -1;
    if (validate_log(doc, 0) != 0)
        return -1;
    if (validate_experimental(doc, 0) != 0)
        return -1;
    if (validate_inbounds(doc, 0) != 0)
        return -1;
    if (validate_runtime_components(doc, 0) != 0)
        return -1;
    if (validate_rule_sets(doc, 0, uid) != 0)
        return -1;
    if (validate_forbidden_keys(doc, 0, -1) != 0)
        return -1;
    return 0;
}

static int valid_cidr(const char *value)
{
    char copy[128];
    size_t length = strlen(value);
    if (length == 0 || length >= sizeof(copy))
        return 0;
    memcpy(copy, value, length + 1);
    char *slash = strchr(copy, '/');
    int prefix = -1;
    if (slash != NULL) {
        *slash = '\0';
        char *end = NULL;
        errno = 0;
        long parsed = strtol(slash + 1, &end, 10);
        if (errno != 0 || end == NULL || *end != '\0' || parsed < 0 || parsed > 128)
            return 0;
        prefix = (int)parsed;
    }
    unsigned char address[16];
    if (inet_pton(AF_INET, copy, address) == 1)
        return prefix < 0 || prefix <= 32;
    if (inet_pton(AF_INET6, copy, address) == 1)
        return prefix < 0 || prefix <= 128;
    return 0;
}

static int validate_pf_rules(const char *rules)
{
    char copy[MAX_PF_RULES_BYTES];
    size_t length = strlen(rules);
    if (length == 0 || length >= sizeof(copy))
        return -1;
    for (size_t i = 0; i < length; i++) {
        unsigned char c = (unsigned char)rules[i];
        if (c < 0x20 && c != '\n' && c != '\r' && c != '\t')
            return -1;
    }
    memcpy(copy, rules, length + 1);

    char *save = NULL;
    for (char *line = strtok_r(copy, "\n", &save); line != NULL;
         line = strtok_r(NULL, "\n", &save)) {
        size_t line_length = strlen(line);
        while (line_length > 0 && (line[line_length - 1] == '\r' ||
                                   line[line_length - 1] == ' ' ||
                                   line[line_length - 1] == '\t'))
            line[--line_length] = '\0';
        char *start = line;
        while (*start == ' ' || *start == '\t')
            start++;
        if (*start == '\0')
            continue;
        if (strcmp(start, "block drop out all") == 0)
            continue;
        if (strcmp(start, "pass out quick on lo0 all") == 0)
            continue;
        const char *inet4 = "pass out quick inet from any to ";
        const char *inet6 = "pass out quick inet6 from any to ";
        if (strncmp(start, inet4, strlen(inet4)) == 0) {
            if (valid_cidr(start + strlen(inet4)))
                continue;
            return -1;
        }
        if (strncmp(start, inet6, strlen(inet6)) == 0) {
            if (valid_cidr(start + strlen(inet6)))
                continue;
            return -1;
        }
        return -1;
    }
    return 0;
}

static int run_capture(const char *path, char *const argv[], char *out,
                       size_t out_capacity, char *err, size_t err_capacity,
                       int *exit_code)
{
    int out_pipe[2];
    int err_pipe[2];
    if (pipe(out_pipe) != 0 || pipe(err_pipe) != 0)
        return -1;

    pid_t pid = fork();
    if (pid < 0) {
        close(out_pipe[0]); close(out_pipe[1]);
        close(err_pipe[0]); close(err_pipe[1]);
        return -1;
    }
    if (pid == 0) {
        dup2(out_pipe[1], STDOUT_FILENO);
        dup2(err_pipe[1], STDERR_FILENO);
        int devnull = open("/dev/null", O_RDONLY);
        if (devnull >= 0) {
            dup2(devnull, STDIN_FILENO);
            close(devnull);
        }
        close(out_pipe[0]); close(out_pipe[1]);
        close(err_pipe[0]); close(err_pipe[1]);
        char *const envp[] = { "PATH=/usr/bin:/bin:/usr/sbin:/sbin", NULL };
        execve(path, argv, envp);
        _exit(127);
    }

    close(out_pipe[1]);
    close(err_pipe[1]);
    size_t out_used = 0;
    size_t err_used = 0;
    int open_fds = 2;
    struct pollfd fds[2] = {
        { out_pipe[0], POLLIN, 0 },
        { err_pipe[0], POLLIN, 0 }
    };
    while (open_fds > 0) {
        int ready = poll(fds, 2, 30000);
        if (ready < 0) {
            if (errno == EINTR)
                continue;
            break;
        }
        if (ready == 0) {
            kill(pid, SIGKILL);
            break;
        }
        for (int i = 0; i < 2; i++) {
            if (fds[i].fd < 0 || !(fds[i].revents & (POLLIN | POLLHUP | POLLERR)))
                continue;
            char *target = i == 0 ? out : err;
            size_t *used = i == 0 ? &out_used : &err_used;
            size_t capacity = i == 0 ? out_capacity : err_capacity;
            char temp[4096];
            ssize_t n = read(fds[i].fd, temp, sizeof(temp));
            if (n > 0) {
                if (*used + (size_t)n + 1 < capacity) {
                    memcpy(target + *used, temp, (size_t)n);
                    *used += (size_t)n;
                }
            } else if (n == 0 || (n < 0 && errno != EINTR)) {
                close(fds[i].fd);
                fds[i].fd = -1;
                open_fds--;
            }
        }
    }
    for (int i = 0; i < 2; i++)
        if (fds[i].fd >= 0)
            close(fds[i].fd);
    out[out_used] = '\0';
    err[err_used] = '\0';

    int status = 0;
    if (waitpid(pid, &status, 0) < 0)
        return -1;
    if (WIFEXITED(status))
        *exit_code = WEXITSTATUS(status);
    else if (WIFSIGNALED(status))
        *exit_code = 128 + WTERMSIG(status);
    else
        *exit_code = -1;
    return 0;
}

static int run_checked(const char *path, char *const argv[])
{
    char out[8192];
    char err[8192];
    int code = -1;
    if (run_capture(path, argv, out, sizeof(out), err, sizeof(err), &code) != 0)
        return -1;
    return code == 0 ? 0 : -1;
}

static int ensure_root_layout(uid_t uid)
{
    if (verify_fixed_chain() != 0)
        return -1;
    if (ensure_root_dir(ROOT_DIR, 0755) != 0 ||
        ensure_root_dir(ROOT_DIR "/bin", 0755) != 0 ||
        ensure_root_dir(ROOT_RUN, 0700) != 0 ||
        ensure_root_dir(ROOT_ASSETS, 0700) != 0)
        return -1;
    char user_run[PATH_MAX];
    char user_assets[PATH_MAX];
    int a = snprintf(user_run, sizeof(user_run), ROOT_RUN "/%u", (unsigned)uid);
    int b = snprintf(user_assets, sizeof(user_assets), ROOT_ASSETS "/%u", (unsigned)uid);
    if (a < 0 || (size_t)a >= sizeof(user_run) || b < 0 ||
        (size_t)b >= sizeof(user_assets))
        return -1;
    if (ensure_root_dir(user_run, 0700) != 0 ||
        ensure_root_dir(user_assets, 0700) != 0)
        return -1;
    return 0;
}

static int copy_asset(const char *source, uid_t uid, const char *name)
{
    char *data = xcalloc(MAX_ASSET_BYTES, 1);
    size_t length = 0;
    if (read_file_checked(source, uid, 0022, MAX_ASSET_BYTES, data, &length) != 0) {
        free(data);
        return -1;
    }
    if (length < 8 || memcmp(data, "SRS", 3) != 0 ||
        (unsigned char)data[3] < 1 || (unsigned char)data[3] > 5) {
        free(data);
        return -1;
    }
    char destination[PATH_MAX];
    int written = snprintf(destination, sizeof(destination), ROOT_ASSETS "/%u/%s",
                           (unsigned)uid, name);
    if (written < 0 || (size_t)written >= sizeof(destination)) {
        free(data);
        return -1;
    }
    int result = write_root_file(destination, data, length, 0600);
    free(data);
    return result;
}

static int sync_assets(uid_t uid, const char *home)
{
    const char *parts[] = { "geo", "cache", "cache/rulesets" };
    for (size_t p = 0; p < sizeof(parts) / sizeof(parts[0]); p++) {
        char directory[PATH_MAX];
        int written = snprintf(directory, sizeof(directory),
                               "%s/Library/Application Support/VPNRouter/%s",
                               home, parts[p]);
        if (written < 0 || (size_t)written >= sizeof(directory))
            return -1;
        struct stat directory_stat;
        if (lstat(directory, &directory_stat) != 0 ||
            !S_ISDIR(directory_stat.st_mode) || S_ISLNK(directory_stat.st_mode))
            continue;
        DIR *dir = opendir(directory);
        if (dir == NULL)
            continue;
        struct dirent *entry;
        while ((entry = readdir(dir)) != NULL) {
            const char *name = entry->d_name;
            size_t name_length = strlen(name);
            if (name_length < 5 || name_length > 200 ||
                strcmp(name + name_length - 4, ".srs") != 0 ||
                strchr(name, '/') != NULL)
                continue;
            char source[PATH_MAX];
            written = snprintf(source, sizeof(source), "%s/%s", directory, name);
            if (written < 0 || (size_t)written >= sizeof(source))
                continue;
            (void)copy_asset(source, uid, name);
        }
        closedir(dir);
    }
    return 0;
}

static int process_is_root_runtime(int pid, uint64_t start_time)
{
    if (pid <= 1)
        return 0;
    char executable[PATH_MAX];
    if (proc_pidpath(pid, executable, sizeof(executable)) <= 0 ||
        strcmp(executable, ROOT_BIN) != 0)
        return 0;
    struct proc_bsdinfo info;
    memset(&info, 0, sizeof(info));
    int bytes = proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, &info, sizeof(info));
    return bytes == (int)sizeof(info) && info.pbi_uid == 0 &&
           (uint64_t)info.pbi_start_tvsec == start_time;
}

static int session_path(char *path, size_t capacity, uid_t uid)
{
    int written = snprintf(path, capacity, SESSION_PATH_FMT, (unsigned)uid);
    return written > 0 && (size_t)written < capacity ? 0 : -1;
}

static int session_lock_path(char *path, size_t capacity, uid_t uid)
{
    int written = snprintf(path, capacity, SESSION_LOCK_FMT, (unsigned)uid);
    return written > 0 && (size_t)written < capacity ? 0 : -1;
}

static int state_lock_path(char *path, size_t capacity, uid_t uid)
{
    int written = snprintf(path, capacity, STATE_LOCK_FMT, (unsigned)uid);
    return written > 0 && (size_t)written < capacity ? 0 : -1;
}

static int open_root_lock(const char *path, int close_on_exec)
{
    int flags = O_RDWR | O_CREAT | O_NOFOLLOW;
    if (close_on_exec)
        flags |= O_CLOEXEC;
    int fd = open(path, flags, 0600);
    if (fd < 0)
        return -1;

    struct stat st;
    if (fstat(fd, &st) != 0 || !S_ISREG(st.st_mode) || st.st_uid != 0 ||
        st.st_gid != 0 || (st.st_mode & 0077) != 0) {
        close(fd);
        return -1;
    }
    if (fchown(fd, 0, 0) != 0 || fchmod(fd, 0600) != 0) {
        close(fd);
        return -1;
    }
    return fd;
}

static int try_lock_exclusive(int fd)
{
    int result;
    do {
        result = flock(fd, LOCK_EX | LOCK_NB);
    } while (result != 0 && errno == EINTR);
    return result;
}

static int acquire_transition_lock(const char *path)
{
    int fd = open_root_lock(path, 1);
    if (fd < 0)
        return -1;
    for (int step = 0; step < STATE_LOCK_WAIT_STEPS; step++) {
        if (try_lock_exclusive(fd) == 0)
            return fd;
        if (errno != EWOULDBLOCK) {
            close(fd);
            return -1;
        }
        usleep(STATE_LOCK_WAIT_USEC);
    }
    close(fd);
    errno = EWOULDBLOCK;
    return -1;
}

static int acquire_state_lock(uid_t uid)
{
    char path[PATH_MAX];
    if (state_lock_path(path, sizeof(path), uid) != 0)
        return -1;
    return acquire_transition_lock(path);
}

static int acquire_session_lease(uid_t uid)
{
    char path[PATH_MAX];
    if (session_lock_path(path, sizeof(path), uid) != 0)
        return -1;
    int fd = open_root_lock(path, 0);
    if (fd < 0)
        return -1;
    if (try_lock_exclusive(fd) == 0)
        return fd;
    int saved = errno;
    close(fd);
    errno = saved;
    return -1;
}

static int read_session_record(uid_t uid, int *pid, uint64_t *start_time)
{
    char path[PATH_MAX];
    char buffer[256];
    size_t length = 0;
    if (session_path(path, sizeof(path), uid) != 0 ||
        read_file_checked(path, 0, 0077, sizeof(buffer), buffer, &length) != 0)
        return -1;
    return parse_stop_identity(buffer, length, pid, start_time) == 0 ? 0 : -1;
}

static int write_session_record(uid_t uid, int pid, uint64_t start_time)
{
    char path[PATH_MAX];
    char json[128];
    if (session_path(path, sizeof(path), uid) != 0)
        return -1;
    int written = snprintf(json, sizeof(json),
                           "{\"pid\":%d,\"start_time\":%llu}", pid,
                           (unsigned long long)start_time);
    if (written < 0 || (size_t)written >= sizeof(json))
        return -1;
    return write_root_file(path, json, (size_t)written, 0600);
}

static int delete_session_record(uid_t uid)
{
    char path[PATH_MAX];
    if (session_path(path, sizeof(path), uid) != 0)
        return -1;
    if (unlink(path) != 0 && errno != ENOENT)
        return -1;
    return 0;
}

static int valid_launch_id(const char *id)
{
    for (size_t i = 0; i < LAUNCH_ID_SIZE - 1; i++) {
        if (!((id[i] >= '0' && id[i] <= '9') || (id[i] >= 'a' && id[i] <= 'f')))
            return 0;
    }
    return id[LAUNCH_ID_SIZE - 1] == '\0';
}

static int parse_host_session(const char *buffer, size_t length, struct host_session *session)
{
    if (length != sizeof(*session) &&
        length != offsetof(struct host_session, launch_id) &&
        length != offsetof(struct host_session, pf_reference))
        return -1;
    memset(session, 0, sizeof(*session));
    memcpy(session, buffer, length);
    uint32_t expected_version = length == sizeof(*session) ? HOST_SESSION_VERSION :
        length == offsetof(struct host_session, launch_id) ? 2 : 1;
    if (session->version != expected_version || session->uid == 0 ||
        session->pid <= 1 || session->start_time == 0 || session->boot_time == 0 ||
        session->pf_mode > PF_RELEASE_DEFAULT ||
        (session->launch_id[0] != '\0' && !valid_launch_id(session->launch_id)))
        return -1;
    return 0;
}

/* All readers and writers hold the host transition lock. */
static int read_host_session(struct host_session *session)
{
    struct stat st;
    if (lstat(HOST_SESSION_PATH, &st) != 0)
        return errno == ENOENT ? 0 : -1;
    char buffer[sizeof(*session) + 1];
    size_t length = 0;
    if (read_file_checked(HOST_SESSION_PATH, 0, 0077, sizeof(buffer), buffer, &length) != 0)
        return -1;
    return parse_host_session(buffer, length, session) == 0 ? 1 : -1;
}

static int write_host_session(const struct host_session *session)
{
    return write_root_file(HOST_SESSION_PATH, session, sizeof(*session), 0600);
}

/* A process which cannot be inspected must not be treated as exited. */
static int host_session_may_be_live(const struct host_session *session)
{
    struct proc_bsdinfo info = {0};
    if (proc_pidinfo(session->pid, PROC_PIDTBSDINFO, 0, &info,
                     sizeof(info)) == (int)sizeof(info))
        return (uint64_t)info.pbi_start_tvsec == session->start_time;
    return kill(session->pid, 0) == 0 || errno != ESRCH;
}

static int parse_pf_reference(const char *start, const char **end, uint64_t *reference)
{
    if (*start < '0' || *start > '9')
        return -1;
    char *next;
    errno = 0;
    unsigned long long value = strtoull(start, &next, 10);
    if (errno != 0 || value == 0)
        return -1;
    *end = next;
    *reference = (uint64_t)value;
    return 0;
}

static int parse_pf_reference_list(char *output, uint64_t reference)
{
    char *cursor = NULL;
    char *line = strtok_r(output, "\r\n", &cursor);
    if (line != NULL && (strcmp(line, "No pf starter references held") == 0 ||
                         strcmp(line, "No pf_enabled references") == 0))
        return 0;
    if (line == NULL || strcmp(line, "TOKENS:") != 0)
        return -1;
    char *header = strtok_r(NULL, "\r\n", &cursor);
    const char *column = header == NULL ? NULL : strstr(header, " TOKEN ");
    if (column == NULL)
        return -1;
    size_t offset = (size_t)(column + 1 - header);
    while ((line = strtok_r(NULL, "\r\n", &cursor)) != NULL) {
        if (strlen(line) <= offset || line[0] < '0' || line[0] > '9')
            return -1;
        const char *end;
        uint64_t found;
        if (parse_pf_reference(line + offset, &end, &found) != 0 ||
            (*end != ' ' && *end != '\t' && *end != '\0'))
            return -1;
        if (found == reference)
            return 1;
    }
    return 0;
}

static int pf_reference_held(uint64_t reference)
{
    if (reference == 0)
        return 0;
    char *out = xcalloc(MAX_COMMAND_OUTPUT, 1);
    char err[8192];
    int code = -1;
    char *const argv[] = { "/sbin/pfctl", "-s", "References", NULL };
    int result = run_capture("/sbin/pfctl", argv, out, MAX_COMMAND_OUTPUT, err, sizeof(err), &code);
    int held = result == 0 && code == 0 ? parse_pf_reference_list(out, reference) : -1;
    if (held < 0 && result == 0 && code == 0 && out[0] == '\0' &&
        (strstr(err, "No pf starter references held") != NULL ||
         strstr(err, "No pf_enabled references") != NULL))
        held = 0;
    free(out);
    return held;
}

static int pf_running(void)
{
    char out[8192];
    char err[8192];
    int code = -1;
    char *const argv[] = { "/sbin/pfctl", "-s", "info", NULL };
    if (run_capture("/sbin/pfctl", argv, out, sizeof(out), err, sizeof(err), &code) != 0 ||
        code != 0)
        return -1;
    const char *enabled = "Status: Enabled";
    const char *disabled = "Status: Disabled";
    if (strncmp(out, enabled, strlen(enabled)) == 0 && strchr(" \r\n", out[strlen(enabled)]) != NULL)
        return 1;
    if (strncmp(out, disabled, strlen(disabled)) == 0 && strchr(" \r\n", out[strlen(disabled)]) != NULL)
        return 0;
    return -1;
}

static int read_legacy_pf_reference(uint64_t *reference)
{
    struct stat st;
    if (lstat(PF_TOKEN_PATH, &st) != 0)
        return errno == ENOENT ? 0 : -1;
    if (!S_ISREG(st.st_mode) || st.st_uid != 0 || (st.st_mode & 07777) != 0600)
        return -1;
    char token[32];
    size_t length = 0;
    const char *end;
    if (read_file_checked(PF_TOKEN_PATH, 0, 0077, sizeof(token), token, &length) != 0 ||
        parse_pf_reference(token, &end, reference) != 0 || end != token + length)
        return -1;
    return 1;
}

static int load_host_session(struct host_session *host, uint64_t *boot_time)
{
    struct timeval boot = {0};
    size_t length = sizeof(boot);
    if (sysctlbyname("kern.boottime", &boot, &length, NULL, 0) != 0 ||
        length != sizeof(boot) || boot.tv_sec <= 0)
        return -1;
    *boot_time = (uint64_t)boot.tv_sec;

    int loaded = read_host_session(host);
    if (loaded < 0)
        fail(EXIT_STATE, "host ownership record is unreadable; administrator recovery is required");
    if (loaded == 1 && host->boot_time != *boot_time) {
        /* PF rules and enable references from a previous boot no longer exist. */
        if ((unlink(PF_TOKEN_PATH) != 0 && errno != ENOENT) || unlink(HOST_SESSION_PATH) != 0)
            return -1;
        memset(host, 0, sizeof(*host));
        return 0;
    }

    uint64_t previous_reference = 0;
    int legacy = read_legacy_pf_reference(&previous_reference);
    if (legacy < 0)
        fail(EXIT_STATE, "previous PF reference record is unreadable; administrator recovery is required");
    if (loaded == 1 && host->version == 1)
        host->pf_reference = previous_reference;
    else if (legacy > 0 && host->pf_reference != previous_reference &&
             pf_reference_held(previous_reference) != 0)
        fail(EXIT_STATE, "cannot confirm that the unowned legacy PF reference is gone; restart macOS");
    if (loaded == 1 && host->version != HOST_SESSION_VERSION) {
        host->version = HOST_SESSION_VERSION;
        if (write_host_session(host) != 0)
            return -1;
    }
    if (legacy > 0 && unlink(PF_TOKEN_PATH) != 0)
        return -1;
    return loaded;
}

static int command_start(const char *request_json, size_t request_length,
                         uid_t uid, const char *home)
{
    struct doc request;
    if (parse_doc((char *)request_json, request_length, &request) != 0)
        return EXIT_INPUT;
    char expected_hash[65];
    char launch_id[LAUNCH_ID_SIZE];
    if (json_string(&request, 0, "config_sha256", expected_hash,
                    sizeof(expected_hash)) != 0 || strlen(expected_hash) != 64 ||
        json_string(&request, 0, "launch_id", launch_id, sizeof(launch_id)) != 0 ||
        !valid_launch_id(launch_id))
        return EXIT_INPUT;

    if (legacy_policy_state_path(LEGACY_POLICY_PATH) != LEGACY_ABSENT)
        fail(EXIT_STATE, "legacy policy removal is unconfirmed");
    if (verify_runtime_artifact() != 0)
        fail(EXIT_STATE, "root-owned runtime artifact is missing or untrusted");
    require_no_legacy_runtime(home);

    struct host_session host = {0};
    uint64_t boot_time = 0;
    int host_loaded = load_host_session(&host, &boot_time);
    if (host_loaded < 0)
        fail(EXIT_STATE, "host network ownership could not be restored");
    if (host_loaded > 0) {
        if (host_session_may_be_live(&host))
            fail(EXIT_STATE, "a privileged runtime already owns the host network");
        if (host.uid != (uint32_t)uid && (host.pf_mode != PF_RELEASE_NONE || host.pf_reference != 0))
            fail(EXIT_STATE, "the previous owner must release the host firewall");
    }

    int state_lock = acquire_state_lock(uid);
    if (state_lock < 0)
        fail(EXIT_STATE, "could not serialize privileged state");
    int lease_fd = acquire_session_lease(uid);
    if (lease_fd < 0) {
        if (errno == EWOULDBLOCK) {
            int recorded_pid = 0;
            uint64_t recorded_start = 0;
            if (read_session_record(uid, &recorded_pid, &recorded_start) == 0 &&
                process_is_root_runtime(recorded_pid, recorded_start))
                fail(EXIT_STATE,
                     "a privileged session is already running for this user");
            fail(EXIT_STATE,
                 "session lease is held by an unrecorded process");
        }
        fail(EXIT_STATE, "could not acquire the session lease");
    }

    int previous_pid = 0;
    uint64_t previous_start = 0;
    if (read_session_record(uid, &previous_pid, &previous_start) == 0) {
        if (process_is_root_runtime(previous_pid, previous_start))
            fail(EXIT_STATE,
                 "session record names a live privileged process without the session lease");
        if (delete_session_record(uid) != 0)
            fail(EXIT_STATE, "could not remove stale session record");
    }
    /* State lock closes at exec. The runtime keeps the session lease. */
    (void)state_lock;
    (void)lease_fd;

    char config_path[PATH_MAX];
    int written = snprintf(config_path, sizeof(config_path),
                           "%s/Library/Application Support/VPNRouter/config/current.json",
                           home);
    if (written < 0 || (size_t)written >= sizeof(config_path))
        return EXIT_INPUT;

    char *config = xcalloc(MAX_CONFIG_BYTES, 1);
    size_t config_length = 0;
    if (read_file_checked(config_path, uid, 0077, MAX_CONFIG_BYTES, config,
                          &config_length) != 0)
        fail(EXIT_STATE, "config is not a private regular file owned by the invoking user");

    char actual_hash[65];
    sha256_bytes(config, config_length, actual_hash);
    if (!constant_time_equal(expected_hash, actual_hash, 64))
        fail(EXIT_INPUT, "config hash does not match the request");

    if (sync_assets(uid, home) != 0)
        fail(EXIT_STATE, "asset sync failed");

    struct doc config_doc;
    if (parse_doc(config, config_length, &config_doc) != 0 ||
        validate_config(&config_doc, uid) != 0)
        fail(EXIT_INPUT, "config violates the privileged runtime contract");

    char working_dir[PATH_MAX];
    written = snprintf(working_dir, sizeof(working_dir), ROOT_RUN "/%u", (unsigned)uid);
    if (written < 0 || (size_t)written >= sizeof(working_dir) || chdir(working_dir) != 0)
        return EXIT_STATE;
    char staged[PATH_MAX];
    written = snprintf(staged, sizeof(staged), "%s/current.json", working_dir);
    if (written < 0 || (size_t)written >= sizeof(staged))
        return EXIT_STATE;
    if (write_root_file(staged, config, config_length, 0600) != 0)
        fail(EXIT_STATE, "could not stage the validated config");

    char *const argv[] = { ROOT_BIN, "run", "-c", staged, NULL };
    char tmp_environment[sizeof(working_dir) + sizeof("TMPDIR=")];
    snprintf(tmp_environment, sizeof(tmp_environment), "TMPDIR=%s", working_dir);
    char *const envp[] = {
        "PATH=/usr/bin:/bin:/usr/sbin:/sbin", "HOME=/var/root", tmp_environment, NULL
    };
    int devnull = open("/dev/null", O_RDONLY);
    if (devnull >= 0) {
        dup2(devnull, STDIN_FILENO);
        close(devnull);
    }

    struct proc_bsdinfo self_info;
    memset(&self_info, 0, sizeof(self_info));
    if (proc_pidinfo(getpid(), PROC_PIDTBSDINFO, 0, &self_info,
                     sizeof(self_info)) != (int)sizeof(self_info) ||
        self_info.pbi_start_tvsec <= 0)
        fail(EXIT_STATE, "could not identify the privileged session");
    host.version = HOST_SESSION_VERSION;
    host.uid = (uint32_t)uid;
    host.pid = getpid();
    host.start_time = (uint64_t)self_info.pbi_start_tvsec;
    host.boot_time = boot_time;
    memcpy(host.launch_id, launch_id, sizeof(host.launch_id));
    if (write_host_session(&host) != 0 ||
        write_session_record(uid, host.pid, host.start_time) != 0)
        fail(EXIT_STATE, "could not bind the privileged session to the invoking user");

    execve(ROOT_BIN, argv, envp);
    delete_session_record(uid);
    fail(EXIT_COMMAND, "could not execute the root-owned sing-box");
    return EXIT_COMMAND;
}

static int parse_stop_identity(const char *json, size_t length,
                               int *pid, uint64_t *start_time)
{
    struct doc request = { 0 };
    long pid_value = 0;
    long start_value = 0;
    int valid = parse_doc((char *)json, length, &request) == 0 &&
                validate_unique_keys(&request, 0) == 0 &&
                json_int(&request, 0, "pid", &pid_value) == 0 &&
                pid_value > 1 && pid_value <= INT_MAX &&
                json_int(&request, 0, "start_time", &start_value) == 0 &&
                start_value > 0;
    free(request.tokens);
    if (!valid)
        return EXIT_INPUT;
    *pid = (int)pid_value;
    *start_time = (uint64_t)start_value;
    return 0;
}

static int command_stop(const char *request_json, size_t request_length, uid_t uid)
{
    int pid;
    uint64_t start_time;
    if (parse_stop_identity(request_json, request_length, &pid, &start_time) != 0)
        return EXIT_INPUT;
    if (pid == getpid() || pid == getppid())
        return EXIT_INPUT;

    int state_lock = acquire_state_lock(uid);
    if (state_lock < 0)
        return EXIT_STATE;
    (void)state_lock;

    int session_pid = 0;
    uint64_t session_start = 0;
    if (read_session_record(uid, &session_pid, &session_start) != 0 ||
        session_pid != pid || session_start != start_time)
        return EXIT_INPUT;

    if (!process_is_root_runtime(pid, start_time)) {
        if (kill(pid, 0) != 0 && errno == ESRCH)
            return delete_session_record(uid) == 0 ? 0 : EXIT_STATE;
        return EXIT_INPUT;
    }

    if (kill(pid, SIGKILL) != 0 && errno != ESRCH)
        fail(EXIT_COMMAND, "exact signal failed");

    for (int i = 0; i < 40; i++) {
        if (kill(pid, 0) != 0 && errno == ESRCH) {
            delete_session_record(uid);
            return 0;
        }
        usleep(25000);
    }
    if (kill(pid, 0) != 0 && errno == ESRCH) {
        delete_session_record(uid);
        return 0;
    }
    return EXIT_COMMAND;
}

static int read_manifest(char *buffer, size_t capacity, size_t *length)
{
    return read_file_checked(MANIFEST_PATH, 0, 0077, capacity, buffer, length);
}

static int verify_runtime_artifact(void)
{
    struct stat st;
    if (lstat(ROOT_BIN, &st) != 0 || !S_ISREG(st.st_mode) ||
        S_ISLNK(st.st_mode) || st.st_uid != 0 || (st.st_mode & 0022) != 0)
        return -1;

    char *manifest = xcalloc(MAX_MANIFEST_BYTES, 1);
    size_t manifest_length = 0;
    if (read_manifest(manifest, MAX_MANIFEST_BYTES, &manifest_length) != 0) {
        free(manifest);
        return -1;
    }

    struct doc doc = { 0 };
    if (parse_doc(manifest, manifest_length, &doc) != 0) {
        free(doc.tokens);
        free(manifest);
        return -1;
    }

    char expected[65] = {0};
    char actual[65] = {0};
    int valid = validate_unique_keys(&doc, 0) == 0 &&
                json_string(&doc, 0, "singbox_sha256", expected,
                            sizeof(expected)) == 0 &&
                strlen(expected) == 64 &&
                sha256_file(ROOT_BIN, actual) == 0 &&
                constant_time_equal(expected, actual, 64);
    free(doc.tokens);
    free(manifest);
    return valid ? 0 : -1;
}

static int legacy_runtime_path(const char *home, char *path, size_t capacity)
{
    if (home == NULL || home[0] != '/')
        return -1;
    int written = snprintf(path, capacity,
                           "%s/Library/Application Support/VPNRouter/bin/sing-box",
                           home);
    return written > 0 && (size_t)written < capacity ? 0 : -1;
}

static int legacy_process_identity(uid_t owner, const char *executable,
                                   const char *expected_path)
{
    return owner == 0 && executable != NULL && expected_path != NULL &&
           strcmp(executable, expected_path) == 0;
}

/* Passive exact scan: never signals or otherwise touches a candidate. */
static int find_legacy_runtime_process(const char *home)
{
    char expected[PATH_MAX];
    if (legacy_runtime_path(home, expected, sizeof(expected)) != 0)
        return -1;

    int count = proc_listallpids(NULL, 0);
    if (count <= 0 || count > MAX_PROCESSES)
        return -1;

    int capacity = count + 16;
    pid_t *pids = xcalloc((size_t)capacity, sizeof(pid_t));
    int used = proc_listallpids(pids, capacity * (int)sizeof(pid_t));
    if (used <= 0 || used >= capacity) {
        free(pids);
        return -1;
    }

    int found = 0;
    for (int i = 0; i < used; i++) {
        pid_t pid = pids[i];
        if (pid <= 1)
            continue;
        char executable[PATH_MAX];
        if (proc_pidpath(pid, executable, sizeof(executable)) <= 0)
            continue;
        struct proc_bsdinfo info;
        memset(&info, 0, sizeof(info));
        if (proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, &info, sizeof(info)) !=
            (int)sizeof(info))
            continue;
        if (legacy_process_identity(info.pbi_uid, executable, expected)) {
            found = 1;
            break;
        }
    }
    free(pids);
    return found;
}

static void require_no_legacy_runtime(const char *home)
{
    int state = find_legacy_runtime_process(home);
    if (state != 0)
        fail(EXIT_STATE, state > 0
            ? "legacy user-runtime process is still running [vpnrouter-legacy-runtime-active]"
            : "legacy user-runtime process state is unknown");
}

static int command_probe(uid_t uid, const char *home)
{
    int ready = 1;
    int legacy_state = legacy_policy_state_path(LEGACY_POLICY_PATH);
    int legacy = legacy_state == LEGACY_PRESENT;
    int legacy_process = 0;
    int legacy_process_state = find_legacy_runtime_process(home);
    const char *reason = NULL;
    char helper_actual[65] = {0};
    char singbox_actual[65] = {0};
    char helper_expected[65] = {0};
    char singbox_expected[65] = {0};
    char app_version[64] = {0};

    if (legacy_state == LEGACY_UNKNOWN) {
        ready = 0;
        reason = "legacy_unknown";
    }
    if (legacy_process_state < 0) {
        ready = 0;
        if (reason == NULL)
            reason = "legacy_process_unknown";
    } else if (legacy_process_state > 0) {
        ready = 0;
        legacy_process = 1;
        reason = "legacy_process";
    }
    if (ready && verify_fixed_chain() != 0) {
        ready = 0;
        reason = "directory_chain_invalid";
    }

    struct stat st;
    if (lstat(HELPER_PATH, &st) != 0 || !S_ISREG(st.st_mode) ||
        S_ISLNK(st.st_mode) || st.st_uid != 0 || (st.st_mode & 0022) != 0) {
        ready = 0;
        reason = "helper_missing";
    } else if (lstat(ROOT_BIN, &st) != 0 || !S_ISREG(st.st_mode) ||
               S_ISLNK(st.st_mode) || st.st_uid != 0 || (st.st_mode & 0022) != 0) {
        ready = 0;
        reason = "singbox_missing";
    } else {
        char manifest[MAX_MANIFEST_BYTES];
        size_t manifest_length = 0;
        if (read_manifest(manifest, sizeof(manifest), &manifest_length) != 0) {
            ready = 0;
            reason = "manifest_missing";
        } else {
            struct doc doc;
            if (parse_doc(manifest, manifest_length, &doc) != 0) {
                ready = 0;
                reason = "manifest_invalid";
            } else if (validate_unique_keys(&doc, 0) != 0) {
                ready = 0;
                reason = "manifest_invalid";
            } else if (json_string(&doc, 0, "helper_sha256", helper_expected,
                                   sizeof(helper_expected)) != 0 ||
                       json_string(&doc, 0, "singbox_sha256", singbox_expected,
                                   sizeof(singbox_expected)) != 0 ||
                       json_string(&doc, 0, "app_version", app_version,
                                   sizeof(app_version)) != 0) {
                ready = 0;
                reason = "manifest_invalid";
            } else if (sha256_file(HELPER_PATH, helper_actual) != 0 ||
                       sha256_file(ROOT_BIN, singbox_actual) != 0 ||
                       !constant_time_equal(helper_actual, helper_expected, 64) ||
                       !constant_time_equal(singbox_actual, singbox_expected, 64)) {
                ready = 0;
                reason = "manifest_mismatch";
            }
        }
    }

    char policy_path[PATH_MAX];
    int written = snprintf(policy_path, sizeof(policy_path), "%s/vpnrouter-%u",
                           SUDOERS_DIR, (unsigned)uid);
    if (written < 0 || (size_t)written >= sizeof(policy_path))
        return EXIT_STATE;
    char policy[8192] = {0};
    size_t policy_length = 0;
    if (ready && (lstat(policy_path, &st) != 0 || !S_ISREG(st.st_mode) ||
                  S_ISLNK(st.st_mode) || st.st_uid != 0 || st.st_gid != 0 ||
                  (st.st_mode & 0777) != 0440 ||
                  read_file_checked(policy_path, 0, 0002, sizeof(policy), policy,
                                    &policy_length) != 0)) {
        ready = 0;
        reason = "policy_missing";
    }
    if (ready) {
        int nopasswd_lines = 0;
        char *save = NULL;
        char copy[8192];
        size_t copy_length = policy_length < sizeof(copy) - 1 ? policy_length : sizeof(copy) - 1;
        memcpy(copy, policy, copy_length);
        copy[copy_length] = '\0';
        for (char *line = strtok_r(copy, "\n", &save); line != NULL;
             line = strtok_r(NULL, "\n", &save)) {
            if (strstr(line, "NOPASSWD:") != NULL)
                nopasswd_lines++;
        }
        if (strstr(policy, POLICY_HEADER) == NULL || nopasswd_lines != 1 ||
            strstr(policy, "NOPASSWD: " HELPER_PATH) == NULL ||
            strstr(policy, "NOPASSWD: /Users/") != NULL ||
            strstr(policy, "pkill") != NULL || strstr(policy, "pfctl") != NULL ||
            strstr(policy, "/bin/kill") != NULL || strstr(policy, "*") != NULL) {
            ready = 0;
            reason = "policy_invalid";
        }
    }
    if (legacy) {
        ready = 0;
        if (reason == NULL)
            reason = "legacy_policy";
    }

    const char *legacy_text = legacy_state == LEGACY_PRESENT ? "present" :
                              legacy_state == LEGACY_ABSENT ? "absent" : "unknown";
    printf("{\"ready\":%s,\"legacy\":%s,\"legacy_state\":\"%s\","
           "\"legacy_process\":%s,\"policy\":%d,\"uid\":%u",
           ready ? "true" : "false", legacy ? "true" : "false", legacy_text,
           legacy_process ? "true" : "false", 2, (unsigned)uid);
    if (helper_actual[0] != '\0') {
        printf(",\"helper_sha256\":\"");
        for (int i = 0; i < 64; i++)
            putchar(helper_actual[i]);
        putchar('"');
    }
    if (singbox_actual[0] != '\0') {
        printf(",\"singbox_sha256\":\"");
        for (int i = 0; i < 64; i++)
            putchar(singbox_actual[i]);
        putchar('"');
    }
    if (app_version[0] != '\0') {
        printf(",\"app_version\":\"");
        for (const char *p = app_version; *p != '\0'; p++)
            if (*p != '"' && *p != '\\')
                putchar(*p);
        putchar('"');
    }
    if (reason != NULL)
        printf(",\"reason\":\"%s\"", reason);
    printf("}\n");
    return 0;
}

struct dns_snapshot {
    uint32_t version;
    uint32_t owner;
    uint32_t count;
    char service[128];
    char servers[MAX_DNS_SERVERS][INET6_ADDRSTRLEN];
};

static int is_dns_address(const char *value)
{
    unsigned char address[16];
    return inet_pton(AF_INET, value, address) == 1 ||
           inet_pton(AF_INET6, value, address) == 1;
}

static int equal_dns_address(const char *left, const char *right)
{
    unsigned char first[16];
    unsigned char second[16];
    if (inet_pton(AF_INET, left, first) == 1)
        return inet_pton(AF_INET, right, second) == 1 && memcmp(first, second, 4) == 0;
    return inet_pton(AF_INET6, left, first) == 1 &&
           inet_pton(AF_INET6, right, second) == 1 &&
           memcmp(first, second, sizeof(first)) == 0;
}

static int valid_dns_service(const char *service)
{
    size_t length = strnlen(service, 128);
    if (length == 0 || length > 100 || service[0] == '-')
        return 0;
    for (size_t i = 0; i < length; i++) {
        unsigned char c = (unsigned char)service[i];
        if (c < 0x20 || c == 0x7f)
            return 0;
    }
    return 1;
}

static int parse_dns_servers(char *output, struct dns_snapshot *snapshot)
{
    snapshot->count = 0;
    char *save = NULL;
    for (char *line = strtok_r(output, "\r\n", &save); line != NULL;
         line = strtok_r(NULL, "\r\n", &save)) {
        static const char no_dns[] = "There aren't any DNS Servers set on ";
        if (strncmp(line, no_dns, sizeof(no_dns) - 1) == 0 &&
            snapshot->count == 0 && strtok_r(NULL, "\r\n", &save) == NULL)
            return 0;
        if (snapshot->count >= MAX_DNS_SERVERS || !is_dns_address(line))
            return -1;
        strcpy(snapshot->servers[snapshot->count++], line);
    }
    return snapshot->count > 0 ? 0 : -1;
}

static int read_dns_snapshot(struct dns_snapshot *snapshot)
{
    struct stat st;
    if (lstat(DNS_STATE_PATH, &st) != 0)
        return errno == ENOENT ? 0 : -1;
    char buffer[sizeof(*snapshot) + 1];
    size_t length = 0;
    if (read_file_checked(DNS_STATE_PATH, 0, 0077, sizeof(buffer), buffer, &length) != 0)
        return -1;
    if (length != sizeof(*snapshot))
        return -1;

    memcpy(snapshot, buffer, sizeof(*snapshot));
    if (snapshot->version != DNS_SNAPSHOT_VERSION || snapshot->owner == 0 ||
        snapshot->count > MAX_DNS_SERVERS ||
        !valid_dns_service(snapshot->service))
        return -1;
    for (uint32_t i = 0; i < snapshot->count; i++) {
        if (memchr(snapshot->servers[i], '\0', sizeof(snapshot->servers[i])) == NULL ||
            !is_dns_address(snapshot->servers[i]))
            return -1;
    }
    return 1;
}

static int read_service_dns(struct dns_snapshot *snapshot)
{
    char out[8192];
    char err[8192];
    int code = -1;
    char *const args[] = {
        "/usr/sbin/networksetup", "-getdnsservers", snapshot->service, NULL
    };
    if (run_capture(args[0], args, out, sizeof(out), err, sizeof(err), &code) != 0 ||
        code != 0)
        return -1;
    return parse_dns_servers(out, snapshot);
}

static int apply_service_dns(struct dns_snapshot *snapshot)
{
    char *args[4 + MAX_DNS_SERVERS];
    args[0] = "/usr/sbin/networksetup";
    args[1] = "-setdnsservers";
    args[2] = snapshot->service;
    if (snapshot->count == 0) {
        args[3] = "empty";
        args[4] = NULL;
    } else {
        for (uint32_t i = 0; i < snapshot->count; i++)
            args[3 + i] = snapshot->servers[i];
        args[3 + snapshot->count] = NULL;
    }
    if (run_checked(args[0], args) != 0)
        return -1;
    struct dns_snapshot actual = *snapshot;
    if (read_service_dns(&actual) != 0 || actual.count != snapshot->count)
        return -1;
    for (uint32_t i = 0; i < actual.count; i++) {
        if (!equal_dns_address(actual.servers[i], snapshot->servers[i]))
            return -1;
    }
    return 0;
}

static int has_live_session(uid_t uid)
{
    int pid;
    uint64_t start_time;
    return read_session_record(uid, &pid, &start_time) == 0 &&
           process_is_root_runtime(pid, start_time);
}

static int command_dns_apply(const struct doc *request, uid_t uid)
{
    if (!has_live_session(uid))
        return EXIT_STATE;
    struct dns_snapshot requested = { .owner = (uint32_t)uid, .count = 1 };
    if (json_string(request, 0, "service", requested.service,
                    sizeof(requested.service)) != 0 ||
        !valid_dns_service(requested.service) ||
        json_string(request, 0, "server", requested.servers[0],
                    sizeof(requested.servers[0])) != 0 ||
        !is_dns_address(requested.servers[0]))
        return EXIT_INPUT;

    struct dns_snapshot original = { .version = DNS_SNAPSHOT_VERSION, .owner = (uint32_t)uid };
    int loaded = read_dns_snapshot(&original);
    if (loaded < 0 || (loaded > 0 && original.owner != (uint32_t)uid))
        return EXIT_STATE;
    if (loaded == 0 || strcmp(original.service, requested.service) != 0) {
        if (loaded > 0 && apply_service_dns(&original) != 0)
            return EXIT_COMMAND;
        strcpy(original.service, requested.service);
        if (read_service_dns(&original) != 0)
            return EXIT_COMMAND;
        if (write_root_file(DNS_STATE_PATH, &original, sizeof(original), 0600) != 0)
            return EXIT_STATE;
    }
    return apply_service_dns(&requested) == 0 ? 0 : EXIT_COMMAND;
}

static int command_dns_restore(uid_t uid)
{
    struct dns_snapshot original = {0};
    int loaded = read_dns_snapshot(&original);
    if (loaded == 0)
        return 0;
    if (loaded < 0 || original.owner != (uint32_t)uid || has_live_session(uid))
        return EXIT_STATE;
    if (apply_service_dns(&original) != 0)
        return EXIT_COMMAND;
    (void)command_dns_flush();
    return unlink(DNS_STATE_PATH) == 0 ? 0 : EXIT_STATE;
}

static int command_dns_flush(void)
{
    char *const flush_argv[] = { "/usr/bin/dscacheutil", "-flushcache", NULL };
    if (run_checked("/usr/bin/dscacheutil", flush_argv) != 0)
        return EXIT_COMMAND;
    char *const kill_argv[] = { "/usr/bin/killall", "-HUP", "mDNSResponder", NULL };
    if (run_checked("/usr/bin/killall", kill_argv) != 0)
        return EXIT_COMMAND;
    return 0;
}

static int command_pf_apply(const struct doc *request, struct host_session *host)
{
    if (pf_reference_held(host->pf_reference) != 1 || pf_running() != 1)
        fail(EXIT_STATE, "the session does not hold an active PF enable reference");
    char mode[32];
    if (json_string(request, 0, "mode", mode, sizeof(mode)) != 0)
        return EXIT_INPUT;

    if (strcmp(mode, "main") == 0) {
        char *main_conf = xcalloc(MAX_CONFIG_BYTES, 1);
        size_t main_length = 0;
        if (read_file_checked(PF_MAIN_CONF, 0, 0022, MAX_CONFIG_BYTES, main_conf,
                              &main_length) != 0) {
            free(main_conf);
            return EXIT_STATE;
        }
        const char *anchor_line = "\nanchor \"" PF_ANCHOR "\"\n";
        if (strstr(main_conf, "anchor \"" PF_ANCHOR "\"") == NULL) {
            if (main_length + strlen(anchor_line) + 1 >= MAX_CONFIG_BYTES) {
                free(main_conf);
                return EXIT_INPUT;
            }
            memcpy(main_conf + main_length, anchor_line, strlen(anchor_line) + 1);
        }
        int result = write_root_file(PF_MAIN_STAGED, main_conf, strlen(main_conf), 0600);
        free(main_conf);
        if (result != 0)
            return EXIT_STATE;
        char *const argv[] = { "/sbin/pfctl", "-f", PF_MAIN_STAGED, NULL };
        return run_checked("/sbin/pfctl", argv) == 0 ? 0 : EXIT_COMMAND;
    }

    int anchor = strcmp(mode, "anchor") == 0;
    if (!anchor && strcmp(mode, "legacy") != 0)
        return EXIT_INPUT;
    char rules[MAX_PF_RULES_BYTES];
    if (json_string(request, 0, "rules", rules, sizeof(rules)) != 0 ||
        validate_pf_rules(rules) != 0)
        return EXIT_INPUT;
    if (write_root_file(PF_RULES_STAGED, rules, strlen(rules), 0600) != 0)
        return EXIT_STATE;

    /* Journal before changing PF so a killed helper still leaves a recovery path. */
    if (!anchor || host->pf_mode == PF_RELEASE_NONE) {
        host->pf_mode = anchor ? PF_RELEASE_ANCHOR : PF_RELEASE_DEFAULT;
        if (write_host_session(host) != 0)
            return EXIT_STATE;
    }
    if (anchor) {
        char *const argv[] = { "/sbin/pfctl", "-a", PF_ANCHOR, "-f", PF_RULES_STAGED, NULL };
        return run_checked("/sbin/pfctl", argv) == 0 ? 0 : EXIT_COMMAND;
    }
    char *const argv[] = { "/sbin/pfctl", "-f", PF_RULES_STAGED, NULL };
    return run_checked("/sbin/pfctl", argv) == 0 ? 0 : EXIT_COMMAND;
}

static int release_pf_reference(uint64_t reference)
{
    char token[32];
    snprintf(token, sizeof(token), "%llu", (unsigned long long)reference);
    char *const argv[] = { "/sbin/pfctl", "-X", token, NULL };
    (void)run_checked("/sbin/pfctl", argv);
    return pf_reference_held(reference) == 0 ? 0 : -1;
}

static int command_pf_enable(struct host_session *host)
{
    int held = pf_reference_held(host->pf_reference);
    int running = pf_running();
    if (held < 0 || running < 0)
        fail(EXIT_COMMAND, "could not verify the kernel PF state");
    if (held > 0 && running > 0)
        return 0;
    if (held > 0 && release_pf_reference(host->pf_reference) != 0)
        return EXIT_COMMAND;

    char out[8192];
    char err[8192];
    int code = -1;
    char *const argv[] = { "/sbin/pfctl", "-E", NULL };
    if (run_capture("/sbin/pfctl", argv, out, sizeof(out), err, sizeof(err), &code) != 0 ||
        code != 0)
        return EXIT_COMMAND;
    const char *token = strstr(err, "Token :");
    if (token == NULL)
        token = strstr(out, "Token :");
    if (token == NULL)
        return EXIT_COMMAND;
    token += strlen("Token :");
    while (*token == ' ' || *token == '\t')
        token++;
    const char *end;
    uint64_t reference;
    if (parse_pf_reference(token, &end, &reference) != 0 ||
        (*end != '\0' && *end != '\r' && *end != '\n'))
        return EXIT_COMMAND;
    host->pf_reference = reference;
    if (write_host_session(host) != 0) {
        (void)release_pf_reference(reference);
        return EXIT_STATE;
    }
    if (pf_reference_held(reference) == 1 && pf_running() == 1)
        return 0;
    if (release_pf_reference(reference) == 0) {
        host->pf_reference = 0;
        (void)write_host_session(host);
    }
    return EXIT_COMMAND;
}

static int command_pf_release(struct host_session *host)
{
    if (host->pf_mode == PF_RELEASE_DEFAULT) {
        char *const argv[] = { "/sbin/pfctl", "-f", PF_MAIN_CONF, NULL };
        if (run_checked("/sbin/pfctl", argv) != 0)
            return EXIT_COMMAND;
    }
    if (host->pf_mode != PF_RELEASE_NONE) {
        char *const argv[] = { "/sbin/pfctl", "-a", PF_ANCHOR, "-F", "rules", NULL };
        if (run_checked("/sbin/pfctl", argv) != 0)
            return EXIT_COMMAND;
        host->pf_mode = PF_RELEASE_NONE;
        if (write_host_session(host) != 0)
            return EXIT_STATE;
    }
    if (host->pf_reference != 0) {
        if (release_pf_reference(host->pf_reference) != 0)
            return EXIT_COMMAND;
        host->pf_reference = 0;
        if (write_host_session(host) != 0)
            return EXIT_STATE;
    }
    if (!host_session_may_be_live(host) && unlink(HOST_SESSION_PATH) != 0)
        return EXIT_STATE;
    return 0;
}

static int command_pf_status(void)
{
    char *out = xcalloc(MAX_COMMAND_OUTPUT, 1);
    char err[8192];
    int code = -1;
    char *const argv[] = { "/sbin/pfctl", "-sr", NULL };
    int result = run_capture("/sbin/pfctl", argv, out, MAX_COMMAND_OUTPUT, err,
                             sizeof(err), &code);
    if (result != 0 || code != 0) {
        free(out);
        return EXIT_COMMAND;
    }
    fputs(out, stdout);
    free(out);
    return 0;
}

int main(void)
{
    if (geteuid() != 0 || getuid() != 0)
        fail(EXIT_USAGE, "must run as root");

    const char *sudo_uid = getenv("SUDO_UID");
    if (sudo_uid == NULL || *sudo_uid == '\0')
        fail(EXIT_USAGE, "SUDO_UID is required");
    char *end = NULL;
    errno = 0;
    unsigned long uid_value = strtoul(sudo_uid, &end, 10);
    if (errno != 0 || end == NULL || *end != '\0' || uid_value == 0 ||
        uid_value > UINT_MAX)
        fail(EXIT_USAGE, "SUDO_UID is invalid");
    uid_t uid = (uid_t)uid_value;
    struct passwd *pw = getpwuid(uid);
    if (pw == NULL || pw->pw_dir == NULL || pw->pw_dir[0] != '/')
        fail(EXIT_USAGE, "invoking user has no home directory");

    char *request_json = xcalloc(MAX_REQUEST_BYTES, 1);
    size_t request_length = 0;
    if (read_stdin_all(request_json, MAX_REQUEST_BYTES, &request_length) != 0)
        fail(EXIT_INPUT, "request is too large");

    struct doc request;
    if (parse_doc(request_json, request_length, &request) != 0)
        fail(EXIT_INPUT, "request is not a JSON object");
    if (validate_unique_keys(&request, 0) != 0)
        fail(EXIT_INPUT, "request contains duplicate keys");
    long version = 0;
    if (json_int(&request, 0, "version", &version) != 0 || version != 1)
        fail(EXIT_INPUT, "unsupported contract version");
    char op[32];
    if (json_string(&request, 0, "op", op, sizeof(op)) != 0)
        fail(EXIT_INPUT, "missing operation");

    if (strcmp(op, "probe") == 0)
        return command_probe(uid, pw->pw_dir);
    if (strcmp(op, "migration_check") == 0) {
        if (verify_fixed_chain() != 0)
            fail(EXIT_STATE, "protected system directory chain is invalid");
        require_no_legacy_runtime(pw->pw_dir);
        return 0;
    }
    int pf_session_operation = strcmp(op, "pf_apply") == 0 || strcmp(op, "pf_enable") == 0 ||
                               strcmp(op, "pf_release") == 0 || strcmp(op, "pf_recover") == 0 ||
                               strcmp(op, "pf_session") == 0;
    if (pf_session_operation || strcmp(op, "start") == 0 || strcmp(op, "stop") == 0 ||
        strcmp(op, "dns_apply") == 0 || strcmp(op, "dns_restore") == 0 ||
        strcmp(op, "dns_flush") == 0) {
        if (ensure_root_layout(uid) != 0 || acquire_transition_lock(HOST_NETWORK_LOCK_PATH) < 0)
            fail(EXIT_STATE, "could not acquire the host network transition lock");
    }
    if (strcmp(op, "start") == 0) {
        struct dns_snapshot snapshot = {0};
        int loaded = read_dns_snapshot(&snapshot);
        if (loaded < 0 || (loaded > 0 && snapshot.owner != (uint32_t)uid))
            fail(EXIT_STATE, "another user's network settings must be restored before starting");
        return command_start(request_json, request_length, uid, pw->pw_dir);
    }
    if (strcmp(op, "stop") == 0)
        return command_stop(request_json, request_length, uid);
    if (strcmp(op, "dns_apply") == 0)
        return command_dns_apply(&request, uid);
    if (strcmp(op, "dns_restore") == 0)
        return command_dns_restore(uid);
    if (strcmp(op, "dns_flush") == 0)
        return has_live_session(uid) ? command_dns_flush() : EXIT_STATE;
    if (pf_session_operation) {
        struct host_session host = {0};
        uint64_t boot_time = 0;
        int loaded = load_host_session(&host, &boot_time);
        int recover = strcmp(op, "pf_recover") == 0;
        if (loaded == 0 && recover)
            return 0;
        if (loaded != 1 || host.uid != (uint32_t)uid)
            return EXIT_STATE;
        if (strcmp(op, "pf_session") == 0) {
            char launch_id[LAUNCH_ID_SIZE];
            if (json_string(&request, 0, "launch_id", launch_id, sizeof(launch_id)) != 0 ||
                !valid_launch_id(launch_id) ||
                !constant_time_equal(host.launch_id, launch_id, LAUNCH_ID_SIZE - 1))
                return EXIT_STATE;
            printf("{\"pid\":%d,\"start_time\":%llu}\n",
                   host.pid, (unsigned long long)host.start_time);
            return 0;
        }
        if (recover)
            return host_session_may_be_live(&host) ? EXIT_STATE : command_pf_release(&host);

        int pid;
        uint64_t start_time;
        if (parse_stop_identity(request_json, request_length, &pid, &start_time) != 0 ||
            pid != host.pid || start_time != host.start_time)
            return EXIT_STATE;
        if (strcmp(op, "pf_apply") == 0)
            return command_pf_apply(&request, &host);
        if (strcmp(op, "pf_enable") == 0)
            return command_pf_enable(&host);
        return command_pf_release(&host);
    }
    if (strcmp(op, "pf_status") == 0)
        return command_pf_status();

    fail(EXIT_INPUT, "unknown operation");
    return EXIT_INPUT;
}
