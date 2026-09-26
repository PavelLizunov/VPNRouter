# Phase - VPNRouter for Omarchy Quattro

Owner: DSH session; human approved the Micro-Spec on 2026-09-17.
Branch: `dsh/omarchy-plugin-2026-09-17`
Base: `517bf7e227e8c75a17479c07facd02033499aa5b`
Risk: HIGH (network lifecycle, privileges, secrets, cross-process ownership).

## Why

Reproduce VPNRouter as a native Omarchy plugin, not a launcher for Avalonia.
Reuse VPNRouter.Core and sing-box without maintaining a second routing engine.
The owner explicitly requested Gemini workers and authorized the public
`PavelLizunov/omarchy-vpnrouter` repository. That repository was created empty.
The owner supplied `omarchy-test` for initial read-only host inspection.
Installation, VPN mutations, shell restarts and releases remain unauthorized.
Task-branch commits, pushes and draft PR updates are authorized; initial
specification commit and draft upstream PR #296 are already published.

## What - approved intent and invariants

- Native QML/Quickshell bar widget, panel and companion singleton service inside
  the existing Omarchy shell; never a second Quickshell or Avalonia window.
- Linux feature parity: connection lifecycle, server import/selection/testing,
  subscriptions, free configurations, process include/exclude/full routing,
  profiles, custom rules/configurations, DNS/TUN settings and diagnostics.
- Simple/advanced modes, English/Russian, host theme and keyboard support.
- Windows-only Zapret, MTProto wrapper and Windows Service are excluded.
- C#/.NET 10 headless adapter references Core. Plugin source and setup live in
  the separate plugin repository with a root manifest. No forked Core sources.
- Closing the panel preserves VPN. Disabling the plugin stops only its own
  session and disposes owned resources; no implicit adoption of another owner.
- Existing desktop/mobile behavior remains unchanged except reviewed additive
  integration seams; no release/version changes or automatic deployment.

## How - interface and data contract

- Plugin ID: `io.github.pavellizunov.vpnrouter`; repository: `omarchy-vpnrouter`.
- One helper-owned engine; JSON-lines over stdin/stdout, no public HTTP server.
- Protocol version 1: request `id`, `method`, `params`; response matching `id`
  with `result` or structured `error`; asynchronous state/progress events.
- Explicit disconnected/connecting/connected/disconnecting/error/unavailable
  states. Only typed Core readiness establishes connected, not process spawn.
- Bounded input bytes before buffering, JSON depth, collection sizes, queues,
  response size, timeouts and cancellation. No secret-bearing argv or logs.
- Configuration changes validate and conflict-check before persistence; status
  snapshots omit secrets. UI preferences stay separate from VPN credentials.
- Privileged operations use system authorization and narrow operations; never
  QML password collection, NOPASSWD grants, privileged shell or implicit retry.
- Packaging pins compatible backend and sing-box artifacts, checks integrity,
  preserves existing configuration and documents manual authorized setup.
- Implementation sequence: source mapping -> precise shared protocol -> backend
  and disjoint QML/packaging Gemini assignments -> source review -> tests -> PRs.

## Research evidence and limits

Three explicitly routed Gemini workers inspected Core APIs, lifecycle/ownership
and Omarchy examples. Their reports are discovery leads, not acceptance.
Confirmed by coordinator: Core is UI-independent (`VPNRouter.Core.csproj`);
CLI status is terminal output (`Commands/StatusCommand.cs`); current CLI start
has an admin gate and Windows-oriented messages (`Commands/StartCommand.cs`).
`TunOwnershipLock.TryAcquire` fails open when named semaphore creation fails
(lines 78-99); a new Unix consumer must not assume this supplies exclusivity.
`LinuxFirewallManager` runs `sudo -n nft` and reports failure without blocking
(lines 38-44, 152-164); the new plugin must not introduce NOPASSWD or report an
unarmed firewall as protected. These integration findings are in OPEN-DEFECTS.

Omarchy's `manual/32-shell-plugins.md` requires a root manifest and reserves
`omarchy.*`. The `omarchy-` GitHub prefix is a convention, not a parser rule.
Examples: PavelLizunov/omarchy-rog-cetra-control, omarchy-tts, omarchy-mx-ergo.
User reference skill: PavelLizunov/omarchy-plugin-patterns (MIT); authoring and
security references read. Actual installed host API still needs inspection.

