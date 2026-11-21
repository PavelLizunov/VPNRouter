#!/bin/bash
# Run the installer's publication and cleanup code with temporary files.
set -euo pipefail
umask 077

installer="$(cd "$(dirname "$0")/.." && pwd)/install-privileged.sh"
fixture_dir="$(mktemp -d /tmp/vpnrouter-installer-tests.XXXXXX)"
trap 'rm -rf "$fixture_dir"' EXIT

awk '/^file_sha256\(\)/ {emit=1} /^check_source "\$helper_source"/ {emit=0} emit' \
    "$installer" > "$fixture_dir/predicates"
awk '/^cleanup\(\)/ {emit=1} /^case "\$\(readlink/ {emit=0} emit' \
    "$installer" > "$fixture_dir/rollback"
awk '/^# Publish the rule only/ {emit=1} emit' "$installer" > "$fixture_dir/publish"
[ -s "$fixture_dir/predicates" ] && [ -s "$fixture_dir/rollback" ] && [ -s "$fixture_dir/publish" ]
source "$fixture_dir/predicates"
source "$fixture_dir/rollback"

# Model installed UID and GID. Keep the filesystem's modes and link counts.
stat() {
    if [ "$1" = -f ] && [ "$2" = '%u:%g:%l' ]; then
        /usr/bin/stat -f '0:0:%l' "$3"
    else
        /usr/bin/stat "$@"
    fi
}

mv() {
    if [ "$3" = "$policy_path" ]; then
        case "$fixture_name" in
            failed-rename) return 41 ;;
            interrupted-publish) /bin/mv "$@"; exit 143 ;;
        esac
    fi
    /bin/mv "$@"
}

run_case() {
    local fixture_name="$1" expected_exit="$2" expected_rule="$3"
    local path="$fixture_dir/$fixture_name" status has_rule=no reply='{"ready":true}'
    mkdir "$path"
    # These variables stay in scope when the production EXIT trap runs.
    helper_tmp="$path/helper.stage"
    singbox_tmp="$path/runtime.stage"
    manifest_tmp="$path/manifest.stage"
    policy_tmp="$path/policy.stage"
    HELPER_DEST="$path/helper"
    ROOT_BIN="$path/runtime"
    MANIFEST="$path/manifest"
    policy_path="$path/policy"
    uid=501
    policy_pending=0
    case "$fixture_name" in
        failed-probe) reply='{"ready":false}' ;;
        legacy-probe) reply='{"ready":false,"legacy_process":true}' ;;
    esac
    printf '#!/bin/bash\n/bin/cat >/dev/null\nprintf "%%s\\n" %s\n' "'$reply'" > "$helper_tmp"
    printf '%s\n' runtime-fixture > "$singbox_tmp"
    printf '%s\n' manifest-fixture > "$manifest_tmp"
    printf '%s\n' inert-policy-fixture > "$policy_tmp"
    chmod 0755 "$helper_tmp" "$singbox_tmp"
    chmod 0600 "$manifest_tmp"
    chmod 0440 "$policy_tmp"
    helper_sha256="$(file_sha256 "$helper_tmp")"
    singbox_sha256="$(file_sha256 "$singbox_tmp")"
    manifest_sha256="$(file_sha256 "$manifest_tmp")"
    policy_sha256="$(file_sha256 "$policy_tmp")"
    case "$fixture_name" in
        bad-bytes) printf '%s\n' different-fixture >> "$singbox_tmp" ;;
        bad-mode) chmod 0700 "$singbox_tmp" ;;
        multiple-links) ln "$singbox_tmp" "$path/runtime.link" ;;
        failed-rename) printf '%s\n' existing-rule-fixture > "$policy_path" ;;
    esac

    set +e
    ( set -e; trap cleanup EXIT; source "$fixture_dir/publish" ) > "$path/log" 2>&1
    status=$?
    set -e
    if [ -f "$policy_path" ]; then has_rule=yes; fi
    printf '%s exit=%s rule=%s\n' "$fixture_name" "$status" "$has_rule"
    if [ "$status" -ne "$expected_exit" ] || [ "$has_rule" != "$expected_rule" ]; then
        cat "$path/log" >&2
        return 1
    fi
    if [ "$fixture_name" = failed-rename ]; then
        [ "$(cat "$policy_path")" = existing-rule-fixture ] || return 1
    fi
    if [ "$fixture_name" = legacy-probe ]; then
        grep -q 'vpnrouter-legacy-runtime-active' "$path/log" || return 1
    fi
    [ ! -e "$helper_tmp" ] && [ ! -e "$singbox_tmp" ] &&
        [ ! -e "$manifest_tmp" ] && [ ! -e "$policy_tmp" ]
}

run_case success 0 yes
run_case failed-probe 78 no
run_case legacy-probe 78 no
run_case bad-bytes 66 no
run_case bad-mode 66 no
run_case multiple-links 66 no
run_case failed-rename 41 yes
run_case interrupted-publish 143 no
