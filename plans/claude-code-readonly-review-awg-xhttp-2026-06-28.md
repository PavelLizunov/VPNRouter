# Claude Code readonly review: AWG/XHTTP follow-up

Date: 2026-06-28
Mode: readonly code review by Codex. Source code was not edited.

## Scope blocks reviewed

1. Runtime capability gate for fork-only sing-box features:
   `VPNRouter.Core/Services/SingBoxFeatures.cs`,
   `ServerUriParser.cs`, `VlessUriParser.cs`, `StartupPipeline.cs`, `AppPaths.cs`.
2. AWG config generation, endpoint routing, QUIC policy, and leak validation:
   `ConfigGenerator.cs`, `LeakProtection.cs`, `VPNConfig.cs`, `VlessConfig.cs`.
3. Parser/import/UI entry points for new protocols:
   `ServerUriParser.cs`, `SubscriptionFetcher.cs`, `SimpleInputDetector.cs`,
   `MainWindowViewModel.SimpleMode.cs`, `ServerViewModel.cs`.
4. Build/test harness for the fork:
   `tools/build-singbox-lx.ps1`, `AmneziaWgEndpointTests.cs`,
   `XhttpTransportTests.cs`, `SingBoxFeaturesGateTests.cs`.

## Findings

### P1. `SingBoxFeatures` can cache the wrong binary capability for the whole process

Evidence:

- `VPNRouter.Core/Services/SingBoxFeatures.cs:67-84` probes `AppPaths.SingBoxExePath`
  once and then sets `_probed = true`.
- `VPNRouter.Core/AppPaths.cs:46-47` points `SingBoxExePath` to the runtime data
  bin directory.
- `VPNRouter.Core/Services/StartupPipeline.cs:1076` deploys the bundled binary later,
  and `StartupPipeline.cs:1100-1121` copies `AppContext.BaseDirectory/sing-box.exe`
  to the runtime path only in the deploy phase.
- The parser gates depend on the cached value at `ServerUriParser.cs:119` and
  `VlessUriParser.cs:86`.

Why it matters:

- If a user imports/pastes an AWG or XHTTP link before the deploy phase, the gate can
  probe a missing or stale runtime binary, cache `false`, then keep rejecting AWG/XHTTP
  even after the lx binary is deployed.
- The reverse is also dangerous: if the runtime data dir still has an lx binary but
  the current app bundle is official upstream, the gate can allow fork-only links before
  the official binary is redeployed.

Suggested fix:

- Make the capability probe target the binary that will actually run for the generated
  config. Good options:
  - deploy/refresh sing-box before any parser gate can be used, then invalidate the
    probe cache after `DeploySingBoxBinary`;
  - or probe the bundled binary from `AppContext.BaseDirectory` when present, falling
    back to `AppPaths.SingBoxExePath`;
  - or expose an explicit production refresh method and call it after every binary copy.
- Do not leave a missing binary as a permanent cached `false`.

Acceptance criteria:

- Add a test where the first probe sees no/official binary, then the runtime binary is
  replaced with an lx fake, and AWG/XHTTP become available without app restart.
- Add a test for the opposite transition: lx fake to official fake.
- Manual verification: import an AWG link immediately after installing an lx build over
  an official build; it must not require an app restart.

### P1. `ReadTagsLine` timeout is ineffective and can hang the UI path

Evidence:

- `VPNRouter.Core/Services/SingBoxFeatures.cs:95-103` redirects both stdout and stderr,
  then calls `p.StandardOutput.ReadToEnd()` before `p.WaitForExit(5000)`.
- Stderr is redirected but never drained.

Why it matters:

- If `sing-box version` hangs, keeps stdout open, or fills stderr, `ReadToEnd()` can block
  before the 5-second timeout is reached. Because parser gates call this lazily, paste,
  subscription refresh, or connect can freeze.

Suggested fix:

- Drain stdout and stderr asynchronously with cancellation, or start both reads and then
  wait with a timeout before consuming the result.
- Kill the process tree on timeout and return the safe default.

Acceptance criteria:

- Add a fake binary test that writes a lot to stderr and never exits. The feature probe
  must return within the timeout.
- Add a fake binary test that prints a normal `Tags:` line and exits. Both AWG and XHTTP
  must be detected.

### P1. AWG can hijack a selected non-AWG server on the same host

Evidence:

- `VPNRouter.Core/Models/VlessConfig.cs:125-127` returns all servers with the same
  `Server` value as the active server in default mode.
- `VPNRouter.Core/Services/ConfigGenerator.cs:1200-1214` picks the first AWG entry from
  that whole same-host list and returns an AWG endpoint config.

Why it matters:

- If the active server is VLESS/HY2/TUIC and the subscription also contains an AWG server
  on the same IP/host, config generation can switch to AWG even though the user selected
  the non-AWG entry. That changes protocol, credentials, route semantics, and runtime
  binary requirements.

Suggested fix:

