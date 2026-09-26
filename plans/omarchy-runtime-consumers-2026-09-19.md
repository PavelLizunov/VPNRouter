# Linux Headless runtime consumer inventory

Approved scope: [runtime contract](phase-omarchy-runtime-contract-2026-09-19.md).
Evidence: [verification report](omarchy-runtime-verification-2026-09-19.md).
This inventory describes the source-only contract, not deployment readiness.

## Selection and scope

`SingBoxRuntimePolicy` is internal. Linux Headless defaults to
`DefaultProduction`: unavailable/untrusted, with no selected executable.
`ForTestFile` accepts only internal fixture authority; SHA-256 pins are not
release provenance. Authorization distinguishes Missing, Untrusted, Changed and
PrerequisiteUnavailable. Fork AWG/XHTTP facts remain false under policy.

AsyncLocal scopes restore their predecessor; null cannot erase a restriction and
fixture scopes cannot weaken DefaultProduction. Shared atomic `Capture(ref ...)`
latches the first non-null policy on long-lived consumers, with production denial
dominating retained fixtures. This prevents replacing retained denial with a
fixture; it is not a general concurrent operation-revocation protocol.

## Consumer map

Paths are relative to the repository root.

| Consumer | Integration |
|---|---|
| `VPNRouter.Headless/Program.cs` | Linux CLI lifetime enters production policy before backend construction. |
| `VPNRouter.Headless/RouterSession.cs`, `Lifecycle/VpnEngineAdapter.cs` | Default engine factories enter policy before constructing Core; deferred start/apply/stop/disposal restore captured scope. Explicit fake lifecycle test seams remain. |
| `VPNRouter.Headless/RouterBackend.cs` | Captures policy for dispatch, snapshots and disposal; typed runtime refusal maps to safe unavailable. |
| `VPNRouter.Headless/Features/{Server,Subscription,FreeConfig,CustomConfig,Diagnostics}Feature.cs` | Scope parsing, feature checks, verifiers and diagnostics; Linux direct constructors default to denial. Pure routing/profile/rules/settings operations need no additional policy fields. |
| `VPNRouter.Headless/Lifecycle/PlatformCapabilityVerifier.cs` | Production readiness requires policy availability; explicit injected fake readiness is retained for isolated lifecycle tests. |
| `VPNRouter.Core/Services/SingBoxFeatures.cs` | Scoped capability facts precede legacy caches/test overrides; restricted prewarm does not launch a version probe. |
| `VPNRouter.Core/Services/VpnEngine.cs` | Captures/re-enters policy around start/apply/stop/teardown and deferred recovery. |
| `VPNRouter.Core/Services/StartupPipeline.cs` | Authorizes before startup phases; restricted deployment does not copy a bundled executable. |
| `VPNRouter.Core/Services/SingBoxManager*.cs` | Selected path for start/restart; reauthorization at use and prespawn; no restricted Cronet colocation or implicit pkexec/sudo start/stop escalation. |
| `VPNRouter.Core/Services/VlessDeepVerifier.cs` | Availability and selected spawn path follow captured policy, including late capture; policy refusal is LocalSpawn, surfaced safely by Headless. |
| `VPNRouter.Core/Services/FreeConfigs/FreeConfigDeepVerifier.cs` | Authorizes before entry mutation and again before spawn; typed policy denial preserves verification fields. Headless returns unavailable without saving cache. |
| `VPNRouter.Core/Services/HealthCheck.cs` | Policy inspection rather than fallback binary advice; no restricted pkexec/NOPASSWD recommendation. Other diagnostic checks are not a network sandbox. |
| `VPNRouter.Core/Services/Diagnostics/DiagnosticsExporter.cs` | Reports selected policy runtime or safe refusal rather than claiming fallback availability. |
| `VPNRouter.Core/Services/ProcessOwnership.cs` | Selected candidate supplements existing owned-process discovery; not authority to adopt or kill arbitrary processes. |

## Boundaries

- Legacy Core callers without a policy retain their previous behavior.
- Linux fixture validation opens with O_NONBLOCK/O_NOFOLLOW/O_CLOEXEC and checks
  the opened descriptor using statx before bounded hashing (256 MiB cap).
  Metadata is checked before/after reading; special files are not hashed.
- Path revalidation is not race-free descriptor-bound execution. No fexecve
  guarantee, production signature chain or privileged artifact trust is claimed.
- No root broker, Polkit grant, setcap, installation, shell restart, live TUN,
  firewall/DNS mutation or VPN dataplane acceptance occurred in this block.
- Default unavailable is an interim safety boundary, not completion of the full
  native QML VPNRouter product. Production distribution/provisioning and actual
  lifecycle/UI/dataplane acceptance remain separate gates.
