# Linux Headless runtime contract verification

Date: 2026-09-19 (Europe/Moscow).
Branch: `dsh/omarchy-plugin-2026-09-17`; approved brief/base
`28b136b6af9759de83546590666421228964c6f1`.
Scope: source-only runtime contract, not runtime provisioning or full product acceptance.

## Requirements and implementation

See [approved brief](phase-omarchy-runtime-contract-2026-09-19.md) and
[consumer inventory](omarchy-runtime-consumers-2026-09-19.md).
Linux Headless production policy is unavailable/untrusted until separately
approved artifact/provisioning exists. No CLI, NDJSON or environment option grants
fixture authority. Scoped policy governs selected runtime, feature facts,
start/restart, verifiers and diagnostics; other Core clients retain null defaults.
Implicit elevation, binary fallback/deployment copying and Cronet copying are
excluded under restricted policy. No privileged helper is introduced.

## Executed evidence

Control plane did not build or execute VPNRouter. Immutable Git archives were
exported to private `vpnrouter-omarchy-checks/<snapshot>` directories on the
approved `omarchy-test` worker, identity `omarchytest`/`tester`, SDK 10.0.301.
Each run used private HOME, XDG config/cache/runtime paths (runtime mode 0700).
Fake process runners and synthetic fixture files supplied runtime execution tests.

### Green snapshot

`dabdc8c24220a0a31533659d71ce613baaf15b29` (bash-34):

```sh
dotnet build VPNRouter.sln -c Release --nologo -v quiet -clp:ErrorsOnly
timeout 180 dotnet VPNRouter.Headless.Tests/bin/Release/net10.0/VPNRouter.Headless.Tests.dll
timeout 240 dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build --no-restore --nologo --filter "$filter"
```

- Solution build: exit 0, 0 errors, 448 warnings (not a warning-free claim).
- Headless: exit 0, 37 top-level groups; includes 41 lifecycle subchecks and 12
  runtime subchecks. Counts are hierarchical, not additive test totals.
- Core audited subset: exit 0, 204 passed, 0 failed, 0 xUnit-skipped: prior 178
  cases plus 26 runtime-policy cases. Platform early returns still count as
  passing; Linux results are not native Windows/macOS/Android evidence.

Filter is OR of `FullyQualifiedName~VPNRouter.Tests.<class>.` for:

```text
ConfigGeneratorTests
NightDnsPrivacyRegressionTests
VpnEngineStartAsyncSeamTests
ProfileApplicationTests
ProfileManagerJsonDosGuardTests
StartupPipelineTests
SingBoxManagerProcessRunnerTests
ServiceAppCoexistenceTests
ConfigGeneratorAutoSelectHealthFilterTests
ConfigGeneratorAppRoutingFingerprintTests
ConfigGeneratorDuplicateNameTests
ConfigGeneratorIncludeModeTests
ConfigGeneratorExcludeModeTests
ConfigGeneratorStrictDnsOverrideTests
ConfigGeneratorTcpKeepAliveTests
AppSettingsDnsLeakLockdownTests
AppConfigDetourTests
ConfigPipelineTests
DnsIpv4StrategyTests
LinuxDnsHardeningTests
ConfigGeneratorRemoteRuleSetGuardTests
HeadlessRuntimePolicyTests
```

Runtime regressions cover refusal types, same-size replacement, unsafe/special
files, bounded fixture identity, default scope dominance, retained objects,
root/unknown identity refusal, fake start/restart selected path, tampered restart,
late-captured verifier path, direct embedded Headless defaults and cache refusal.
The Capture test invokes production code directly, without reflection or a copied
fallback implementation. SuppressFlow is disposed before awaiting its child.

### Negative control

`a368989cab877e976ae3f898bded2f2bdf248f7c` (bash-35) uses green snapshot tests with
only `FreeConfigDeepVerifier.cs` and `FreeConfigFeature.cs` from preceding
`c539f904598191350b36eee9b8bc82926b6b30f4`.

```sh
dotnet build VPNRouter.Headless.Tests/VPNRouter.Headless.Tests.csproj -c Release --nologo -v quiet -clp:ErrorsOnly
timeout 180 dotnet VPNRouter.Headless.Tests/bin/Release/net10.0/VPNRouter.Headless.Tests.dll
```

Build exit 0. Runner exit 1: 36 groups passed, RuntimePolicyChecks failed exactly
at `FreeConfigFeature.VerifyAsync must throw RouterException with code
'unavailable' under runtime refusal`. Harness expected exit 1 and returned 0.
Green snapshot passes this assertion and subsequent byte-identical persisted
cache/status checks through both feature and actual backend dispatch. No dirty
worktree files were reverted for the negative control.

## Review and corrected findings

Independent Gemini correctness/security/test reviews were source-checked by the
coordinator; a final bounded correction review covered
`c539f904..dabdc8c2` and reported no blocking findings in that diff.
Review statements about global safety or all races are not adopted.

Corrected before delivery: missing namespace/enum compile errors; nested scope
test assumptions; wrong unsupported-import oracle; retained verifier spawn path;
local policy refusal falsely marking free configs TlsFailed; duplicated nonatomic
capture getters; restricted diagnostic elevation advice; test reflection/fallback,
SuppressFlow lifetime and fake restart matcher reuse.

## Untested limits and remaining gates

- Full Core suite is reserved for isolated CI, not blindly executed on a live
  worker. Native non-Linux checks and production artifact compatibility are not
  established by the audited Linux subset.
- No live shell/plugin activation, VPN connect, TUN/firewall/DNS mutation, root
  provisioning, helper installation, merge, tag or release.
- File identity/hash tests do not establish release provenance or race-free
  path-to-exec binding. No descriptor-bound execution is implemented.
- Linux firewall/DNS capability flags stay false. Production distribution,
  privilege architecture and real QML/lifecycle/dataplane acceptance remain.
- Post-push exact-SHA CI and canonical PowerShell verifier receipts belong in the
  draft PR #296. At preparation of this report they are pending; this document
  alone does not certify the eventual delivery commit or full product readiness.