- Only enter the AWG endpoint branch when the actual active entry is AWG.
- Keep deliberate TCP/UDP sibling pairing for existing protocols, but do not let AWG
  participate as an implicit same-host sibling.

Acceptance criteria:

- Add a regression test with two same-host entries:
  active `vless`, sibling `amneziawg`. Expected: generated config has a VLESS `proxy`
  outbound and no `endpoints`.
- Add the inverse test:
  active `amneziawg`, sibling `vless`. Expected: generated config has one `proxy`
  endpoint and no VLESS `proxy` outbound.

### P1. AWG parser still accepts malformed links and corrupts `+` in keys

Evidence:

- `VPNRouter.Core/Services/ServerUriParser.cs:479-493` uses
  `HttpUtility.ParseQueryString(parsed.Query)` for AWG query parameters.
- `ServerUriParser.cs:491-493` assigns `PeerPublicKey`, `PrivateKey`, and `Address`,
  but missing `private_key` or `address` becomes an empty string/list.
- `VPNRouter.Core/Services/ConfigGenerator.cs:1574-1575` then defaults an empty address
  to `10.13.13.2/32` and emits the empty private key.

Why it matters:

- WireGuard/AWG base64 keys can contain `+`. `HttpUtility.ParseQueryString` treats `+`
  as a space in query strings, so a valid unescaped key can be corrupted before it reaches
  config generation.
- A malformed AWG link without a private key or address can survive the parser and fail
  only at sing-box runtime.

Suggested fix:

- Parse AWG query parameters with a helper that preserves literal `+` and applies
  `Uri.UnescapeDataString` per key/value segment.
- Validate required fields in the AWG parser:
  `peer public key`, `private_key`, at least one `address`, host, and port.
- Consider validating base64 WireGuard key shape early enough to produce a user-readable
  error.

Acceptance criteria:

- Add parser tests for `private_key` and `preshared_key` containing literal `+`; the
  parsed values must preserve `+`.
- Add parser tests that missing `private_key`, missing peer public key, and missing
  `address` throw `FormatException` with AWG-specific messages.

### P1. LeakProtection now recognizes endpoint `proxy`, but does not validate it

Evidence:

- `VPNRouter.Core/Services/LeakProtection.cs:284-292` treats an endpoint tagged `proxy`
  as satisfying the "proxy exists" check.
- `LeakProtection.cs:304-323` validates only `proxy` and `proxy-udp` outbounds.
- `VPNRouter.Core/Models/VPNConfig.cs:128-175` shows required endpoint fields:
  `type`, `tag`, `address`, `private_key`, `peers`, peer `address`, `port`,
  `public_key`, and `allowed_ips`.

Why it matters:

- A `proxy` endpoint with empty private key, missing peer public key, no peers, or empty
  address passes local validation and fails later inside sing-box. That weakens the same
  strict validation path that exists for VLESS/HY2/TUIC/SS outbounds.

Suggested fix:

- Add endpoint validation to `LeakProtection` for endpoint tag `proxy`.
- Validate at least:
  - type is `wireguard`;
  - `Address` has at least one value;
  - `PrivateKey` is non-empty;
  - `Peers` has at least one peer;
  - peer `Address`, `Port`, `PublicKey`, and `AllowedIps` are valid/non-empty.

Acceptance criteria:

- Add tests where malformed AWG endpoints fail `LeakProtection.ValidateConfig`.
- Keep the existing valid AWG endpoint test green.

### P2. QUIC reject suppression is keyed to "any endpoint exists"

Evidence:

- `VPNRouter.Core/Services/ConfigGenerator.cs:140` passes
  `proxyIsUdpNative: endpoints != null && endpoints.Count > 0`.
- `ConfigGenerator.cs:1884` skips QUIC reject when `proxyIsUdpNative` is true.

Why it matters:

- Today AWG is the only generated endpoint, so this works accidentally. If another
  endpoint type is added later, or a non-proxy endpoint is emitted, QUIC rejection can
  be disabled for a TCP-only proxy.

Suggested fix:

- Compute this from the actual selected proxy endpoint/protocol, not from endpoint count.
  For example: `proxyIsUdpNative = activeProxyIsAwgOrWireguardEndpoint`.

Acceptance criteria:

- Add a test that a non-AWG endpoint or unrelated endpoint does not suppress QUIC reject
  for TCP-only VLESS.

### P2. Simple Mode still does not accept AWG links as server input

Evidence:

- `VPNRouter.App/SimpleInputDetector.cs:42-54` recognizes VLESS/HY2/TUIC/SS/Naive/
  dns-tunnel, but not `awg://` or `amneziawg://`.
- `VPNRouter.App/ViewModels/MainWindowViewModel.SimpleMode.cs:455-456` and
  `MainWindowViewModel.SimpleMode.cs:607-608` show user-facing supported schemes
  without AWG.

Why it matters:

- Manual AWG paste in Simple Mode can be classified as invalid before it reaches the
  new parser gate. Users with an lx build will see a generic "unsupported input" path
  instead of either successful import or the explicit sing-box-lx requirement.

