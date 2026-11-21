# F-01 macOS privileged runtime migration (2026-11-20)

Snapshot: Standard `6066dd593f47f82a45eae4d55be0d5e4ca429a28`.

## Intent

Replace the macOS NOPASSWD grant on the user-writable `sing-box` copy with a
managed, root-owned privileged helper. Remove the DMG guide's duplicated
sudoers command so the guide and the app cannot diverge again.

## Scope

- New root-owned helper contract and install/migration path under
  `packaging/macos/`.
- One typed request protocol on stdin; sudoers grants exactly one helper path
  with no arguments and no wildcards.
- Root-owned `sing-box`, root-owned staged config, root-owned rule-set assets.
- Start, exact stop, DNS hardening, pf kill-switch, and probe through the helper.
- App-side policy renderer, config contract, setup/probe client, and launch
  integration.
- Guide and user docs no longer contain a sudoers command.
- Contract/unit tests under `VPNRouter.Tests`; helper compile and typed-contract tests in macOS CI.

## Out of scope

- `SMAppService`/`SMJobBless` XPC helper (macOS 13+; default remains macOS 12).
- Developer ID signing/notarization.
- Windows/Linux privileged paths.
- A live install or root execution on this workstation.

## Invariants

1. No persistent sudo grant names a path under `/Users/`, a user-writable
   directory, or `sing-box` directly.
2. The only sudoers command is
   `/Library/PrivilegedHelperTools/com.vpnrouter.privileged` with no arguments.
3. The helper requires root, derives the invoking uid from `SUDO_UID`, and
   validates every input before it is interpreted as root.
4. Privileged `sing-box` runs from a root-owned path with a root-owned staged
   config and root-owned rule-set assets.
5. A missing, legacy, or mismatched helper state fails closed: the app does not
   fall back to the old `sudo <user-path>/sing-box` command.
6. The guide has no `sudo`, `NOPASSWD`, `pkill`, or `/etc/sudoers` text.

## Verification

Contract tests:

```powershell
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~MacPrivilege|FullyQualifiedName~MacPrivileged|FullyQualifiedName~MacInstallGuide"
```

Related regression slice:

```powershell
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~MacDnsHardeningTests|FullyQualifiedName~MacFirewallManagerTests|FullyQualifiedName~SingBoxManagerProcessRunnerTests|FullyQualifiedName~UnixOwnedProcessSignalTests"
```

macOS CI compiles the helper with `clang -Wall -Wextra -Werror`, runs its
typed-contract tests under ASan/UBSan, and builds the DMG. Full-suite and live
acceptance remain gates on an authorised preflighted worker or macOS test host.

## Independent review of the prototype

The prototype is isolated pending these source corrections:

- Authenticate the installer before elevation. Verify the installed helper,
  runtime and their protected directory chain independently before invoking
  either. A running helper's reported hashes are diagnostic evidence.
- Validate a single canonical sudoers rule and publish it after verification.
  An interrupted or failed migration must leave no newly installed grant.
- Ensure the helper and runtime agree on configuration keys and reject
  ambiguous encodings or duplicates. Verify the exact bundled core's control
  handlers and restrict the privileged controller to the session owner.
- Bind privileged stop and firewall operations to the invoking session.
  Verify DNS and firewall restoration and preserve the existing regressions.

These findings keep F-01 open. No privileged install or runtime acceptance
was performed on the reviewed prototype.

## Defensive follow-up 2026-11-20

### Intent

Harden the privileged configuration contract before any value validation and
decide whether the reload-capable Clash controller can be removed without a
larger trusted IPC.

### Scope

- Canonical object keys in the C helper and the C# contract: printable ASCII,
  no escapes, no empty keys, no duplicates at any object depth.
- Preserve escaped and non-ASCII text values.
- Focused C and C# contract tests with synthetic JSON.
- No installer, sudoers, Windows/Linux, network, live-root, or release work.

### Invariants

1. Key validation runs before normalization, forbidden-key checks and lookups.
2. The C helper and the C# contract reject the same key encodings.
3. A key that cannot be compared byte-for-byte without decoding is rejected,
   not silently normalized.
