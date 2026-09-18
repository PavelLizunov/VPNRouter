# Integrated backend candidate verification

Base delivered commit: ecd1b503df327867c5750617987101486ed665c9.
That standalone fixture commit is pushed to draft PR296; canonical PowerShell
verifier on WINBRAT passed4 checks/0 tolerated/0 pending/0 red, exit0 (bash-50).
Temporary verifier directory removal was confirmed. No release authorized.

Candidate snapshot: a0cf051adade9d6d49fded4fe104ff983a73536f.
Contains remaining Core/Headless/test/solution/CI/protocol changes selected from
the delivery plan, on top of delivered fixture. Does NOT include latest local
phase/defect ledger edits or historical reports. Not a delivery commit/push.

## Executed checks

bash-51 exit0 on identity-checked omarchy-test, SDK10.0.301:
- dotnet build VPNRouter.sln -c Release --nologo -v quiet -clp:ErrorsOnly
  succeeded: 434 warnings, 0 errors.
- timeout180 dotnet VPNRouter.Headless.Tests/bin/Release/net10.0/VPNRouter.Headless.Tests.dll
  passed36 groups, including41 lifecycle checks and real subprocess controls.
- dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build
  --no-restore --nologo --filter '<exact class terms>' passed178/failed0/skipped0.
  Terms were FullyQualifiedName~VPNRouter.Tests.<class>. OR-joined for:
  ConfigGeneratorTests, NightDnsPrivacyRegressionTests,
  VpnEngineStartAsyncSeamTests, ProfileApplicationTests,
  ProfileManagerJsonDosGuardTests, StartupPipelineTests,
  SingBoxManagerProcessRunnerTests, ServiceAppCoexistenceTests,
  ConfigGeneratorAutoSelectHealthFilterTests,
  ConfigGeneratorAppRoutingFingerprintTests, ConfigGeneratorDuplicateNameTests,
  ConfigGeneratorIncludeModeTests, ConfigGeneratorExcludeModeTests,
  ConfigGeneratorStrictDnsOverrideTests, ConfigGeneratorTcpKeepAliveTests,
  AppSettingsDnsLeakLockdownTests, AppConfigDetourTests, ConfigPipelineTests,
  DnsIpv4StrategyTests, LinuxDnsHardeningTests,
  ConfigGeneratorRemoteRuleSetGuardTests.

Tests used private HOME/XDG config/cache/runtime, with certificate generation
disabled. Full Core suite remains unrun on the live Omarchy host; audited subset
only. Non-Windows early-return runner cases are not Windows behavior evidence.
No live VPN/routing/root operations. Candidate git diff --cached --check passed.

Additional bash-52 exit0 on the same candidate:
`dotnet publish VPNRouter.Headless/VPNRouter.Headless.csproj -c Release --self-contained false -o <private-temp> --nologo -v quiet -clp:ErrorsOnly`.
Parsed deps.json libraries contain no Avalonia; no Avalonia DLL in publish root;
both profiles/default.json and profiles/default-linux.json are byte-identical
to candidate sources. Temporary publish directory removed by exact-path trap.

## Review disposition

One independent Gemini read-only prepublication lane inspected integrated code,
CI and docs. No additional concrete draft-publication blocker reported.
Coordinator checked workflow steps, capability gate and flock permission checks.
Do not adopt blanket worker claims of 'safe' or 'verified dependencies' as runtime
proof. Directory validation rejects group/other access; it is not an exact0700
comparison as worker prose implied. CanConnect currently proves file presence
and free cooperative lock, not binary integrity or TUN privileges. Same-UID flock
is not host-global ownership. Existing Core pkexec fallback remains unchanged.

Linux firewall/DNS protection parity, trustworthy binary distribution/path,
actual shell popup/input/scaling and dataplane acceptance remain outstanding.
Source-only privileged-helper Micro-Spec still lacks explicit approval.

Next block: reconcile/sanitize delivery documents and reviewed allowlist, then
commit integrated backend on task branch, push immediately and run canonical
exact-SHA verifier after CI. This report does not authorize merge/deployment.