Suggested fix:

- Add `awg://` and `amneziawg://` to `SimpleInputDetector`.
- Let parser errors for unsupported official builds surface as fork-specific messages,
  not as generic invalid link text.
- Update visible scheme hints to include AWG only where that is product-intended.

Acceptance criteria:

- Unit test `SimpleInputDetector.Classify("awg://...") == ServerUri`.
- Manual Simple Mode paste on an lx build imports AWG.
- Manual Simple Mode paste on an official build shows the explicit sing-box-lx/AWG
  requirement.

### P2. Server list subtitle does not have an AWG branch

Evidence:

- `VPNRouter.App/ViewModels/ServerViewModel.cs:345-386` handles hysteria2, tuic, ss,
  naive, and dns-tunnel, then falls back to VLESS transport/security display.
- There is no `amneziawg`/`awg` case.

Why it matters:

- An AWG server can be displayed as a generic VLESS-like `tcp + reality` entry because
  default `Transport` and `Security` fields still exist on the shared model.

Suggested fix:

- Add an AWG branch that displays `amneziawg` or `awg`, optionally with `keepalive` or
  another non-secret cue.

Acceptance criteria:

- Add a view-model test for `Protocol = "amneziawg"` returning an AWG-specific subtitle.

### P2. `tools/build-singbox-lx.ps1` AWG probe can fail falsely and does not exercise AWG-only fields

Evidence:

- `tools/build-singbox-lx.ps1:93-101` writes the probe JSON with
  `Set-Content -Encoding utf8` and then runs `sing-box check`.
- `tools/build-singbox-lx.ps1:95-97` uses a plain WireGuard endpoint without AWG-only
  fields such as `jc`, `jmin`, or `h1`.

Why it matters:

- On Windows PowerShell 5.1, `Set-Content -Encoding utf8` writes a UTF-8 BOM. Some JSON
  parsers reject BOM-prefixed config files. That can make a good binary fail the probe.
- The probe mostly proves top-level `wireguard` endpoint support, not that AWG-only
  promoted fields are accepted. The `Tags:` check is the stronger AWG signal today.

Suggested fix:

- Write the probe file as UTF-8 without BOM, for example via
  `System.Text.UTF8Encoding($false)`.
- Include at least one AWG-only field in the probe config.
- Check `$LASTEXITCODE` after `version` as well as after `check`.

Acceptance criteria:

- Run `tools/build-singbox-lx.ps1` under Windows PowerShell 5.1 and PowerShell 7.
- The generated probe file has no BOM.
- A binary without `with_awg` fails the script; a valid lx binary passes.

### P2. Static feature overrides in tests can race other test collections

Evidence:

- `VPNRouter.Tests/AmneziaWgEndpointTests.cs:22-26`,
  `XhttpTransportTests.cs:19-23`, and `SingBoxFeaturesGateTests.cs:22-31` mutate
  global static `SingBoxFeatures.OverrideAwg` / `OverrideXhttp`.
- `rg` found no `CollectionDefinition` for `SingBoxFeaturesSerial`; only class-level
  `[Collection("SingBoxFeaturesSerial")]` attributes are present.

Why it matters:

- The three classes are serialized with each other, but xUnit can still run other test
  collections in parallel. Any unrelated test that parses VLESS/AWG links during that
  window can observe the temporary override value.

Suggested fix:

- Add a real collection definition for `SingBoxFeaturesSerial` with parallelization
  disabled, or disable assembly parallelization for the small group of parser/config
  tests that share global feature state.
- Longer-term: avoid mutable global overrides by injecting a capability provider into
  parsers/generators, leaving the static probe as production default.

Acceptance criteria:

- Add the collection definition or equivalent parallelization guard.
- Run the affected parser/config test suite repeatedly with parallelization enabled;
  no nondeterministic failures.

## Suggested delegation order for Claude Code

1. Fix `SingBoxFeatures` probe correctness and timeout first. This is the foundation
   for both AWG and XHTTP safety.
2. Fix AWG active-selection and parser validation next. These are user-visible protocol
   correctness issues.
3. Add endpoint validation in `LeakProtection`.
4. Patch Simple Mode and server subtitle UX.
5. Harden the build script and test isolation.

## Final verification checklist

- `dotnet build VPNRouter.sln -c Release`
- Targeted tests:
  - `AmneziaWgEndpointTests`
  - `XhttpTransportTests`
  - `SingBoxFeaturesGateTests`
  - new tests for stale probe refresh, probe timeout, AWG same-host selection,
    AWG parser required fields, and endpoint validation.
- Manual checks:
  - Official upstream binary rejects AWG/XHTTP at intake with a clear message.
  - sing-box-lx binary accepts AWG/XHTTP without app restart after install/update.
  - Simple Mode paste of `awg://...` behaves correctly on both official and lx builds.
  - Generated non-AWG configs have no `endpoints` or XHTTP artifacts.