Read-only preflight: `omarchy-test` resolved using existing trusted SSH config;
identity `omarchytest`, user `tester`; Omarchy, Quickshell, dotnet, git and Python
were present. No installation or runtime scenario was performed. `linux-worker`
was reachable but dotnet was absent from PATH; do not provision it automatically.

## Tests and six gates

1. Build - SCOPED PASS: Release solution and UI-free publish on snapshot0b8fbacc;
   later Headless snapshots compile. Final delivery-SHA CI remains pending.
2. Tests - PARTIAL: Headless and focused Core checks pass as recorded below.
   Full Core suite and cross-platform gates remain pending; no unisolated
   production-path fixture execution is authorized by these scoped passes.
3. Documentation - PENDING: feature parity matrix, setup/update/remove, protocol,
   limitations and final outcome; coordinated READMEs in both repositories.
4. Independent review - PENDING: Gemini correctness/test/security lenses;
   coordinator source verification and change-verification/security-review.
5. UI/runtime - PENDING: isolated QML/fixture checks, keyboard/scaling/RU/EN,
   themes/multi-monitor and real dataplane on approved Omarchy target. Read-only
   host permission is not authority to install, restart or connect VPN.
6. Integration - PENDING: compatible manifest/backend versions, packaging,
   lifecycle enable/disable/reload, no regression to other platform consumers.

## Rollback

Task commits can be reverted after review; neither main is pushed directly.
No live changes exist to roll back. Plugin removal must be documented to stop
its own session, remove its own artifacts and preserve user configuration.

## Outcome

Status: IN PROGRESS (2026-09-18). Native plugin and UI-free headless adapter
are implemented in the working trees; implementation is not yet published.
The approved full-parity scope remains unchanged.

Observed isolated verification:
- Rule-set fixture isolation: safe red9957faca failed before generation because
  the fixture retained shared testhost AppPaths; green6c7fc82c passed all 6 cases
  after private cache/geo seeding and exact local-tag/path/byte assertions.
  bash-40 exit 0, Release build 426 warnings / 0 errors. TestEnvironmentSafety
  already protects the live data root; the earlier broad live-cache-write claim
  was corrected. This verifies generated shape, not actual SRS binary validity.
- Runtime-path Gemini research was source-checked; no global AppPaths cascade,
  setcap deployment or ownership trust expansion was accepted. The readiness
  gate checks DataDir/bin before startup can deploy a bundled copy. See
  omarchy-runtime-path-review.md; privilege/distribution decisions remain open.
- Expanded offline Core slice on `5edc14bfd5f550fcc61129122cb28c8d64e287ab`
  (bash-39 exit 0): test-project Release build 403 warnings / 0 errors; 76 tests
  passed, 0 failed, 0 skipped across 12 audited classes. Disposable HOME/XDG was
  removed by the job's cleanup trap. Runtime DNS commands were fake-runner only.
  This is additional scoped coverage, not the full Core gate. Network-dependent
  and production-binary-path fixtures were excluded and recorded in OPEN-DEFECTS.
- Round 9: bounded post-admission handler scheduling and precancelled settings
  persistence regression verified on `5edc14bfd5f550fcc61129122cb28c8d64e287ab`.
  Release Headless test build: 31 warnings, 0 errors; 36 groups passed twice,
  including 41 lifecycle checks (bash-38 exit 0). Earlier candidate2bc9072e failed
  backpressure delivery because the test sent EOF before response completion;
  the test now waits for all 12 unique IDs before EOF. Production EOF semantics
  are unchanged. Separate paced-response admission bound remains tested.
  Scoped Gemini review completed after one incomplete reviewer attempt; coordinator
  limits recorded in omarchy-transport-security-review.md. Later edits are comments/docs.
- Saved real consumer checker `tests/check-published-backend.py`, plugin snapshot
  `1712eb082f72165aecf6169477a09641c0871f3d`, passed against backend publish
  b93d5a95 (bash-35, exit 0): production setup/wrapper returned 9 unchanged
  profile names, then 10 including a Linux-only canary in the disposable installed
  copy. Independent Gemini review exposed identical generic/Linux names in the
  original proof; separate canary now distinguishes selection without altering
  supplied publish inputs. Both requests remained read-only and exited on EOF.
  This is not live shell or routing evidence. RU/EN README documents reproduction.
