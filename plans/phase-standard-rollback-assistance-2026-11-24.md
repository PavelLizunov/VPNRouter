# Standard Windows rollback assistance, 2026-11-24

## Intent

After an upgrade, offer a verified previous official Windows Standard build when
the current build has three comparable failed manual connections and a previous
build is known to have worked with the same configuration. Keep the offer and
the installation handoff separate from the assistant and crash-diagnosis code.

## Scope

- Persist manual connection history in a rollback-owned file keyed by build and
  configuration fingerprint. Do not store subscription URLs, UUIDs or raw logs.
- Count only completed manual technical failures. Exclude cancellation,
  background retries, no network, provider or authentication failures, local
  environment conflicts and assistant-driven connects.
- Reset the counter on a confirmed connection or a different build.
- Capture the already-confirmed previous installed version from
  `.update-installed-version` before the existing startup receipt check consumes
  it. This is evidence of the installation, not proof that the old configuration
  worked.
- When the captured previous version is exactly the signed recovery target and
  there is no local success record, ask a separate attestation question: did the
  connection work on that version with the same settings? Yes unlocks a
  medium-confidence offer; no or unknown goes to local diagnostics.
- Package provenance alone never produces an offer. A clean install with no
  previous-install or local-success evidence gets local diagnostics only.
- Check the active routing configuration, not the whole saved file. Active DNSTT,
  VMess or new VMess fields, pinned single-outbound rows, duplicate-name
  subscription rows that the old build would resolve differently, ambiguous
  detour chains and unverifiable active custom configs produce local diagnostics
  and block both the offer and the final apply. Inactive rows and subscriptions
  with unrelated new features do not block a compatible active row.
- Let the user dismiss an offer for the current build and configuration.
- Select only an older official Windows Standard x64 release from the existing
  update source, with the release SHA-256 sidecar, official HTTPS release URL
  and a settings-schema compatibility floor.
- Require a separate confirmation, back up settings through the existing
  downgrade path, apply through the existing updater, and record a handoff that
  the next supporting build can verify.
- Keep automatic update checks from offering a newer build while a deliberate
  rollback choice is active. A manual check remains available.

## Invariants

- The assistant/model never supplies a download URL, version or install action.
- Windows Standard only. macOS, Linux and Android remain out of scope.
- The updater's staging, digest verification and downgrade backup remain the
  only installation path.
- A failure, cancellation or corrupt package leaves the installed build and the
  settings backup available for recovery.

## Verification

- Core decision tests: threshold, exclusions, build/config reset, success reset,
  dismissal, confidence, known-regression input, clean-install no-offer,
  exact previous-install attestation and negative/unknown diagnostics.
- Artifact policy tests: platform, architecture, channel, version order, digest,
  official URL, revocation input, schema floor, signed manifest, full commit
  oracle, canonical `_bootstrap/VPNRouter.App.exe` path and misleading versions.
- ViewModel handoff tests: separate attestation, explicit final confirmation,
  incompatible artifact refusal, active-config refusal at final apply, handoff
  creation, apply failure recovery and automatic-update suppression.
- Active-config compatibility tests: generated and subscription scope, DNSTT,
  VMess fields, single-outbound pins, duplicate-name exact rows, detour chains,
  custom configs and newer targets.
- Static diff review only in this checkout; compiled tests and the Windows
  scenario are left to the parent integration and WINBRAT gates.

## 2.50.5 package evidence

The accepted update ZIP `VPNRouter-update-v2.50.5-win.zip` (72127549 bytes,
SHA-256 `d9053ea0...68c2`) contains the canonical
`_bootstrap/VPNRouter.App.exe` with PE ProductVersion
`1.0.0+097393a79641848e84cb76aa324b8bc873da3ed6`. `VPNRouter.GUI.exe` has no
PE VERSIONINFO. The staged check therefore uses the canonical App.exe path and
the full commit suffix, not the product version label and not the GUI launcher.
The embedded manifest binds the same commit, size and SHA-256.

## Previous-version receipt, 2.50.5 to candidate

The published 2.50.5 updater writes `AppPaths.DataDir/.update-installed-version`
as `timestamp\n2.50.5\n` before it exits for the update. The candidate's
`CheckInstallReceipt` reads that data-directory receipt when no protected
per-installation receipt exists, sees `current > previous`, and now captures
`2.50.5` into `connection-rollback.json` before deleting the receipt. A clean
install has no receipt and therefore no previous-install evidence. A failed
update (`current <= previous`) is not captured.

## v2.50.5 updater behavior

The published 2.50.5 build predates this code. Its `CheckOnStartupAsync` always
runs a background check (`AutoCheck` is not consulted) and can show a dismissible
update banner. It never installs without the user pressing Update; `DownloadAndApply`
is an explicit command. It cannot read a new hold file or the local rollback
choice. The rollback choice therefore prevents automatic re-offers only in builds
that contain this code; 2.50.5 can still show its own passive banner. The client
does not mutate `github_repo`, `channel` or other settings to fake suppression,
because that would silently disable updates and break the settings-preservation
requirement.

