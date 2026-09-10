# PR233 worker SDK installation outcome

Owner approved staged user-local toolchain preparation, then explicitly approved alternate macOS destination after permission failure. SDK installation only; no build or package acceptance claimed.

## Verified installations

Both archives matched SHA512 in pr233-dotnet-sdk-manifest-2026-09-10.json before extraction. Both dotnet --info commands succeeded with SDK10.0.301, commit96856fd726, MSBuild18.6.4, runtime10.0.9 and no workloads installed.

- Linux: /home/tester/.local/share/vpnrouter-build-tools/dotnet-10.0.301, linux-x64. Installation job bash-193 completed Linux successfully before macOS path failure.
- macOS: /Users/slovn/Library/Application Support/VPNRouterBuildTools/dotnet-10.0.301, osx-arm64. Parent ownership/writability and target absence checked; installation job bash-194 exited0.

## Failures and handling

Initial Linux download failed DNS resolution (curl6), before extraction; task temporary directory removed. Canonical homelab proxy documentation was then read and fixed official archive HEAD probes through documented HTTP CONNECT returned200. Downloads used explicit per-command proxy with empty noproxy, HTTPS-only protocol/redirects, no credentials and no direct fallback. No DNS/routing/proxy profile persisted or changed. No egress IP claim made.

First macOS target under ~/.local/share failed PermissionError before download: ~/.local belongs to root. No sudo/chown or retry against protected directory. Owner approved alternate user-owned Application Support path; installation succeeded there. Existing .NET8, Xcode, registered x64 installation and shell PATH unchanged.

Temporary archive/CLI-home directories created by these commands were removed in finally blocks. Installed SDK directories retained as authorized toolchains. Root-owned directory untouched. No SDK installed on control plane, no services restarted, no VPNRouter executed, no release/signing/deployment operations.

## Remaining gates

Linux JDK17, Android command-line tools/platform36/build-tools36.0.0 and Android workload are not installed by this stage. Exact archives/licenses still need verification, Android license acceptance separately agreed. Tests and builds must use explicit SDK paths on committed exact-SHA isolated checkout. Current #233 integration remains uncommitted; SDK installation does not establish product correctness. Broad main regression/anti-slop/performance verification follows completion of merges per latest owner ordering.