- Plugin snapshot `f3b1e1a62835253635e07062436449b98e7ff071`: setup now preserves
  mandatory published profiles/default.json and default-linux.json with bounded,
  non-symlink regular-file reads. Red setup test reproduced missing catalog;
  38 packaging tests passed after fix (bash-32). Real FDD backend publish from
  `b93d5a952f38dba1d1ab788038b385688e5ff968` passed staged setup handshake and
  byte-identical catalog checks in a disposable plugin tree (bash-33, exit 0).
  Isolated HOME stayed empty; Omarchy lock/validator commands were stubbed.
  No live installation or activation. Integrity/version/runtime sing-box path
  remain unresolved; see `omarchy-packaging-profile-review.md`.
- Offline cache isolation red/green (bash-31, overall exit 0): snapshot
  `2e60e688adf4142f533a4a4f2b0e55f1bbc28471` failed only the new foreign-cache
  regression (34 groups passed, 1 failed); fixed snapshot
  `b93d5a952f38dba1d1ab788038b385688e5ff968` passed all 35 groups. Tests use two
  private temporary directories, verify foreign DNS mode exclusion, own/default
  cache loading and unchanged cache bytes. CLI already overrides AppPaths;
  the fix covers offline-helper/embedded backend callers with a separate dataDir.
  Online Core GitHubProfileSource still uses process-global AppPaths; this is
  aligned by CLI startup but not a claim of multi-instance embedded isolation.
  Headless README/zone instructions now reflect actual bounded transport behavior.
- Snapshot `616848d81d2f3a5ae4e5e905ec8f0f699dfb8f58`: complete Release
  solution build succeeded (434 warnings, zero errors), 96 explicitly scoped
  Core tests and 35 Headless groups passed (bash-30, exit 0). Coexistence fixture
  now isolates Linux runtime/AppPaths and verifies real flock reconnect.
  Eight Windows-only runner tests return early on Linux. See
  `omarchy-core-delivery-review.md` for independent Gemini review disposition,
  safe filter, untested platform limits and pending commit/CI prerequisites.
- Backend snapshot `5d48f257c3dfbc57d7335dcc8843a4847e591718` and plugin
  snapshot `d655365f05f315fd841306c4ccbb0b3db251d24f`: 35 Headless groups,
  five Node suites and Quickshell offscreen loading passed (bash-29, exit 0).
  Nullable dnsModeOverride now survives settings.get and QML Service mapping;
  profile-default reset remains null on refresh and subsequent unrelated saves.
  Service also preserves dnsModeSemantics (a test-only copy had masked its loss).
  Node regression executes actual Service getSettings, draft initializer and
  payload functions. RU/EN README notes describe reset and protection precedence.
- Plugin snapshot `27d7e3e3c2e2e82d92ae959e4b88d630bc2d6a23`: five Node
  suites and actual Quickshell offscreen component loading passed (bash-28,
  exit 0). This includes the corrected real QML settings payload extraction.
  Runner substitutes KeyboardPanel; warnings include objects outside a graphics
  scene and scanner import hints. This proves loading, not rendering, popup,
  keyboard, scaling or live shell integration. No active host plugin was changed.
- Snapshot `a1e31c1284ac928a55544e4d2b7f25259cde8705`: Release Headless
  build and 35 executable groups passed (bash-27, exit 0). Urgent controls now
  validate before cancellation: unknown cancel fields, invalid target IDs and
  nonempty disconnect params are rejected without invoking the backend or
  affecting an active operation. A subsequent valid cancel still succeeds.
- Draft upstream PR #296 remains at specification commit `b8bde92c`; its four
  observed checks are green. These do not cover any implementation snapshot.
- Snapshot `188e011ea8779e5cab0a60fa428748f3459124c0`: Release Headless
  build and 34 executable groups passed (bash-26 green exit 0), including the
  persisted real subprocess stdout-stall check and a new paced asynchronous
  response-admission bound. Red control `42542937b14f75ed26c49ab017cb0b0af3a96625`
  uses the same tests with only the old Server: exactly the new bound test fails
  (50 invocations versus limit14; 33 other groups pass). The fix independently
  bounds response-waiter tasks after dispatcher execution completes.
  Scoped independent Gemini review and coordinator disposition are recorded in
  `plans/omarchy-transport-security-review.md`; overall verdict changes_required.
