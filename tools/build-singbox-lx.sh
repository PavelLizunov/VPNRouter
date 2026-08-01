#!/usr/bin/env bash
# Build sing-box-lx (Leadaxe fork) with AmneziaWG (with_awg) + XHTTP for macOS/Linux.
# Darwin/Linux port of tools/build-singbox-lx.ps1 — SAME pinned commits, SAME 4
# conn/bind_std.go patches. All four live in one cross-platform file and are safe
# off Windows:
#   - H4 reserved-byte receive-clear GATE: pure AmneziaWG protocol fix, ESSENTIAL
#     (without it every AWG transport packet is misclassified -> no data flows).
#   - OOB nil-guard (golang/go#77875): the pooled OOB is a non-nil zero-length slice
#     when controlSize==0, which is ALSO the case on macOS (per the upstream note);
#     leaving it nil is correct everywhere.
#   - WriteMsgUDP send site + WSAENOBUFS(10055) retry: the errno-10055 check never
#     matches off Windows (darwin ENOBUFS==55), so the retry is dead-but-harmless;
#     the nil-guard half still helps.
# Output: a `sing-box` binary (no .exe). Keep the pins in sync with the .ps1.
set -euo pipefail

LX_REPO="https://github.com/Leadaxe/sing-box-lx"
LX_COMMIT="c7a2592e750406ade9ebaae1d0fdb7482fc0773e"
WG_REPO="https://github.com/Leadaxe/wireguard-go-awg2-lx"
WG_BRANCH="lx"
WG_COMMIT="0c0c10b5d3236796bd3832a6813223d6dc7d0bb1"
# Targeted upstream backports (applied build-time on the pinned fork tree). The working
# tree is a Leadaxe/sing-box-lx clone, so origin points at Leadaxe and is NOT the proven
# source of these two SagerNet commits — fetch the EXACT SHAs from the immutable upstream
# URL, never origin, never a branch/tag (both mutable). Keep in sync with the .ps1.
UPSTREAM_REPO="https://github.com/SagerNet/sing-box.git"
TUN_BACKPORT="0b7ffbaafa5f060dd8c762dfbc751d592cba1fea"   # F1: sing-tun v0.8.11 (TUN system-stack TCP NAT collision)
DNS_BACKPORT="72a8723e13b9574664f4c78e588069fa4aca6fc9"   # F2: DNS nested single-flight self-deadlock
TAGS="with_gvisor,with_quic,with_dhcp,with_wireguard,with_utls,with_clash_api,with_naive_outbound,with_purego,badlinkname,tfogo_checklinkname0,with_xhttp,with_awg"
VER="${1:-1.13.13-lx-awg}"
OUT="${2:-$PWD/sing-box-lx}"
GO="${GO:-go}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# Fail early if the Go toolchain is older than the fork's go.mod directive (1.24.7).
command -v git >/dev/null   || { echo "FATAL: git not found";     exit 1; }
command -v python3 >/dev/null || { echo "FATAL: python3 not found"; exit 1; }
"$GO" version >/dev/null 2>&1 || { echo "FATAL: Go toolchain '$GO' not runnable"; exit 1; }
MINGO="1.24.7"; HAVEGO="$("$GO" env GOVERSION 2>/dev/null | sed 's/^go//')"
if [ "$(printf '%s\n%s\n' "$MINGO" "$HAVEGO" | sort -V | head -1)" != "$MINGO" ]; then
  echo "FATAL: Go >=$MINGO required for the fork (have '${HAVEGO:-none}'). Set GO=/path/to/newer/go."; exit 1
fi

