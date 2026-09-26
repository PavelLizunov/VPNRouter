# Omarchy Core delivery review

## Scope and evidence

Two independent Gemini workers reviewed the working tree against specification
commit `b8bde92c99448c6321396fcaf14f9a9f6f1eae93`: Core ownership/DNS/profile
changes and the safety of proposed Core test filters. Workers performed no builds,
remote operations or source edits. Their prose verdicts are not acceptance.

## Coordinator disposition

- The reviewer found no additional concrete product regression in its inspected
  Core scope. Its statements of zero Windows/macOS/Android regressions and
  elimination of TOCTOU attacks are not accepted: it did not run those platforms,
  and fd validation does not eliminate every path replacement scenario.
- Same-UID flock remains an intentional limited boundary, not host-global TUN
  arbitration. Missing privileged firewall/DNS integration is still a parity gap,
  not an approved reduction of the original scope.
- Static runtime-directory test override is used only by serialized fixtures;
  `VPNRouter.Tests/xunit.runner.json` disables collection/assembly parallelism.
  No parallel-test safety claim is made.
- Bundled profile catalogs intentionally precede unconfigured user-directory
  catalogs; explicitly configured sources remain the customization mechanism.
- The audit correctly identified that `FullyQualifiedName~Profile` also selects
  methods in unrelated classes. Future scoped runs use namespace/class followed
  by a dot, not an unqualified topic substring.
- `ServiceAppCoexistenceTests` previously acquired/probed the production Linux
  runtime lock and seeded Windows semaphore fields for its reconnect test.
  OMARCHY-COEXISTENCE-TEST-ISOLATION was recorded before changes. The fixture now
  redirects AppPaths and Linux runtime to its private temporary tree, restores
  both on disposal and uses real flock acquisition for Linux reconnect.
- The suspected owner-monitor bypass was rejected on source inspection:
  `ProcessOwnership.ConfiguredExePath` calls `RegisterExecutablePath`; the latter
  checks the current singleton's `_owned` state independently of lock backend.
- Eight SingBoxManagerProcessRunner tests return early on non-Windows. A Linux
  green result cannot validate their Windows launch/stop/restart assertions.

## Expanded offline slice

A subsequent read-only Gemini audit covered 18 generator/DNS test files.
Coordinator checked the fake DNS runner, temporary AppPaths usage, and actual
rule-set download callsites. Twelve classes were selected. Snapshot
`5edc14bfd5f550fcc61129122cb28c8d64e287ab`, bash-39 exit 0:
`dotnet build VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --nologo -v quiet -clp:ErrorsOnly`
completed with 403 warnings / 0 errors. Then
`timeout 180 dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build --no-restore --nologo --filter '<exact class terms>' --logger 'trx;LogFileName=expanded-core.trx' --results-directory verification-expanded-core`
passed 76 tests, 0 failed, 0 skipped. Each filter term is
`FullyQualifiedName~VPNRouter.Tests.<class>.`, joined with OR:

- ConfigGeneratorAutoSelectHealthFilterTests
- ConfigGeneratorAppRoutingFingerprintTests
- ConfigGeneratorDuplicateNameTests
- ConfigGeneratorIncludeModeTests
- ConfigGeneratorExcludeModeTests
- ConfigGeneratorStrictDnsOverrideTests
- ConfigGeneratorTcpKeepAliveTests
- AppSettingsDnsLeakLockdownTests
- AppConfigDetourTests
- ConfigPipelineTests
- DnsIpv4StrategyTests
- LinuxDnsHardeningTests

Tests used disposable HOME/XDG directories, removed by the exact job cleanup
trap. SDK first-run output reported development-certificate creation under this
isolated user environment; no trust command was invoked. For future fresh-HOME
runs also set DOTNET_GENERATE_ASPNET_CERTIFICATE=false. Linux DNS command tests
use FakeProcessRunner and a private sentinel; no live DNS configuration proof.

Correction after inspecting TestEnvironmentSafety.cs: its module initializer
already redirects AppPaths to a per-testhost temporary directory, so the earlier
Gemini assertion of production cache writes was too broad. The confirmed issue
is unmocked network access and shared/non-deterministic testhost cache, not a
proven production-directory write in this test assembly.

