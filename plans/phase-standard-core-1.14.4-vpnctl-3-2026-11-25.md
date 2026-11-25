# Standard core 1.14.4-vpnctl.3

Standard v2.51.1-r2 was built from aaa02215 with the 1.14.4-vpnctl.2 core.
Use the official 1.14.4-vpnctl.3 release in the next Standard build. Update
the version, archive hash and Windows CI cache key in lockstep for the
Windows, macOS, Linux, Arch and Android packaging paths. Bind the Arch
notice record to the exact Linux payload and upstream source. Preserve the
legacy Android AAR variant, the current Cronet version and the historical
DNSTT compatibility threshold.

Risk is a stale or incompatible bundled artifact. Roll back the pin changes
together. Compare official SHA256SUMS, the files and GitHub build attestations.
Use the hosted Release build, existing compatibility and packaging tests, an
Android Release APK build and the Windows update scenario. Record source
checks here and request an independent review of the pins and notice metadata.
The core update has no UI changes. Verify the resulting release binaries and
data plane at the next authorized candidate.

## Outcome

The Windows amd64 ZIP, macOS universal ZIP, Linux amd64 archive and legacy
Android AAR match their SHA256SUMS and GitHub release asset digests. Each
download passed GitHub attestation verification for refs/tags/v1.14.4-vpnctl.3,
source fe24739a8b8c1ebd573269e50b124366310e4d3e and the hosted release workflow.
See [the core build](https://github.com/PavelLizunov/sing-box-vpnctl/actions/runs/44745019219).

The Arch payload hash was computed from the executable in the verified
Linux archive. The archive's GPL text matches the cataloged text and the
license at the exact upstream commit. The upstream go.mod and go.sum blobs
match those used for .2, so the existing Cronet source and payload pins apply.
The DNSTT fixture for .2 preserves the published minimum.

Scoped diff, YAML, Python and shell syntax checks passed. Hosted Release,
Windows update, Android APK and packaging gates remain pending on the
integrated commit.
