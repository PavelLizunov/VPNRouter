# Omarchy sing-box runtime path investigation

Status: research only; no path resolver, privilege policy or packaging change approved here.

## Confirmed consumer chain

- Plugin setup stages payload in plugin-relative bin/backend.
- Headless PlatformCapabilityVerifier.cs:76 requires AppPaths.SingBoxExePath.
- AppPaths.cs:55 resolves that to DataDir/bin/sing-box on Linux.
- SingBoxManager.Lifecycle.cs:56-58 and restart resolve the same data path;
  configured executable_path is honored only on Windows.
- StartupPipeline.cs:1116-1144 can copy a bundled binary to the data path during
  explicit connection startup. It compares length, not content integrity. A
  missing data binary prevents Headless readiness before this phase is reached.
- VpnEngineAdapter.cs:56 exposes no production readiness override.
- SingBoxManager.Lifecycle.cs:915-929 chooses direct capability-bearing execution
  or pkexec. Finding a binary alone proves neither TUN permission nor protection.

## Gemini research disposition

The read-only worker suggested global AppPaths cascading, configured-path wiring,
or system packaging with file capabilities. None is implemented or accepted as
an approved privilege design. Global cascading would change other Core consumers
and ownership assumptions; broadening trusted directories merely to accommodate
discovery is not justified. Consistent start/restart/check/diagnostic selection
must be demonstrated rather than changing readiness alone.

Reject the worker's claim that root-owned setcap deployment has zero privilege
escalation surface. A network-capable binary consuming caller-selected config
still grants powerful behavior; binary ownership is necessary but not sufficient
for authorization, cross-user isolation or safe dependency loading. File
capabilities do not implement Headless firewall/DNS protection. No setcap,
installation, live TUN creation or elevated user-writable execution was performed.

The existing source-only privileged-helper Micro-Spec still requires explicit
human approve. Routine continuation does not authorize new privilege/distribution
architecture. Pinning fork/version and independently trusted integrity metadata
remain separate gates. The worker's suggested versions/hashes were not verified
against current release artifacts and must not be used as installation inputs.

Next architecture work must unify runtime selection and the approved privilege
boundary, preserve side-effect-free reads, and test packaged consumer behavior
without silently claiming that file presence makes connection available.
