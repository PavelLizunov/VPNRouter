# Phase 1.1 — libbox.aar build pipeline

**Methodology ref**: `plans/android-development-methodology.md` §7 Phase 1.
**Goal**: pinned, reproducible build of `libbox.aar` from sing-box upstream
that VPNRouter.Android can `<AndroidLibrary Include=...>` against.

## Context (research findings 2026-05-12)

| Where | Что нашёл |
|---|---|
| `SagerNet/sing-box-for-android` | Public, no releases (no pre-built AAR). Default branch: `dev`. Version pinning via `version.properties` (1.14.0-alpha.24, GO_VERSION=go1.25.9). |
| `app/build.gradle.kts` | `playImplementation files("libs/libbox.aar")` — expects AAR at `app/libs/libbox.aar`, NOT committed to repo (`.gitignore`'d) |
| `SagerNet/sing-box/experimental/libbox` | Source of libbox bindings — Go package with `Box`, `Outbound`, etc. Built via `gomobile bind` |
| Mac (`slovn@192.168.0.246`) | go ❌ / gomobile ❌ / android-ndk ❌ / sing-box source ❌ |

Conclusion: need full bootstrap of Go-mobile pipeline + sing-box clone + first build.

## Pre-committed decisions

| Decision | Choice | Rationale |
|---|---|---|
| Build host | Mac (`mm4.local`, slovn@192.168.0.246) | ARM64 + already used for mac DMG builds |
| Pinned sing-box version | **`v1.14.0-alpha.24`** (matches SFA upstream version.properties) | v1.13.10 hit Go 1.25/1.26 linker error `os.checkPidfdOnce`. SFA upstream actually pins to v1.14 for Android — its `version.properties` says `VERSION_NAME=1.14.0-alpha.24` + `GO_VERSION=go1.25.9`. v1.14 has `go 1.24.7` min in go.mod and known-works with Go 1.25 gomobile. **Desktop sing-box stays at v1.13.10 — that's CLI build, different toolchain. Android binding diverges to v1.14 for stability.** |
| Pinned Go version | **Go 1.25.x** (NOT 1.26) | sing-box link fails on Go 1.26 with `invalid reference to os.checkPidfdOnce` — confirmed both v1.13.10 and v1.14 affected. Brew install `go@1.25` (keg-only). Bootstrap script uses `/opt/homebrew/opt/go@1.25/bin/go`. |
| gomobile target | `android` (NOT `androidaar`) | Produces AAR with Java/Kotlin bindings |
| Android API min | 26 (matches methodology §2 min SDK) | Covers > 95% active devices |
| Target ABI | arm64-v8a only initially | matches Phase 0 single-APK arm64 |
| NDK version | 27.2.12479018 | Already installed on Mac; works with gomobile Android binding |
| AAR storage | `VPNRouter.Android/libs/libbox.aar` | Local file ref in csproj — same pattern SFA uses |
| Commit strategy | **NOT commit AAR to git** (large binary). Cache locally + CI rebuild. | Git history small, reproducible builds |
| Re-build trigger | When `MEMORY.md` records sing-box bump OR libbox API change | Manual rebuild + version.properties update |

### Key learning from first build attempts (2026-05-12)

1. **🔴 CRITICAL: Use sagernet's gomobile FORK, NOT golang.org/x/mobile**.
   `golang.org/x/mobile/cmd/gomobile@latest` (upstream) produces a linker
   error `link: invalid reference to os.checkPidfdOnce` regardless of
   Go version (tested 1.25.10, 1.26.3) or sing-box version (v1.13.10,
   v1.14.0-alpha.24). The fix discovered in `sing-box/Makefile`:
   ```
   lib_android:
       go run ./cmd/internal/build_libbox -target android
   lib_android_new:
       go install -v github.com/sagernet/gomobile/cmd/gomobile@v0.1.12
       go install -v github.com/sagernet/gomobile/cmd/gobind@v0.1.12
   ```
   Sagernet maintains a fork with workarounds specific to sing-box's
   build needs. Pin: `v0.1.12`.

2. **Use sing-box's internal build_libbox tool, not raw `gomobile bind`**.
   `go run ./cmd/internal/build_libbox -target android` handles all the
   right flags, output paths, and post-processing. Raw `gomobile bind`
   misses required setup steps.

3. **Go-version requirement**: sing-box v1.14.0-alpha.24's go.mod declares
   `go 1.24.7` minimum. SFA upstream `version.properties` says
   `GO_VERSION=go1.25.9`. Use Go 1.25 to match upstream CI.

4. **sing-box version pin**: Android port pins to v1.14.0-alpha.24
   (matches SFA `version.properties`). Desktop CLI keeps v1.13.10
   (different binary, different release schedule). Don't try to unify —
   SFA's `version.properties` is the canonical source of "tested-together"
   versions for the Android side.

5. **`go get golang.org/x/mobile/bind` is NOT needed** when using
   sagernet/gomobile fork. The fork handles its own dependency graph.

## Phase 1.1 acceptance criteria

- [ ] **AC-1**: `~/build/libbox/libbox.aar` exists on Mac after build, ~30 MB
- [ ] **AC-2**: `aapt2 dump` libbox.aar shows `box.Box`, `box.Outbound` Java classes
- [ ] **AC-3**: `unzip -l libbox.aar | grep "jni/arm64-v8a/libgojni.so"` → present
- [ ] **AC-4**: VPNRouter.Android.csproj has `<AndroidLibrary Include="libs/libbox.aar" />` (when present)
- [ ] **AC-5**: `dotnet build VPNRouter.Android` succeeds with libbox in place
- [ ] **AC-6**: AAR fingerprint (sha256) pinned in `tools/wgturn-cli-cache`-style cache file
- [ ] **AC-7**: Build script `tools/build-libbox-aar.sh` reproducible — running it twice gives same AAR
- [ ] **AC-8**: Build instructions documented in this file (§ "How to rebuild")

## Step-by-step build plan

### Step 1.1.1 — Mac toolchain bootstrap

```bash
# On mm4.local via SSH
ssh slovn@192.168.0.246

# 1. Go via brew
brew install go
go version  # should be go1.21+ (we want 1.25 per sing-box pin but 1.21+ works)

# 2. Android command-line tools
brew install --cask android-commandlinetools
# Or manual: download from developer.android.com, unzip to ~/Library/Android/sdk/cmdline-tools/latest

# 3. NDK via sdkmanager
sdkmanager "ndk;26.1.10909125"  # match sing-box-for-android's pinned NDK
export ANDROID_NDK_HOME=$HOME/Library/Android/sdk/ndk/26.1.10909125

# 4. gomobile
go install golang.org/x/mobile/cmd/gomobile@latest
go install golang.org/x/mobile/cmd/gobind@latest
gomobile init  # initializes gomobile cache
```

### Step 1.1.2 — Clone + pin sing-box

```bash
mkdir -p ~/build && cd ~/build
git clone https://github.com/SagerNet/sing-box.git sing-box
cd sing-box
git checkout v1.13.10  # pin matches desktop bundle
```

### Step 1.1.3 — Build libbox.aar via gomobile

```bash
cd ~/build/sing-box/experimental/libbox
gomobile bind -v \
  -target=android/arm64 \
  -androidapi=26 \
  -ldflags="-s -w -X github.com/sagernet/sing-box/constant.Version=1.13.10" \
  -tags="with_gvisor,with_quic,with_grpc,with_utls,with_wireguard,with_clash_api" \
  -o ~/build/libbox/libbox.aar \
  ./
```

Expected output: `libbox.aar` (~30 MB), `libbox-sources.jar` (~1 MB).

### Step 1.1.4 — Validate AAR

```bash
cd ~/build/libbox
unzip -l libbox.aar | head -20
unzip -l libbox.aar | grep "jni/arm64-v8a/libgojni.so"  # AC-3
sha256sum libbox.aar > libbox.aar.sha256
```

### Step 1.1.5 — Transfer to dev VM + wire into VPNRouter.Android

```bash
# From dev VM:
scp slovn@192.168.0.246:~/build/libbox/libbox.aar VPNRouter.Android/libs/

# Update csproj
# Add: <AndroidLibrary Include="libs\libbox.aar" />

# Build verify
dotnet build VPNRouter.Android/VPNRouter.Android.csproj \
  -c Release /p:EnableAndroidTarget=true
```

### Step 1.1.6 — Document fingerprint

`tools/libbox-cache/version.json`:

```json
{
  "sing_box_version": "v1.13.10",
  "go_version": "1.25.x",
  "ndk_version": "26.1.10909125",
  "gomobile_version": "latest@2026-05-12",
  "target": "android/arm64",
  "androidapi": 26,
  "tags": ["with_gvisor","with_quic","with_grpc","with_utls","with_wireguard","with_clash_api"],
  "aar_sha256": "<filled after build>",
  "built_at": "2026-05-12T22:00:00Z",
  "built_by": "Pavel Lizunov on mm4.local"
}
```

## Tests (per methodology §3)

### Layer C (Unit, no device)

- `LibboxAarSanityTests` — runs `aapt2 dump` on the AAR file, asserts
  presence of `box.Box`, `box.Outbound` class entries. Fails if upstream
  API surface changes silently.
- `LibboxAarSha256Tests` — compares actual AAR sha256 with the pinned
  fingerprint in `tools/libbox-cache/version.json`. Catches accidental
  cache corruption / unwanted rebuild.

### Layer B (Integration, needs Android emulator OR device)

- `LibboxStartShutdownTests` — loads libbox.aar via reflection (using
  Java in-process — only feasible on a device). Calls `Box.New(config)`
  → `Start()` → `Stop()`. Assert no JNI crash, no leak.
- `LibboxStartWithRealConfig_TcpProbe` — full e2e: VLESS config → libbox
  starts → curl-equivalent through proxy succeeds.

These run on physical phone (A101BM) via:

```bash
# On dev VM:
ssh slovn@192.168.0.246 "ADB=/opt/homebrew/bin/adb; \$ADB install -r ~/VPNRouter/release-tmp/test.apk; \$ADB shell am instrument -w com.ninitux.vpnrouter.test/..."
```

### Layer N (Network)

Same as Integration but with real upstream VLESS server (use brat's
subscription server `de-01 main-brat`). Manual smoke.

## Performance baseline capture (per §5)

After AC-5 passes, capture:

```json
// VPNRouter.Tests/perf-baselines/android-libbox-load.json
{
  "benchmark": "android-libbox-load",
  "device": "A101BM",  // KYOCERA Android 12
  "metrics": {
    "p50_ms": <measured>,
    "p95_ms": <measured>
  },
  "raw_runs": [<10 runs>]
}
```

`tools/bench-libbox-load.sh` — N=10 runs of `adb shell am start ...
VPNRouter.App` + grep logcat for "libbox loaded in Xms" timestamp.

## How to rebuild (one-liner after bootstrap)

```bash
# On Mac:
cd ~/build/sing-box && git pull && git checkout <new-version>
~/build/build-libbox-aar.sh   # script encapsulates Step 1.1.3
```

## Risk register addendum

| Risk | Mitigation |
|---|---|
| gomobile bind fails on Mac arm64 (cross-target to android/arm64) | Test early, fall back to Linux build host if Mac native issue |
| NDK version mismatch with .NET Android workload | Lock to NDK 26.1 (methodology §2 + SFA pin) |
| libbox.aar > 50 MB exceeds APK size budget | Phase 1.3 — multi-ABI split (arm64 only initially) |
| Upstream rename `box.Box` → `box.BoxService` mid-version | Pin sing-box version, document API rename in changelog |

## Cross-references

- `plans/android-development-methodology.md` §7 Phase 1
- SFA: https://github.com/SagerNet/sing-box-for-android
- libbox source: https://github.com/SagerNet/sing-box/tree/main/experimental/libbox
- gomobile docs: https://pkg.go.dev/golang.org/x/mobile/cmd/gomobile

## Status

- [x] Plan written (this file)
- [ ] Mac toolchain installed (Step 1.1.1)
- [ ] sing-box cloned + pinned (Step 1.1.2)
- [ ] libbox.aar built first time (Step 1.1.3)
- [ ] AAR validated (Step 1.1.4)
- [ ] Transferred to VM + wired into csproj (Step 1.1.5)
- [ ] Fingerprint cached (Step 1.1.6)
- [ ] Tests Layer C added (LibboxAarSanityTests + Sha256)
- [ ] Tests Layer B added (LibboxStartShutdown on device)
- [ ] Baseline captured (perf §5)
