# PR233 Android toolchain installation outcome

Installed on authorized linux-worker as tester under ~/.local/share/vpnrouter-build-tools; no system PATH/profile or sudo changes.

- Command-line tools20.0: CI-pinned archive14742923 verified against vendor SHA1; measured SHA25604453066b540409d975c676d781da1477479dde3761310f1a7eb92a1dfb15af7. sdkmanager --version returned20.0 using Temurin17.0.20.1, exit0 (bash-199). Archive extracted into android-sdk/cmdline-tools/20.0 with path/type checks and executable permissions retained.
- sdkmanager install with closed stdin first displayed only android-sdk-license and skipped both packages despite exit0 (bash-200). Owner had explicitly accepted this license. Second invocation supplied one y to the exact package install, not blanket --licenses (bash-201).
- sdkmanager --list_installed confirmed platforms;android-36 revision2 and build-tools;36.0.0 version36.0.0. aapt2 version succeeded (2.20-13193326). Manually extracted CLI not enumerated by local package metadata; executable version separately verified. No emulator/NDK/platform-tools added.
- .NET10.0.301 workload install android --skip-manifest-update succeeded (bash-202). workload list confirmed android36.1.2/10.0.100, installed by SDK10.0.300 feature band, workload manifests10.0.300-manifests.b0c14421. Remaining disk48GiB. SDK workload includes its transitive runtime packs for multiple ABIs/.NET versions; no application minimum API change.

## Limits and observed side effects

Installer printed 'Skipping NuGet package signature verification.' No skip flag or signature environment override was supplied by this task; no cryptographic NuGet signature verification claimed. Source configured by SDK defaults over per-command reviewed HTTPS proxy; further supply-chain checks may be needed before stronger assurance claims.

SDK first-run printed 'Installed an ASP.NET Core HTTPS development certificate.' No trust command was executed. This is an observed SDK side effect, not intentionally provisioned production credentials; do not delete certificates indiscriminately. Subsequent commands explicitly set DOTNET_GENERATE_ASPNET_CERTIFICATE=false. DOTNET_CLI_HOME and NuGet cache isolated under approved build-tools root; sdkmanager may create normal user metadata outside SDK root. No assertion of zero user-state side effects.

Temporary command-tools download directories removed. Toolchain retained as approved. No VPNRouter build, tests, signing, device installation, deployment or live VPN occurred. Next step: freeze committed integration snapshot, ordinary CI and exact-SHA isolated worker builds; broad combined-main audit/performance remains after merges per owner instruction.
