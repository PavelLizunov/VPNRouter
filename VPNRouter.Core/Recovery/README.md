# Signed Windows recovery manifest

This directory contains public material only:

- `publisher.pem`: dedicated owner P-256 public key.
- `windows-2.50.5.json`: exact signed manifest bytes, including the trailing
  newline.
- `windows-2.50.5.sig`: ASN.1 DER ECDSA SHA-256 signature.

The signature is verified in `SignedRecoveryManifestTrust` before the manifest is
used. The private key is not in the repository.

Provenance recorded by the parent for this package:

- release receipt `plans/phase-stable-2.50.5-2026-10-20.md`
- tag commit `097393a79641848e84cb76aa324b8bc873da3ed6`
- update asset `VPNRouter-update-v2.50.5-win.zip`
- size `72127549`
- SHA-256 `d9053ea06a8abc0ef7bee09b52f83d035b662e3e838818a254f7077aae6068c2`

Physical package check:

- `_bootstrap/VPNRouter.App.exe` has PE ProductVersion
  `1.0.0+097393a79641848e84cb76aa324b8bc873da3ed6`.
- `VPNRouter.GUI.exe` has no PE VERSIONINFO, so it is not the version oracle.
- The client requires the canonical `_bootstrap/VPNRouter.App.exe` path and
  matches the full commit suffix to this manifest. A wrong path, a missing
  commit or any other commit is refused. The updater separately verifies the
  signed archive size and SHA-256.

The manifest is the signed-manifest provenance route for Windows because the
published desktop binaries are unsigned and Authenticode identity is absent.