# Resolve OUT to an ABSOLUTE path now, while pwd is still the caller's dir. `go build`
# runs after `cd "$SRC"` (a temp workdir), so a relative OUT (e.g. the Linux CI's
# publish/linux-x64/sing-box) would land inside $WORK and get rm'd by the EXIT trap —
# the binary built fine but vanished (r14 Linux CI). Mac passed an absolute path so it
# was unaffected. Also ensure the target dir exists.
case "$OUT" in /*) ;; *) OUT="$PWD/$OUT" ;; esac
mkdir -p "$(dirname "$OUT")"

# P2 supply-chain (2026-07-10): fail CLOSED if a pinned checkout drifts from the
# expected commit (a moved tag/branch would otherwise bundle an unpinned tree).
assert_git_head() {  # <repo-dir> <expected-sha> <label>
  head="$(git -C "$1" rev-parse HEAD)"
  if [ "$head" != "$2" ]; then
    echo "ERROR: $3 HEAD drift: expected $2, got $head — refusing to build an unpinned core." >&2
    exit 1
  fi
  echo "       $3 HEAD pinned OK ($head)"
}

SRC="$WORK/sing-box-lx"
echo "[1/4] Clone sing-box-lx @ $LX_COMMIT"
git clone --quiet "$LX_REPO" "$SRC"
git -C "$SRC" checkout --quiet "$LX_COMMIT"
assert_git_head "$SRC" "$LX_COMMIT" "sing-box-lx"

# Targeted upstream backports on the pinned fork tree (no pin rotation): cherry-pick
# --no-commit (no Git identity needed); set -e + grep -Fq gates below fail closed.
echo "[1.5/4] Backport upstream fixes (sing-tun NAT + DNS single-flight)"
git -C "$SRC" fetch --quiet "$UPSTREAM_REPO" "$TUN_BACKPORT" "$DNS_BACKPORT"
git -C "$SRC" cherry-pick --no-commit "$TUN_BACKPORT"
git -C "$SRC" cherry-pick --no-commit "$DNS_BACKPORT"

grep -Fq "github.com/sagernet/sing-tun v0.8.11" "$SRC/go.mod" \
  || { echo "FATAL: go.mod missing 'github.com/sagernet/sing-tun v0.8.11' after TUN backport ($TUN_BACKPORT) — sing-tun bump did not apply" >&2; exit 1; }
if grep -Fq "github.com/sagernet/sing-tun v0.8.10" "$SRC/go.mod"; then
  echo "FATAL: go.mod still pins 'github.com/sagernet/sing-tun v0.8.10' after TUN backport ($TUN_BACKPORT) — TUN TCP NAT collision fix NOT in tree" >&2; exit 1
fi
grep -Fq "compatible.Map[transportCacheKey, chan struct{}]" "$SRC/dns/client.go" \
  || { echo "FATAL: dns/client.go missing 'compatible.Map[transportCacheKey, chan struct{}]' after DNS backport ($DNS_BACKPORT)" >&2; exit 1; }
grep -Fq "cacheKey := transportCacheKey{Question: question, transportTag: transport.Tag()}" "$SRC/dns/client.go" \
  || { echo "FATAL: dns/client.go missing transportCacheKey cache-key construction after DNS backport ($DNS_BACKPORT)" >&2; exit 1; }
if grep -Fq "compatible.Map[dns.Question, chan struct{}]" "$SRC/dns/client.go"; then
  echo "FATAL: dns/client.go still uses 'compatible.Map[dns.Question, chan struct{}]' after DNS backport ($DNS_BACKPORT) — pre-fix map still present" >&2; exit 1
fi
echo "  backported sing-tun v0.8.11 (TUN NAT) + DNS single-flight deadlock fix; fail-closed assertions passed"

WG="$SRC/submodules/wireguard-go"
echo "[2/4] Clone wireguard-go-awg2-lx @ $WG_COMMIT -> submodules/wireguard-go"
rm -rf "$WG"
git clone --quiet -b "$WG_BRANCH" "$WG_REPO" "$WG"
git -C "$WG" checkout --quiet "$WG_COMMIT"
assert_git_head "$WG" "$WG_COMMIT" "wireguard-go-awg2-lx"

echo "[2.5/4] Patch conn/bind_std.go (H4 gate + OOB nil-guard + ENOBUFS retry)"
python3 - "$WG/conn/bind_std.go" <<'PYEOF'
import sys
p = sys.argv[1]
s = open(p, encoding='utf-8').read()
patches = [
  ('\t"syscall"', '\t"syscall"\n\t"time"', 'time import'),
  ('msgs[i].OOB = make([]byte, controlSize)',
   'if controlSize > 0 { msgs[i].OOB = make([]byte, controlSize) }', 'OOB nil-guard'),
  ('_, _, err = conn.WriteMsgUDP(msg.Buffers[0], msg.OOB, msg.Addr.(*net.UDPAddr))',
   'oob := msg.OOB; if len(oob) == 0 { oob = nil }; for _rn := 0; ; _rn++ { _, _, err = conn.WriteMsgUDP(msg.Buffers[0], oob, msg.Addr.(*net.UDPAddr)); if err == nil || _rn >= 8 || !errors.Is(err, syscall.Errno(10055)) { break }; time.Sleep(time.Duration(80*(_rn+1)) * time.Microsecond) }',
   'send + ENOBUFS retry'),
  ('common.ClearArray(bufs[i][1:4])',
   'if _, resvLoaded := s.reservedForEndpoint[M.AddrPortFromNet(msg.Addr)]; resvLoaded { common.ClearArray(bufs[i][1:4]) }',
   'H4 reserved-byte gate'),
]
for old, new, name in patches:
    c = s.count(old)
    if c != 1:
        sys.exit("FATAL: '%s' matched %d times (expected 1) — fork source changed, re-vet patches" % (name, c))
    s = s.replace(old, new)
open(p, 'w', encoding='utf-8').write(s)
print("  patched 4/4")
PYEOF

echo "[3/4] go build -tags"
cd "$SRC"
CGO_ENABLED=0 "$GO" build -trimpath -tags "$TAGS" \
  -ldflags "-checklinkname=0 -X github.com/sagernet/sing-box/constant.Version=$VER" \
  -o "$OUT" ./cmd/sing-box

echo "[4/4] Verify build-info -tags carries with_awg + with_xhttp (NOT forgeable)"
# Use `go version -m` (reads the embedded build settings) rather than running the
# binary: the recorded `build -tags=...` is what Go compiled in, and it works for
# CROSS-compiled outputs too (a linux ELF can't exec on a darwin host). Go silently
# ignores unknown -tags, so a dropped tag / unresolved wireguard-go replace yields a
# feature-less binary that ships green then FATALs every AWG/xhttp config at runtime.
TAGSINFO="$("$GO" version -m "$OUT" 2>/dev/null | grep -E 'build[[:space:]]+-tags=' || true)"
echo "  $TAGSINFO"
echo "$TAGSINFO" | grep -q "with_awg"   || { echo "FATAL: built binary MISSING with_awg — do NOT bundle";   exit 1; }
echo "$TAGSINFO" | grep -q "with_xhttp" || { echo "FATAL: built binary MISSING with_xhttp — do NOT bundle"; exit 1; }
# Native build only: smoke the version line too (cross-compiled outputs can't run here).
if [ "$("$GO" env GOOS)" = "$("$GO" env GOHOSTOS)" ] && [ "$("$GO" env GOARCH)" = "$("$GO" env GOHOSTARCH)" ]; then
  "$OUT" version | head -1
fi
echo "OK -> $OUT"
