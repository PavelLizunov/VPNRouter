#!/bin/bash
#
# VPNRouter macOS privileged installation and migration.
#
# This script is executed once by the app through an administrator-approved
# osascript prompt. It installs the root-owned helper and runtime, removes the
# legacy shared sudoers file, and writes one per-user policy line. It never
# copies a user-writable script into a privileged location and never uses eval.
#
# Contract: packaging/macos/README.md.

set -euo pipefail
umask 077
export PATH=/usr/bin:/bin:/usr/sbin:/sbin

HELPER_DEST="/Library/PrivilegedHelperTools/com.vpnrouter.privileged"
ROOT_DIR="/Library/Application Support/VPNRouter"
ROOT_BIN="$ROOT_DIR/bin/sing-box"
MANIFEST="$ROOT_DIR/install.json"
SUDOERS_DIR="/private/etc/sudoers.d"
LEGACY_POLICY="$SUDOERS_DIR/vpnrouter"

policy_base64=""
helper_source=""
singbox_source=""
helper_sha256=""
singbox_sha256=""
app_version=""
user_name=""

usage() {
    echo "usage: install-privileged.sh --policy-base64 DATA --helper PATH --singbox PATH --helper-sha256 HEX --singbox-sha256 HEX --app-version VERSION --user NAME" >&2
    exit 64
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --policy-base64) policy_base64="${2:-}"; shift 2 ;;
        --helper) helper_source="${2:-}"; shift 2 ;;
        --singbox) singbox_source="${2:-}"; shift 2 ;;
        --helper-sha256) helper_sha256="${2:-}"; shift 2 ;;
        --singbox-sha256) singbox_sha256="${2:-}"; shift 2 ;;
        --app-version) app_version="${2:-}"; shift 2 ;;
        --user) user_name="${2:-}"; shift 2 ;;
        *) usage ;;
    esac
done

[ "$(id -u)" -eq 0 ] || { echo "install-privileged: administrator rights are required" >&2; exit 77; }
[ -n "$policy_base64" ] && [ -n "$helper_source" ] && [ -n "$singbox_source" ] || usage
[ "${#policy_base64}" -le 4096 ] || usage
[ -n "$app_version" ] && [ -n "$user_name" ] || usage
[[ "$user_name" =~ ^[A-Za-z0-9._-]+$ ]] || { echo "install-privileged: invalid user name" >&2; exit 65; }
[[ "$helper_sha256" =~ ^[0-9a-f]{64}$ ]] || { echo "install-privileged: invalid helper hash" >&2; exit 65; }
[[ "$singbox_sha256" =~ ^[0-9a-f]{64}$ ]] || { echo "install-privileged: invalid sing-box hash" >&2; exit 65; }
[[ "$app_version" =~ ^[A-Za-z0-9._-]+$ ]] || { echo "install-privileged: invalid version" >&2; exit 65; }

uid="$(id -u "$user_name")"
case "$uid" in
    ''|0|*[!0-9]*) echo "install-privileged: invalid uid" >&2; exit 65 ;;
esac
[ "$uid" -gt 0 ] || { echo "install-privileged: refusing root as the invoking user" >&2; exit 65; }

