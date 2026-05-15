#!/usr/bin/env bash
# build-libbox-aar.sh — reproducible build of libbox.aar from sing-box
# source. Runs on Mac (mm4.local) via SSH. Phase 1.1 of Android port.
#
# Methodology ref: plans/android-development-methodology.md §7 Phase 1
# Plan ref:        plans/android-phase-1-libbox-build.md
#
# Usage:
#   bash tools/build-libbox-aar.sh                  # default pinned version
#   SING_BOX_VERSION=v1.13.11 bash tools/build-libbox-aar.sh  # override
#
# What it does (idempotent):
#   1. Refresh sing-box clone at ~/build/sing-box (checks out pinned tag)
#   2. Verify gomobile toolchain (Go + gomobile + NDK)
#   3. Run gomobile bind for android/arm64 + selected build tags
#   4. Output to ~/build/libbox-out/libbox.aar
#   5. Compute sha256, write tools/libbox-cache/version.json fingerprint
#
# Exit codes:
#   0 — AAR built + validated successfully
#   1 — toolchain missing (fix per android-bootstrap.ps1 / methodology §8)
#   2 — gomobile bind failed (see captured stderr)
#   3 — AAR validation failed (missing jni/arm64-v8a/libgojni.so etc)

set -euo pipefail

SING_BOX_VERSION="${SING_BOX_VERSION:-v1.13.10}"
BUILD_DIR="${BUILD_DIR:-$HOME/build}"
SING_BOX_DIR="$BUILD_DIR/sing-box"
OUT_DIR="$BUILD_DIR/libbox-out"
TAGS="${TAGS:-with_gvisor,with_quic,with_grpc,with_utls,with_wireguard,with_clash_api}"
ANDROID_API="${ANDROID_API:-26}"
ANDROID_TARGET="${ANDROID_TARGET:-android/arm64}"

# Locate toolchain — set defaults if env not already configured.
ANDROID_SDK_ROOT="${ANDROID_SDK_ROOT:-/opt/homebrew/share/android-commandlinetools}"
ANDROID_NDK_HOME="${ANDROID_NDK_HOME:-$ANDROID_SDK_ROOT/ndk/27.2.12479018}"
export ANDROID_SDK_ROOT ANDROID_NDK_HOME
export ANDROID_HOME="$ANDROID_SDK_ROOT"
export PATH="/opt/homebrew/bin:$HOME/go/bin:$PATH"

echo "── build-libbox-aar.sh ──"
echo "  sing-box version:  $SING_BOX_VERSION"
echo "  build tags:        $TAGS"
echo "  Android API:       $ANDROID_API"
echo "  target:            $ANDROID_TARGET"
echo "  output:            $OUT_DIR/libbox.aar"

# ── Step 1: toolchain check ──
for tool in git go gomobile; do
  if ! command -v "$tool" >/dev/null 2>&1; then
    echo "[FAIL] $tool not in PATH. See tools/android-bootstrap.ps1." >&2
    exit 1
  fi
done
if [ ! -d "$ANDROID_NDK_HOME" ]; then
  echo "[FAIL] ANDROID_NDK_HOME=$ANDROID_NDK_HOME does not exist." >&2
  exit 1
fi
echo "[OK] toolchain present: go $(go version | awk '{print $3}'), gomobile $(which gomobile)"

# ── Step 2: refresh sing-box source ──
mkdir -p "$BUILD_DIR"
if [ -d "$SING_BOX_DIR/.git" ]; then
  echo "[INFO] refreshing existing sing-box clone..."
  (cd "$SING_BOX_DIR" && git fetch --tags --quiet)
else
  echo "[INFO] cloning sing-box..."
  git clone --quiet https://github.com/SagerNet/sing-box.git "$SING_BOX_DIR"
fi
(cd "$SING_BOX_DIR" && git checkout --quiet "$SING_BOX_VERSION")
ACTUAL_VERSION=$(cd "$SING_BOX_DIR" && git describe --tags --exact-match 2>/dev/null || git rev-parse --short HEAD)
echo "[OK] sing-box checked out: $ACTUAL_VERSION"

# ── Step 3: gomobile bind ──
mkdir -p "$OUT_DIR"
cd "$SING_BOX_DIR/experimental/libbox"
echo "[INFO] running gomobile bind (this can take 3-5 min on M-series)..."
if ! gomobile bind -v \
    -target="$ANDROID_TARGET" \
    -androidapi="$ANDROID_API" \
    -ldflags="-s -w -X github.com/sagernet/sing-box/constant.Version=$SING_BOX_VERSION" \
    -tags="$TAGS" \
    -o "$OUT_DIR/libbox.aar" \
    ./ 2>&1 | tee "$OUT_DIR/gomobile.log" | tail -20; then
  echo "[FAIL] gomobile bind exited non-zero. Full log: $OUT_DIR/gomobile.log" >&2
  exit 2
fi
echo "[OK] AAR produced: $(ls -lh $OUT_DIR/libbox.aar | awk '{print $5}')"

# ── Step 4: validate AAR ──
if ! unzip -l "$OUT_DIR/libbox.aar" | grep -q "jni/arm64-v8a/libgojni.so"; then
  echo "[FAIL] AAR missing jni/arm64-v8a/libgojni.so — gomobile bind incomplete" >&2
  exit 3
fi
echo "[OK] AAR contains arm64-v8a JNI lib"

if ! unzip -l "$OUT_DIR/libbox.aar" | grep -q "classes.jar"; then
  echo "[FAIL] AAR missing classes.jar — gomobile bind incomplete" >&2
  exit 3
fi
echo "[OK] AAR contains classes.jar"

# ── Step 5: fingerprint ──
SHA256=$(shasum -a 256 "$OUT_DIR/libbox.aar" | awk '{print $1}')
SIZE=$(stat -f '%z' "$OUT_DIR/libbox.aar" 2>/dev/null || stat -c '%s' "$OUT_DIR/libbox.aar")
TIMESTAMP=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
GO_VER=$(go version | awk '{print $3}')
NDK_VER=$(basename "$ANDROID_NDK_HOME")

echo "[OK] sha256: $SHA256"
echo "[OK] size:   $SIZE bytes"

# Write fingerprint
cat > "$OUT_DIR/version.json" <<EOF
{
  "sing_box_version": "$ACTUAL_VERSION",
  "sing_box_version_requested": "$SING_BOX_VERSION",
  "go_version": "$GO_VER",
  "ndk_version": "$NDK_VER",
  "target": "$ANDROID_TARGET",
  "androidapi": $ANDROID_API,
  "tags": "$TAGS",
  "aar_sha256": "$SHA256",
  "aar_size_bytes": $SIZE,
  "built_at": "$TIMESTAMP",
  "built_by": "$(whoami)@$(hostname)"
}
EOF

echo
echo "═══════════════════════════════════════"
echo "✓ libbox.aar build succeeded"
echo "  Output: $OUT_DIR/libbox.aar"
echo "  Fingerprint: $OUT_DIR/version.json"
echo "═══════════════════════════════════════"
echo
echo "Next steps:"
echo "  1. scp slovn@192.168.0.246:$OUT_DIR/libbox.aar VPNRouter.Android/libs/"
echo "  2. scp slovn@192.168.0.246:$OUT_DIR/version.json tools/libbox-cache/"
echo "  3. Add <AndroidLibrary Include=\"libs\\libbox.aar\" /> to VPNRouter.Android.csproj"
echo "  4. dotnet build VPNRouter.Android — verify reference resolves"