4. Preserve escaped ASCII and raw UTF-8 values. The helper's typed string
   fields still reject Unicode escapes above ASCII.
5. Both parsers limit nesting to 64 containers. The helper checks this
   before tokenization and recursive validation and accepts one complete
   root object. Duplicate detection sorts the keys of each object.

### Verification

```powershell
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~MacPrivilegedConfigContractTests"
```

macOS CI compiles and runs `packaging/macos/tests/contract-tests.c`. This
workstation did not run `dotnet test` or the macOS CI build; the local
stub-header contract run and synthetic-JSON reference checks are recorded in
the outcome below.

### Privileged controller: pinned-core evidence and remaining work

The helper accepts a loopback `experimental.clash_api.external_controller`.
The pinned core is `v1.14.4-vpnctl.2`; the published Darwin archive digest
matches the pin in `build-mac.sh`. Its `updateConfigs` handler only returns
204, without reading the body or reloading a file. A client success status
therefore cannot prove that a new configuration was applied. Do not assume
this behavior when changing the pinned core.
[Exact core handler](https://github.com/PavelLizunov/sing-box-vpnctl/blob/v1.14.4-vpnctl.2/experimental/clashapi/configs.go#L64-L66)

The controller still includes routing and cache operations. Loopback alone
does not authenticate the session owner. The following callers depend on
its startup and telemetry functions:

- `StartupPipeline` calls `ClashSingBoxApi.ReadLocalController` and
  `ResolveControlTargetsAsync` before launch, and passes the endpoint to
  `StartMonitorsPhase`.
- `HealthMonitor` uses `GetVersionAsync`, `GetProxyDelayAsync` and
  `ReloadConfigAsync` for serving, strict-DNS failover and hot reload.
- `SingBoxManager.IsRunning` and `IsHealthy` use the Clash API on macOS.
- `AcceptedTunnelInfo.OpenController`, `VpnEngine` apply and the optional
  `ClashLogStream` also consume the controller.
- `ConfigSanityCheck.ProbeAsync` uses the controller for startup proxy probes.

`ReadLocalController` throws when the key is absent, and the health monitor
defaults to port 9090 when no endpoint is passed, so omission alone would
either crash startup or probe an unrelated listener. Any replacement must
preserve typed status/probe operations and bind them to the verified child
and session. Audit the actual config-apply and strict-DNS
recovery paths; this core's 204 is not configuration reload evidence. F-01
still requires a verified installer, exact sudoers publication, a restricted
controller and runtime acceptance.

### Outcome (2026-11-20)

Changed:

- `packaging/macos/vpnrouter-privileged.c`: canonical-key predicate,
  key validation folded into `validate_unique_keys`, forbidden-key checks use
  raw canonical tokens, manifest keys validated before use.
- `packaging/macos/tests/contract-tests.c`: escaped, duplicate, nested,
  non-ASCII, control, empty and long-key vectors; escaped and raw-UTF-8 value
  preservation.
- `VPNRouter.Core/Platform/macOS/MacPrivilegedConfigContract.cs`: iterative
  `Utf8JsonReader` key pre-scan before `JsonNode.Parse`, plus a post-serialization
  key check on the exact staged bytes.
- `VPNRouter.Tests/MacPrivilegedConfigContractTests.cs` and
  `MacPrivilegedSourceContractTests.cs`: focused contract and ordering tests.
- `packaging/macos/README.md`, this plan and `plans/OPEN-DEFECTS.md`: contract
  and remaining-risk text.

Verification performed on this workstation: `git diff --check`; the real
`packaging/macos/tests/contract-tests.c` compiled with clang
`-Wall -Wextra -Werror` and ASan/UBSan against temporary stub headers for the
macOS-only CommonCrypto/libproc pieces, and passed. A Python reference check of
the synthetic accept/reject vectors, including escaped and raw-UTF-8 values,
also passed.

Not run here: `dotnet build`/`dotnet test` and the real macOS helper compile.
Workspace instructions forbid builds on this workstation, and the C helper's
CommonCrypto/libproc/root operations only build and run on macOS. The stubbed
contract run proves the parser and validator paths exercised by those tests, not
the stubbed crypto, process inspection or privileged commands. This is not
runtime acceptance.

Limits: the Clash controller remains accepted (see above); no installer,
sudoers, Windows/Linux, network, root, live-host, secret, reproduction,
commit, push or delegation work was performed. F-01 remains open.

## Unknowns and live-only checks

- Actual TUN creation, `sudo -n` prompt behaviour, admin cancellation, and
  `networksetup`/`pfctl` interaction are live-only.
- Unsigned app bundles remain a trust-anchor residual risk; the installer
  refuses group/world-writable sources but cannot prove the app publisher.
- Multi-user Macs: the managed policy is per-uid; the legacy shared file is
  removed once, which can affect another user's older session.

## Rollback

The app never writes a user-writable sudoers file. To roll back, remove
`/etc/sudoers.d/vpnrouter-<uid>`, `/Library/PrivilegedHelperTools/com.vpnrouter.privileged`,
and `/Library/Application Support/VPNRouter` as root, then install a previous
version. The old version will ask for its old grant again only after its own
installer path is used; this patch does not recreate it.

### Parser boundedness follow-up

Review accepted canonical-key parity. The follow-up limits nesting before
tokenization, rejects raw NUL and trailing root content, and sorts each
object's keys for duplicate checks. Synthetic contract cases cover depth
64/65, escaped quotes inside a value, a second root and a wide object.
The .NET cases also cover keys escaped by serialization. Native and .NET
execution of this follow-up remain pending on the worker snapshot.

### Structural-key case parity and strict syntax follow-up (2026-11-20)

Static review found two parser mismatches with the pinned core: the helper and
C# contract accepted any canonical key spelling while sing-box binds struct
fields case-insensitively, and the lexical depth pre-scan could disagree with
lenient jsmn on malformed syntax.

Changed:

- `packaging/macos/vpnrouter-privileged.c`: reserved structural and forbidden
  names are rejected in any case-variant spelling before object lookup; jsmn
  input is gated by a strict RFC 8259 parser that counts containers and rejects
  nesting above 64 before tokenization.
- `VPNRouter.Core/Platform/macOS/MacPrivilegedConfigContract.cs`: the same
  reserved-name set is checked in the pre-parse key pass, so the app rejects a
  configuration the helper would reject instead of failing only after launch.
- Contract tests cover inert key-casing, strict-syntax, 64/65 nesting and
  ordinary-config cases; the source contract pins the shared reserved list and
  the syntax-before-tokenization order.

Compatibility: every generated sing-box key inspected in
`VPNRouter.Core/Models/VPNConfig.cs` is lowercase snake_case, so generated
configurations are unchanged. A custom or imported configuration that spelled
a reserved structural name differently (for example `Log` or `Output`) is now
rejected in both layers; such a key was ambiguous because sing-box would bind
it while the helper skipped it. Non-reserved field names (for example a custom
`CustomField`) and normal string values, including mixed case and raw UTF-8,
remain accepted and are preserved. Case-sensitive dictionary entries keep
their ordinary spelling; only a map key that matches a reserved name
case-insensitively (for example a custom HTTP header named `Path`) is rejected
because it cannot be distinguished from a structural field at this layer. The
exact lowercase spelling remains accepted.

Not run here: the native helper compile and contract run, and `dotnet test`;
per the workspace contract those run on an authorised worker or macOS CI. This
is not runtime acceptance, and F-01 remains open.

### Stop request identity follow-up

Review confirmed that the helper accepted a stop request without a usable
start timestamp. The client also divided .NET ticks into seconds without
changing epochs. Require a positive Unix timestamp at both boundaries,
compare it to the live process, and test request parsing without signaling
any process. Session ownership and the installer trust gates remain open.
The native helper now rejects missing and invalid timestamps before reading
the live process, and the client sends Unix seconds using the owned process
snapshot. Native parser fixtures and a managed fake-runner guard cover the
request boundary. Native and managed tests have not run on this diff.