check_source() {
    local path="$1"
    case "$path" in
        /*) ;;
        *) echo "install-privileged: source path must be absolute" >&2; exit 66 ;;
    esac
    [ -f "$path" ] || { echo "install-privileged: missing source file" >&2; exit 66; }
    [ ! -L "$path" ] || { echo "install-privileged: source is a symlink" >&2; exit 66; }
    local mode
    mode="$(stat -f '%Lp' "$path")"
    [ $((0$mode & 0022)) -eq 0 ] || { echo "install-privileged: source is group/world writable" >&2; exit 66; }
    local owner
    owner="$(stat -f '%u' "$path")"
    [ "$owner" = "0" ] || [ "$owner" = "$uid" ] || {
        echo "install-privileged: source is not owned by root or the invoking user" >&2
        exit 66
    }
}

check_source_chain() {
    local path="$1"
    local directory
    directory="$(dirname "$path")"
    while [ "$directory" != "/" ]; do
        [ -d "$directory" ] && [ ! -L "$directory" ] || {
            echo "install-privileged: source directory is not a real directory" >&2
            exit 66
        }
        local mode
        mode="$(stat -f '%Lp' "$directory")"
        [ $((0$mode & 0002)) -eq 0 ] || {
            echo "install-privileged: source directory is world writable" >&2
            exit 66
        }
        local owner
        owner="$(stat -f '%u' "$directory")"
        [ "$owner" = "0" ] || [ "$owner" = "$uid" ] || {
            echo "install-privileged: source directory is not owned by root or the invoking user" >&2
            exit 66
        }
        directory="$(dirname "$directory")"
    done
}

check_root_dir() {
    local path="$1"
    local expected="$2"
    local group="${3:-0}"
    [ -d "$path" ] && [ ! -L "$path" ] || {
        echo "install-privileged: protected directory is missing or linked: $path" >&2
        exit 66
    }
    if [ "$(stat -f '%u' "$path")" != "0" ] ||
        { [ "$group" != "-" ] && [ "$(stat -f '%g' "$path")" != "$group" ]; }; then
        echo "install-privileged: protected directory has unexpected ownership: $path" >&2
        exit 66
    fi
    local mode
    mode="$(stat -f '%Op' "$path")"
    # Match the native helper, which ignores the sticky bit on protected dirs.
    mode=$((0$mode & 06777))
    local expected_value
    expected_value=$((0$expected))
    [ "$mode" -eq "$expected_value" ] || {
        echo "install-privileged: protected directory has an unexpected mode: $path" >&2
        exit 66
    }
}

ensure_root_dir() {
    local path="$1"
    local mode="$2"
    if [ ! -e "$path" ] && [ ! -L "$path" ]; then
        install -d -o root -g wheel -m "$mode" "$path"
    fi
    check_root_dir "$path" "$mode"
}

file_sha256() {
    shasum -a 256 "$1" | awk '{print $1}'
}

check_root_file() {
    local path="$1"
    local expected_mode="$2"
    local metadata mode
    [ -f "$path" ] && [ ! -L "$path" ] || {
        echo "install-privileged: protected file is missing or linked: $path" >&2
        exit 66
    }
    metadata="$(stat -f '%u:%g:%l' "$path")"
    mode="$(stat -f '%Op' "$path")"
    if [ "$metadata" != "0:0:1" ] || [ "$((0$mode & 07777))" -ne "$((0$expected_mode))" ]; then
        echo "install-privileged: protected file has unexpected metadata: $path" >&2
        exit 66
    fi
}

check_replacement_target() {
    if [ -e "$1" ] || [ -L "$1" ]; then
        check_root_file "$1" "$2"
    fi
}

verify_root_artifact() {
    local path="$1"
    check_root_file "$path" "$2"
    if [ "$(file_sha256 "$path")" != "$3" ]; then
        echo "install-privileged: protected file hash mismatch: $path" >&2
        exit 66
    fi
}

check_source "$helper_source"
check_source "$singbox_source"
check_source_chain "$helper_source"
check_source_chain "$singbox_source"

actual_helper_sha="$(file_sha256 "$helper_source")"
actual_singbox_sha="$(file_sha256 "$singbox_source")"
[ "$actual_helper_sha" = "$helper_sha256" ] || { echo "install-privileged: helper hash mismatch" >&2; exit 66; }
[ "$actual_singbox_sha" = "$singbox_sha256" ] || { echo "install-privileged: sing-box hash mismatch" >&2; exit 66; }

policy_path="$SUDOERS_DIR/vpnrouter-$uid"
policy_tmp="$SUDOERS_DIR/.vpnrouter-$uid.tmp"
manifest_tmp="$MANIFEST.tmp"
lock_path="$ROOT_DIR/.install.lock"
helper_tmp="$HELPER_DEST.tmp"
singbox_tmp="$ROOT_BIN.tmp"
policy_pending=0

cleanup() {
    if [ "$policy_pending" -eq 1 ] && [ ! -e "$policy_tmp" ] && ! rm -f "$policy_path"; then
        echo "install-privileged: could not remove the unconfirmed managed rule" >&2
    fi
    rm -f "$policy_tmp" "$manifest_tmp" "$helper_tmp" "$singbox_tmp" 2>/dev/null || true
}

case "$(readlink /etc)" in
    private/etc|/private/etc) ;;
    *) echo "install-privileged: unexpected /etc target" >&2; exit 66 ;;
esac

check_root_dir /Library 0755
# Application Support may be root:admin. Mode 0755 denies group writes.
check_root_dir "/Library/Application Support" 0755 -
check_root_dir /private 0755
check_root_dir /private/etc 0755
check_root_dir "$SUDOERS_DIR" 0755
ensure_root_dir /Library/PrivilegedHelperTools 0755
ensure_root_dir "$ROOT_DIR" 0755
# Keep the descriptor open until this shell exits. The kernel releases its
# lock even if the installer is interrupted before the cleanup trap can run.
if [ -L "$lock_path" ] || { [ -e "$lock_path" ] &&
    { [ ! -f "$lock_path" ] || [ "$(stat -f '%u:%g %Lp' "$lock_path")" != "0:0 600" ]; }; }; then
    echo "install-privileged: invalid installation lock: $lock_path" >&2
    exit 66
fi
exec 9>>"$lock_path"
if ! /usr/bin/perl -MFcntl=:flock -e 'flock(STDIN, LOCK_EX | LOCK_NB) or exit 75' <&9; then
    echo "install-privileged: another installation is in progress" >&2
    exit 75
fi
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
rm -f "$policy_tmp" "$manifest_tmp" "$helper_tmp" "$singbox_tmp"

# Only the two managed comments and the exact helper rule may enter sudoers.
printf '%s' "$policy_base64" | /usr/bin/base64 -D > "$policy_tmp"
expected_rule="$user_name ALL=(root) NOPASSWD: $HELPER_DEST"
awk -v expected="$expected_rule" '
    NR == 1 && $0 == "# vpnrouter policy v2 (managed; do not edit)" { next }
    NR == 2 && $0 == "# Root-owned launcher; no user-writable executables or wildcard arguments." { next }
    NR == 3 && $0 == expected { next }
    { invalid = 1 }
    END { if (invalid || NR != 3) exit 1 }
' "$policy_tmp" || {
    echo "install-privileged: policy must contain exactly the managed helper rule" >&2
    exit 65
}
chown root:wheel "$policy_tmp"
chmod 0440 "$policy_tmp"
visudo -cf "$policy_tmp" >/dev/null
policy_sha256="$(file_sha256 "$policy_tmp")"

ensure_root_dir "$ROOT_DIR/bin" 0755
ensure_root_dir "$ROOT_DIR/run" 0700
ensure_root_dir "$ROOT_DIR/assets" 0700
check_replacement_target "$HELPER_DEST" 0755
check_replacement_target "$ROOT_BIN" 0755
check_replacement_target "$MANIFEST" 0600
check_replacement_target "$policy_path" 0440

install -o root -g wheel -m 0755 "$helper_source" "$helper_tmp"
install -o root -g wheel -m 0755 "$singbox_source" "$singbox_tmp"
verify_root_artifact "$helper_tmp" 0755 "$helper_sha256"
verify_root_artifact "$singbox_tmp" 0755 "$singbox_sha256"

check_legacy_runtime() {
    printf '%s' '{"version":1,"op":"migration_check"}' |
        env -i SUDO_UID="$uid" "$helper_tmp" 9>&-
}

# Check before removal so an active legacy instance can still be stopped.
check_legacy_runtime

# Remove the legacy grant before replacing executable artifacts.
if [ -e "$LEGACY_POLICY" ] || [ -L "$LEGACY_POLICY" ]; then
    rm -f "$LEGACY_POLICY"
fi
if [ -e "$LEGACY_POLICY" ] || [ -L "$LEGACY_POLICY" ]; then
    echo "install-privileged: legacy policy is still present after removal" >&2
    exit 66
fi

check_legacy_runtime

cat > "$manifest_tmp" <<EOF
{"schema_version":1,"policy_version":2,"app_version":"$app_version","helper_sha256":"$helper_sha256","singbox_sha256":"$singbox_sha256"}
EOF
chown root:wheel "$manifest_tmp"
chmod 0600 "$manifest_tmp"
manifest_sha256="$(file_sha256 "$manifest_tmp")"

# Publish the rule only after independently checking the installed set.
mv -f "$helper_tmp" "$HELPER_DEST"
mv -f "$singbox_tmp" "$ROOT_BIN"
mv -f "$manifest_tmp" "$MANIFEST"
verify_root_artifact "$HELPER_DEST" 0755 "$helper_sha256"
verify_root_artifact "$ROOT_BIN" 0755 "$singbox_sha256"
verify_root_artifact "$MANIFEST" 0600 "$manifest_sha256"
verify_root_artifact "$policy_tmp" 0440 "$policy_sha256"

# Arm cleanup before the atomic rename. If rename fails, the staged file is
# still present and cleanup keeps any existing policy.
policy_pending=1
mv -f "$policy_tmp" "$policy_path"
verify_root_artifact "$policy_path" 0440 "$policy_sha256"

# A failed installed-helper probe removes the newly published rule.
probe_output="$(printf '%s' '{"version":1,"op":"probe"}' | env -i SUDO_UID="$uid" "$HELPER_DEST" 9>&-)" || {
    echo "install-privileged: installed helper probe failed" >&2
    exit 78
}
case "$probe_output" in
    *'"ready":true'*) ;;
    *) echo "install-privileged: helper is not ready after installation" >&2; exit 78 ;;
esac

policy_pending=0
echo "install-privileged: managed policy=2 uid=$uid"