- Snapshot `3ddce2f14cf5f767cb85d042728836e98fc46251`: Release Headless
  build and 32 executable groups passed (job bash-22, exit 0). New coverage
  verifies a 3-second stdout write/flush stall deadline with open stdin,
  cancellation-ignoring asynchronous and synchronously blocking streams, and
  handler teardown without waiting for output drain. Normal temporary
  backpressure still drains all responses. Core source is unchanged from the
  101-test snapshot below.
  Real subprocess verification on the same snapshot also passed (bash-24,
  exit 0): stdin stayed open, stdout was never read, backend exited with code 0
  after 4.68 seconds and created no data files. The preceding Python run reached
  the same backend outcome but failed during pipe-close cleanup; it is not
  counted as a successful command.
- Snapshot `0b8fbacc0c2697de97b33627f18e037d28a8cc8b`: full solution build
  and framework-dependent Headless publish passed (job bash-21, exit 0), with
  no Avalonia dependency or payload and both bundled profile catalogs present.
- Coordinator snapshot `0b8fbacc0c2697de97b33627f18e037d28a8cc8b`:
  Release headless build (31 warnings, 0 errors), 30 executable groups including
  41 lifecycle checks, and 101 focused Core tests passed (job bash-19, exit 0).
  This includes retained event-time readiness, real SIGTERM with open stdin,
  platform profile selection and capability refusal on retry from error.
  Core firewall fixtures now use explicit local profiles, isolated AppPaths and
  Linux runtime lock directories; three previous failures are covered without
  weakening their assertions. Full Core suite remains unverified.
- Plugin settings payload now omits unsupported DNS-lockdown without clearing an
  existing safety preference. Coordinator Node tests execute the actual QML
  payload function, replacing a copied simulation; local test exit 0.
- Snapshot `407c31c4b65f159700e9aa4f1789a4ea7b901d06`: Release headless
  build, 27 executable test groups, 56 focused Core tests and framework-dependent
  publish with bundled Linux/default profiles passed (coordinator job exit 0).
- Real stdio reads on that snapshot created no data files. A SIGTERM test exposed
  a blocked stdin read; closing stdin had previously masked that failure.
- Snapshot `f7513760ce9ba84fa7f269bda5000729c9d476b2`: worker reports 30
  groups passing including SIGTERM with stdin kept open. Coordinator inspected
  cancellation and bounded output-disposal implementation; see stdio evidence.
- Snapshot `1adc26bdafe2d994853b235e0c4f79e5af2083b4`: worker reports 35
  lifecycle checks and 30 groups after typed recovery readiness fixes. This is
  scoped worker evidence, not a live connection or whole-product acceptance.
- Solution/CI now include Headless and its executable tests; plugin CI covers
  Node, packaging and shell syntax. These new CI jobs have not run on GitHub.

Remaining acceptance work:
- A separate source-only privileged-helper Micro-Spec was presented to the
  owner; no explicit approve has been received. Automatic goal continuation
  does not authorize that architecture. Continue only already-approved work
  until the owner decides. No helper implementation or policy installation.
- Privileged firewall/DNS adapter, verified protection/readback/cleanup and
  compatible pinned sing-box distribution. Unsupported capability flags are
  honest refusal, not completed Linux parity.
- Real host popup, keyboard, scaling and lifecycle acceptance. Offscreen QML
  substitutes KeyboardPanel and cannot establish those outcomes.
- Final security/change review, full Core regression gate, ledger reconciliation,
  task commits/pushes and PR checks in both repositories.
- Installation, activation, live VPN/routing and root provisioning remain outside
  current authority. Isolated tests do not authorize them.

Unrelated untracked workspace files are preserved and excluded from commits.
Read-only round-9 probing confirmed neither powershell nor pwsh on the control
plane or the authorized Omarchy worker. The worker has gh. Methodology line48
permits exact PR-check substitution, but the canonical agent-contract requires
the PowerShell script after push and declares conflicting guidance a defect.
Do not silently treat the weaker methodology as authority to waive this gate.
No new runtime was installed, no implementation commit pushed in this round.
Update 2026-09-18: the existing WINBRAT PowerShell5.1/Git/gh route was verified
with byte-matched canonical script for spec SHA b8bde92c (bash-45 exit0,
4 green, no tolerated/pending/red checks; temporary directory removed).
No runtime installation or waiver required. Implementation SHAs still need their
own checks. Delivery inventory and corrected commit grouping are recorded in
plans/omarchy-delivery-plan-2026-09-18.md; no implementation push yet.
