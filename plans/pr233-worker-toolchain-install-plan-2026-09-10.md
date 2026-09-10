# PR233 worker toolchain preparation

Status: PLAN ONLY. Owner requested a plan, not installation. No packages, SDKs, profiles or licenses changed.

## Scope

linux-worker: .NET SDK10.0.301 x64 in dedicated user-owned directory; JDK17, Android command-line tools, platforms;android-36, build-tools;36.0.0 and .NET Android workload with --skip-manifest-update. Android compile target36 does not change supported minimum API23 or legacy libbox1.13.10. mac-worker: .NET SDK10.0.301 arm64 only initially. Existing .NET8/Homebrew/Xcode and default shell PATH untouched. Windows package/update/native checks use existing CI; no Windows SDK provisioning.

Use task-scoped DOTNET_ROOT/PATH/JAVA_HOME/ANDROID_HOME and isolated NuGet/CLI homes. Proposed roots under each worker's ~/.local/share/vpnrouter-build-tools/; inspect existence before choosing exact subdirectories and never overwrite existing installations. Do not install Go merely because old build scripts needed it: #233 consumes pinned desktop release binaries; inspect actual remaining build dependencies before requesting extra packages.

## Installation approval gates

1. Fresh identity/resource/job preflight immediately before mutation. Prior observations: Linux53GiB free, macOS41GiB; not a current guarantee.
2. Read official vendor download metadata and license terms; select exact SDK/JDK/Android tool archive versions and hashes before fetching/installing. Verify downloaded archive hashes and supported OS/architecture. No curl-to-shell, no unreviewed remote installer, no production secrets.
3. Present exact version/path/download manifest and actual storage estimate. Stop if SDK10.0.301 is unavailable rather than silently changing global.json. System prerequisite packages or elevation require separate exact approval.
4. Android SDK licenses require explicit owner acceptance before noninteractive acceptance. No blanket acceptance of unspecified future licenses. If declined, Android remains unavailable.
5. Install in stages: both .NET SDKs and verify --info; then Linux JDK/Android/workload and verify sdkmanager/workload output. No persistent profile, sudoers, system service or network changes.

## Verification and cleanup

Build from a committed/pushed exact integration SHA in a dedicated worker checkout after CI gating; never sync mutable source. Linux/macOS invoke reviewed nonpublishing package scripts only. Android uses an explicitly unsigned Release package command and no production keystore; inspect project signing defaults first and fail if unsigned isolation cannot be established. Do not claim signing/device acceptance from unsigned output. No VPNRouter launch, device installation, TUN/firewall/DNS mutation or release publication.

Retain installed approved toolchains for subsequent broad verification if owner wants; cleanup only exact test-owned build outputs per worker contract. Rollback of tools only for directories created by this task, after confirming no active consumers; no global cache cleanup or removal of existing .NET8/Xcode. Record exact files/paths/versions, commands, hashes, exit status and limitations in installation outcome.

Next decision: approve staged preparation scope (including named Android license review/acceptance gate), then resolve vendor manifest before installation. This document itself grants no installation authority.