Excluded at the time: RemoteRuleSetGuard and SplitCharacterization invoke rule-set downloads;
DnsTunnel, QuicBlock and EmptyServersGuard include production Windows binary
probes that return early when absent. These findings are in OPEN-DEFECTS. Source
inspection and passing tests do not prove zero egress by network instrumentation,
or safety of the remaining unaudited fixtures. Prior ConfigGeneratorTests slice
also contains a geo-file-dependent early return; its pass is not full branch
coverage. No unrelated fixture assertion was weakened to expand this slice.

## Rule-set fixture isolation follow-up

OMARCHY-CORE-FIXTURE-NETWORK was narrowed after reading the assembly initializer:
AppPaths already points to a private testhost tree. The fixture itself did not
seed rule sets and could fetch real HTTP inputs or pass vacuously with no entries.
The revised fixture redirects/restores AppPaths, seeds fresh cache and minimum-size
geo files, and requires the exact local tags, contained paths and unchanged bytes.
It explicitly does not validate SRS binary format or sing-box compatibility.

Safe red snapshot `9957facaa36ed4755151a00c941c50b984998c16` ran only
`FullyQualifiedName=VPNRouter.Tests.ConfigGeneratorRemoteRuleSetGuardTests.Fixture_UsesPrivateSeededRuleSets`:
failed at private-directory assertion before any generation/download path.
Green `6c7fc82cd427773ad627c1cae264c9ab5a3a9103` ran the complete class with
`FullyQualifiedName~VPNRouter.Tests.ConfigGeneratorRemoteRuleSetGuardTests.`:
6 passed, 0 failed, 0 skipped. Both Release builds completed with 426 warnings,
0 errors. bash-40 overall exit 0; each test invocation used a disposable HOME,
XDG_CONFIG_HOME and XDG_CACHE_HOME with DOTNET_GENERATE_ASPNET_CERTIFICATE=false.
A subsequent comment-only correction names testhost isolation accurately.

Coordinator reviewed this one-file fixture change directly. Current cache-hit
calls do not need HTTP; no network-namespace or packet-capture proof was obtained.
Tests check freshness before generation but do not claim protection against an
external actor deleting fixture files between assertions. SplitCharacterization
and the remaining full-suite isolation audit are still outstanding.

## Delivery prerequisites

The full Core suite remains unapproved for execution on the live Omarchy desktop
until other fixtures are audited or safely isolated. Scoped passes are not a
full-suite verdict. Current checkout has no active custom Git hooks configured;
no hook setting has been changed or bypassed. Neither `powershell` nor `pwsh` is
available on the control plane, so the mandatory post-push script cannot currently
be invoked there. No SDK/runtime is installed to work around that constraint.
Implementation delivery commits and exact-SHA GitHub CI remain pending.

## Verification

Snapshot `616848d81d2f3a5ae4e5e905ec8f0f699dfb8f58`, bash-30, exit 0:
- `dotnet build VPNRouter.sln -c Release --nologo -v quiet -clp:ErrorsOnly`:
  succeeded, 434 warnings, zero errors. This is not a warning-free build.
- `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build
  --nologo --filter '<class filter below>'`: 96 passed, zero failed. Eight
  Windows-guarded runner cases return early and are not Windows evidence.
- `timeout 180 dotnet VPNRouter.Headless.Tests/bin/Release/net10.0/VPNRouter.Headless.Tests.dll`:
  35 groups passed, including 41 lifecycle checks.

Class filter: OR the `FullyQualifiedName~VPNRouter.Tests.<class>.` terms for
ConfigGeneratorTests, NightDnsPrivacyRegressionTests, VpnEngineStartAsyncSeamTests,
ProfileApplicationTests, ProfileManagerJsonDosGuardTests, StartupPipelineTests,
SingBoxManagerProcessRunnerTests and ServiceAppCoexistenceTests.

After this snapshot, only the LinuxTunOwnership XML comment was corrected to
remove its universal TOCTOU claim; executable source remains as tested.
No live VPN operation was exercised. Verdict: scoped checks passed; whole-product
acceptance and implementation delivery remain pending.
