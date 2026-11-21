#!/bin/bash
# Benign installation and TUN lifecycle test on a fresh GitHub macOS VM.
set -euo pipefail
umask 077

if [ "${RUNNER_ENVIRONMENT:-}" != github-hosted ] ||
   [ "${GITHUB_ACTIONS:-}" != true ] || [ "${RUNNER_OS:-}" != macOS ] ||
   [ "${GITHUB_EVENT_NAME:-}" != pull_request ]; then
    echo "system acceptance requires a disposable macOS PR runner" >&2
    exit 64
fi

installer="$(cd "$(dirname "$0")/.." && pwd)/install-privileged.sh"
package="$1/Contents/MacOS"
version="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "$1/Contents/Info.plist")"
fixture_user=vpnrouterci
fixture_home="/Users/$fixture_user"
fixture_source=/Library/VPNRouterAcceptance
root_dir="/Library/Application Support/VPNRouter"
helper=/Library/PrivilegedHelperTools/com.vpnrouter.privileged
sudoers=/private/etc/sudoers.d
legacy="$sudoers/vpnrouter"
tun=utun99

for path in "$fixture_home" "$fixture_source" "$root_dir" "$helper" "$legacy"; do
    sudo -n /bin/test ! -e "$path"
    sudo -n /bin/test ! -L "$path"
done
if id "$fixture_user" >/dev/null 2>&1 || /sbin/ifconfig "$tun" >/dev/null 2>&1; then
    echo "system acceptance requires unused account and interface names" >&2
    exit 66
fi

fixture_uid=600
while id -nu "$fixture_uid" >/dev/null 2>&1; do
    fixture_uid=$((fixture_uid + 1))
    if [ "$fixture_uid" -ge 700 ]; then
        echo "no available fixture UID in 600-699" >&2
        exit 66
    fi
done
policy="$sudoers/vpnrouter-$fixture_uid"
record="$root_dir/run/$fixture_uid/session.json"
sudo -n /bin/test ! -e "$policy"
sudo -n /bin/test ! -L "$policy"
fixture_dir="$(mktemp -d "$RUNNER_TEMP/vpnrouter-system.XXXXXX")"
launcher_pid=
dns_service='VPNRouter CI DNS'
dns_service_prepared=0

as_fixture() {
    sudo -n -H -u "$fixture_user" /usr/bin/sudo -n "$helper"
}

stop_fixture() {
    local identity
    identity="$(sudo -n /bin/cat "$record" |
        jq -ce '{version:1, op:"stop", pid:.pid, start_time:.start_time}')"
    printf '%s' "$identity" | as_fixture
}

dns_service_exists() {
    /usr/sbin/networksetup -listallnetworkservices |
        sed 's/^\*[[:space:]]*//' | grep -Fxq "$dns_service"
}