## Unknowns and limitations

- The parent supplied the signed-manifest route: an embedded P-256 public key,
  the exact `windows-2.50.5.json` bytes and the DER signature. The client now
  verifies that manifest before the automatic rollback path can run. Authenticode
  publisher identity is still absent for the published desktop binaries; the
  signed manifest is the trusted provenance route.
- The client requires the target to appear in the official stable release history
  and enforces a settings-schema floor. The artifact policy accepts an optional
  revocation list in tests. The production caller supplies no revocation feed.
- Version 2.50.5 predates local connection history. A matching receipt identifies
  the immediately preceding installation. A user who confirms that the same
  configuration worked on 2.50.5 can receive a medium-confidence offer.
- Version 2.50.5 also predates restart verification. A later supporting build
  reports its own version and the requested target. The sequence of intermediate
  launches cannot be reconstructed from this record.

## Observed outcomes

- Added the rollback history, failure classifier, signed-manifest verifier,
  artifact policy, attestation flow, updater size binding and Simple-page card.
- Verified the embedded manifest signature locally with the supplied public key
  and confirmed the accepted ZIP size and SHA-256. The physical
  `_bootstrap/VPNRouter.App.exe` ProductVersion is
  `1.0.0+097393a79641848e84cb76aa324b8bc873da3ed6`; the GUI launcher has no PE
  VERSIONINFO.
- No build, compiled test, installer run or Windows scenario was executed in
  this checkout. The parent owns Git, integration and the WINBRAT gate.

## Integration checks

Source review found that the original implementation reused mutable update-dialog state after downloading a recovery package. The installer now carries the consented decision and signed package as local operation inputs. It rechecks settings, connection state and compatibility on the UI thread before reserving the restart. Recovery and ordinary version history have distinct confirmation views. The regression test changes the selected profile or clears the suggestion while a fake download is paused.

The connection fingerprint now covers routing rule contents, process lists, TUN and DNS settings, effective outbound pool and the active custom file contents. The startup coordinator records completed manual attempts. Normal engine teardown also occurs while retiring a previous session, so only a lost startup process identity produces the unexpected-stop diagnosis. A later confirmed connection clears the earlier failure streak.

The 2.50.5 client can start a Telegram proxy whose Python and certifi marker files exist. Assisted recovery checks the runtime when Telegram autostart is enabled. The confirmation discloses that installing the optional proxy afresh on 2.50.5 fetches an older CA bundle. Targeted tests also cover a compatible detour chain and a missing or ambiguous upstream.

The embedded P-256 signature verified with OpenSSL on 24 November. Both confirmation views, the source-diagnosis panel and the Core project XML parsed. Compiled regression tests and physical installation/relaunch on WINBRAT are pending.

Windows CI on `115ab275` failed to select the recovery target and the stale-decision
test then waited until the four-minute runner timeout. With the Git Windows
checkout filter, the 293-byte signed manifest became 294 bytes and OpenSSL
rejected its signature. Preserve the committed manifest and signature bytes on
checkout. Bound the test wait and check the confirmation before the download.
Verify the Windows job and the installed consent flow.

After the checkout attributes change, all three Git autocrlf modes produced
the signed 293-byte JSON (SHA-256 `6032a12f353805e657732b69f31da02ee6a5c90094fb4bbfdca193cb49b83713`).
OpenSSL accepted every checkout and rejected the deliberately changed newline.


## Follow-up to the Windows acceptance review

At `9d155378` all ten hosted checks passed. Cover the continuation after a
delayed release listing in the UI tests, including a profile or compatibility
change. Limit the restart message to the requested and observed versions.
Run the usual staging cleanup when an update check is suppressed. WINBRAT
remains the source of evidence for actual installation and restart.

## WINBRAT acceptance on 24 November

The installed Windows x64 preview reported commit `8fe3eb9c`. Actions run
45415698640 built the preview from that pinned source. The candidate consumed
a previous-version receipt seeded in the published 2.50.5 format. Three manual
attempts with a disposable test engine triggered the prior-success question
only on the third failure. A real local conflict left the count at zero.
With the real engine, two Phase B failures were followed by a successful
connection that cleared the count.

Prior-success testimony and final installation consent were separate steps.
Cancel preserved the installed preview and settings. After explicit confirmation
the updater launched official 2.50.5 at commit `097393a7`. The installed EXE
and DLL matched the corresponding files in the 72,127,549-byte release ZIP.
The active settings and their retained backup matched the pre-install bytes.
The healthy target removed the temporary application backup after 30 seconds.

The tester returned to its original `2890a246` installation and settings,
with the application stopped. This run covers the installed flow on
`8fe3eb9c`. Hosted tests on `d61971a6` cover the later revalidation after
the release list loads. The three-failure threshold used a test engine, and
the seeded receipt supplies installation history only.
