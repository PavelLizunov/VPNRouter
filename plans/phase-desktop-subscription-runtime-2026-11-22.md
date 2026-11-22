# Report the selected desktop core for VPNRouter subscriptions

## Why

vpnctl can limit profile delivery to the desktop releases using a specific
managed sing-box build. VPNRouter needs to report the core selected for
connections when it fetches those profiles.

## Scope

Send the desktop platform and the selected executable's canonical
`sing-box version` value to HTTPS VPNRouter app-config endpoints. Use the
existing runtime selection in the app and Omarchy headless service. Pin
`1.14.4-vpnctl.3` in desktop packages. Keep Android and legacy subscription
headers compatible. The reported values do not attest the device.

Missing or unreadable binaries leave the new headers unset. Hosts with a
configured policy then omit the restricted profile. Roll back the client
change by reverting this branch and clear the vpnctl filter if needed.

## Verification

| Gate | Evidence |
|---|---|
| Build | Release solution build in GitHub Actions |
| Tests | Managed core parsing, existing subscription tests, and Arch packaging |
| Documentation | Platform pins and the verification outcome in this brief |
| Review | Independent review of the client and controller diff |
| UI and remote | No visual changes. Verify desktop and Omarchy profile delivery before enabling the production filter |
| Integration | Existing client subscription behavior, matching core, and version rejection in the vpnctl contract tests |

## Outcome

The local Arch packaging suite passed 132 tests. Independent review found a
stale hash and source revision for the bundled Arch core. Both now match the
verified release archive and its source license. Release build, .NET tests and
integrated delivery require candidate validation.