cleanup() {
    local result=$?
    trap - EXIT
    trap '' INT TERM
    if sudo -n /bin/test -f "$record" && ! stop_fixture; then
        echo "exact fixture stop failed; preserving system state until runner disposal" >&2
        tail -25 "$fixture_dir/runtime.log" >&2 || true
        exit 1
    fi
    if sudo -n /bin/test -f "$root_dir/run/host-session"; then
        if ! printf '%s' '{"version":1,"op":"pf_recover"}' | as_fixture; then
            echo "fixture firewall recovery failed; preserving the helper state for runner disposal" >&2
            exit 1
        fi
    fi
    if [ "$dns_service_prepared" -eq 1 ] && dns_service_exists; then
        printf '%s' '{"version":1,"op":"dns_restore"}' | as_fixture || result=1
        sudo -n /usr/sbin/networksetup -removenetworkservice "$dns_service" || result=1
    fi
    sudo -n /bin/rm -f "$policy" "$legacy" "$helper" || result=1
    sudo -n /bin/rm -rf "$fixture_source" "$root_dir" "$fixture_home" || result=1
    if id "$fixture_user" >/dev/null 2>&1; then
        sudo -n /usr/bin/dscl . -delete "/Users/$fixture_user" || result=1
    fi
    if [ "$result" -ne 0 ] && [ -f "$fixture_dir/runtime.log" ]; then
        tail -25 "$fixture_dir/runtime.log" >&2
    fi
    rm -rf "$fixture_dir"
    exit "$result"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

echo "Checking protected directories on the hosted macOS image"
sudo -n /usr/bin/stat -f '%N %u:%g %Op' /Library "/Library/Application Support" \
    /private /private/etc "$sudoers"

sudo -n /usr/bin/dscl . -create "/Users/$fixture_user"
sudo -n /usr/bin/dscl . -create "/Users/$fixture_user" UniqueID "$fixture_uid"
sudo -n /usr/bin/dscl . -create "/Users/$fixture_user" PrimaryGroupID 20
sudo -n /usr/bin/dscl . -create "/Users/$fixture_user" NFSHomeDirectory "$fixture_home"
sudo -n /usr/bin/dscl . -create "/Users/$fixture_user" UserShell /bin/bash
sudo -n /usr/bin/install -d -o "$fixture_user" -g staff -m 0700 "$fixture_home"
if sudo -n -H -u "$fixture_user" /usr/bin/sudo -n /usr/bin/id -u >/dev/null 2>&1; then
    echo "fixture user has an unrelated administrative grant" >&2
    exit 66
fi

sudo -n /usr/bin/install -d -o root -g wheel -m 0755 "$fixture_source"
sudo -n /usr/bin/install -o root -g wheel -m 0755 \
    "$package/vpnrouter-privileged" "$fixture_source/helper"
sudo -n /usr/bin/install -o root -g wheel -m 0755 \
    "$package/sing-box" "$fixture_source/sing-box"
helper_sha="$(shasum -a 256 "$package/vpnrouter-privileged" | awk '{print $1}')"
singbox_sha="$(shasum -a 256 "$package/sing-box" | awk '{print $1}')"

# The old policy fixture authorizes only a no-op.
printf '%s ALL=(root) NOPASSWD: /usr/bin/true\n' "$fixture_user" > "$fixture_dir/legacy"
sudo -n /usr/sbin/visudo -cf "$fixture_dir/legacy"
sudo -n /usr/bin/install -o root -g wheel -m 0440 "$fixture_dir/legacy" "$legacy"
sudo -n -H -u "$fixture_user" /usr/bin/sudo -n /usr/bin/true

sed "s/{{USER}}/$fixture_user/g" "$package/sudoers.template" > "$fixture_dir/policy"
rendered_policy="$(/usr/bin/base64 < "$fixture_dir/policy" | tr -d '\n')"
sudo -n /bin/bash "$installer" \
    --policy-base64 "$rendered_policy" \
    --helper "$fixture_source/helper" --singbox "$fixture_source/sing-box" \
    --helper-sha256 "$helper_sha" --singbox-sha256 "$singbox_sha" \
    --app-version "$version" --user "$fixture_user"
sudo -n /bin/test ! -e "$legacy"
sudo -n /usr/bin/cmp "$fixture_dir/policy" "$policy"
if sudo -n -H -u "$fixture_user" /usr/bin/sudo -n /usr/bin/true >/dev/null 2>&1; then
    echo "legacy command stayed authorized after migration" >&2
    exit 66
fi

probe="$(printf '%s' '{"version":1,"op":"probe"}' | as_fixture)"
jq -e --argjson uid "$fixture_uid" --arg helper "$helper_sha" --arg runtime "$singbox_sha" \
    --arg version "$version" \
    '.ready == true and .legacy_state == "absent" and .legacy_process == false and
     .uid == $uid and .helper_sha256 == $helper and .singbox_sha256 == $runtime and
     .app_version == $version' <<< "$probe" >/dev/null
echo "Installed helper is ready under the standard user"

default_route() {
    /sbin/route -n get default | awk '
        $1 == "gateway:" && NF == 2 { gateway = $2 }
        $1 == "interface:" && NF == 2 { device = $2 }
        END {
            if (gateway == "" || device == "") exit 1
            print gateway, device
        }'
}

verify_fixture_dns() {
    local expected="$1" actual
    actual="$(/usr/sbin/networksetup -getdnsservers "$dns_service")"
    if [ "$actual" != "$expected" ]; then
        printf 'DNS fixture expected %s, got: %s\n' "$expected" "$actual" >&2
        exit 66
    fi
    local resolver_config
    resolver_config="$(/usr/sbin/scutil --dns)"
    if awk '/nameserver\[/ {print $NF}' <<< "$resolver_config" | grep -Fxq "$expected"; then
        echo "fixture address appeared among active system resolvers" >&2
        exit 66
    fi
}

baseline_route="$(default_route)"
default_device="$(awk '{print $2}' <<< "$baseline_route")"
if dns_service_exists; then
    echo "DNS fixture service name is already in use" >&2
    exit 66
fi
clang -std=c11 -Wall -Wextra -Werror -framework SystemConfiguration -framework CoreFoundation \
    "$(dirname "$0")/isolated-dns-service.c" -o "$fixture_dir/isolated-dns-service"
dns_service_prepared=1
sudo -n "$fixture_dir/isolated-dns-service" "$default_device" "$dns_service"
[ "$(/usr/sbin/networksetup -getnetworkserviceenabled "$dns_service")" = Enabled ]
sudo -n /usr/sbin/networksetup -setdnsservers "$dns_service" 192.0.2.17
verify_fixture_dns 192.0.2.17
[ "$(default_route)" = "$baseline_route" ]

config_dir="$fixture_home/Library/Application Support/VPNRouter/config"
sudo -n -H -u "$fixture_user" /bin/mkdir -p "$config_dir"
cat > "$fixture_dir/settings.json" <<'JSON'
{
  "log": { "output": "" },
  "inbounds": [{
    "type": "tun",
    "tag": "test-tun",
    "interface_name": "utun99",
    "address": ["198.18.230.1/30"],
    "auto_route": false,
    "mtu": 1280
  }],
  "outbounds": [{ "type": "direct", "tag": "direct" }],
  "route": { "final": "direct" }
}
JSON
sudo -n /usr/bin/install -o "$fixture_user" -g staff -m 0600 \
    "$fixture_dir/settings.json" "$config_dir/current.json"
config_sha="$(shasum -a 256 "$fixture_dir/settings.json" | awk '{print $1}')"

printf '{"version":1,"op":"start","config_sha256":"%s"}' "$config_sha" |
    as_fixture > "$fixture_dir/runtime.log" 2>&1 &
launcher_pid=$!
for attempt in {1..30}; do
    if /sbin/ifconfig "$tun" 2>/dev/null | grep -q 'inet 198\.18\.230\.1 '; then
        break
    fi
    kill -0 "$launcher_pid"
    sleep 1
done
/sbin/ifconfig "$tun" | grep -q 'inet 198\.18\.230\.1 '
[ "$(default_route)" = "$baseline_route" ]
pid="$(sudo -n /bin/cat "$record" | jq -er '.pid')"
[ "$(/bin/ps -p "$pid" -o uid= | tr -d ' ')" = 0 ]
echo "Root runtime created the fixture TUN with the default route intact"

jq -cn --arg service "$dns_service" \
    '{version:1, op:"dns_apply", service:$service, server:"198.18.230.1"}' | as_fixture
verify_fixture_dns 198.18.230.1
echo "DNS changed on the isolated fixture service while the session was active"

identity="$(printf '%s' '{"version":1,"op":"pf_session"}' | as_fixture)"
expected_identity="$(sudo -n /bin/cat "$record" | jq -ce '{pid,start_time}')"
[ "$(jq -ce '{pid,start_time}' <<< "$identity")" = "$expected_identity" ]

pf_request() {
    jq -c --arg operation "$1" '. + {version:1, op:$operation}' <<< "$identity" | as_fixture
}

pf_request pf_enable
host_state="$root_dir/run/host-session"
old_token="$root_dir/run/pf.token"
first_state="$(sudo -n /usr/bin/shasum -a 256 "$host_state")"
pf_request pf_enable
[ "$(sudo -n /usr/bin/shasum -a 256 "$host_state")" = "$first_state" ]
sudo -n /sbin/pfctl -s info | grep '^Status: Enabled' >/dev/null

# The v2 record stores its uint64 PF reference after the 32-byte owner record.
# Release only the fixture reference; leave its stale owner record in place.
first_reference="$(sudo -n /usr/bin/od -An -t u8 -j 32 -N 8 "$host_state" | tr -d '[:space:]')"
[[ "$first_reference" =~ ^[1-9][0-9]*$ ]]
sudo -n /sbin/pfctl -X "$first_reference" >/dev/null 2>&1
pf_request pf_enable
[ "$(sudo -n /usr/bin/shasum -a 256 "$host_state")" != "$first_state" ]
sudo -n /sbin/pfctl -s info | grep '^Status: Enabled' >/dev/null
echo "PF enable verified a replacement for the released kernel reference"
sudo -n /usr/bin/install -o root -g wheel -m 0600 "$host_state" "$fixture_dir/pf-prior-boot"
jq -c '. + {version:1, op:"pf_apply", mode:"anchor", rules:"pass out quick on lo0 all\n"}' \
    <<< "$identity" | as_fixture
sudo -n /sbin/pfctl -a com.vpnrouter/killswitch -sr | grep -q 'pass out quick on lo0'

if printf '%s' '{"version":1,"op":"pf_recover"}' | as_fixture; then
    echo "firewall recovery unexpectedly released an active session" >&2
    exit 66
fi
jq -c '. + {version:1, op:"pf_release", start_time:(.start_time + 1)}' \
    <<< "$identity" > "$fixture_dir/stale-pf-request.json"
if as_fixture < "$fixture_dir/stale-pf-request.json"; then
    echo "firewall release unexpectedly accepted a different session identity" >&2
    exit 66
fi
sudo -n /sbin/pfctl -a com.vpnrouter/killswitch -sr | grep -q 'pass out quick on lo0'
[ "$(default_route)" = "$baseline_route" ]

stop_fixture
wait "$launcher_pid" || true
sudo -n /bin/test ! -e "$record"
if /sbin/ifconfig "$tun" >/dev/null 2>&1; then
    echo "fixture TUN remained after stop" >&2
    exit 66
fi
printf '%s' '{"version":1,"op":"dns_restore"}' | as_fixture
verify_fixture_dns 192.0.2.17
printf '%s' '{"version":1,"op":"pf_recover"}' | as_fixture
[ -z "$(sudo -n /sbin/pfctl -a com.vpnrouter/killswitch -sr)" ]
sudo -n /bin/test ! -e "$host_state"
if pf_request pf_enable; then
    echo "the recovered session unexpectedly armed PF again" >&2
    exit 66
fi
# Model a leftover token-only record from the old helper. The kernel has
# already released this specific fixture reference.
sudo -n /usr/bin/install -o root -g wheel -m 0600 /dev/null "$old_token"
printf '%s' "$first_reference" | sudo -n /usr/bin/tee "$old_token" >/dev/null
printf '%s' '{"version":1,"op":"pf_recover"}' | as_fixture
sudo -n /bin/test ! -e "$old_token"

# Reuse the stopped fixture's record with a past boot timestamp. Both
# reference releases above have already taken effect in the real kernel.
sudo -n /usr/bin/install -o root -g wheel -m 0600 "$fixture_dir/pf-prior-boot" "$host_state"
printf '\001\000\000\000\000\000\000\000' |
    sudo -n /bin/dd of="$host_state" bs=1 seek=16 conv=notrunc 2>/dev/null
printf '%s' '{"version":1,"op":"pf_recover"}' | as_fixture
sudo -n /bin/test ! -e "$host_state"
[ "$(default_route)" = "$baseline_route" ]
echo "System migration, standard-user TUN, isolated-service DNS restore, and anchor recovery passed"
