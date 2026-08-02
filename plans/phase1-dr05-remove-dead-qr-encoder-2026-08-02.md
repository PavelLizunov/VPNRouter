# Phase 1 — DR-05 remove dead QR encoder

**Owner**: Codex

**Branch**: `codex/dr-05-remove-dead-qr-encoder`

**Audit ref**: dependency replacement task list DR-05, draft PR #99

**Effort**: 1–2 hours

**Risk**: LOW — delete a test-only encoder while preserving the real Android ZXing scanner and config share/import flow

**Blast radius**: one Core file, one dedicated test file, and three QR-only tests in `ConfigShareDocumentTests`

**Rollback**: revert the implementation commit or close the branch

## Why

`VPNRouter.Core.Services.QrCode` is a vendored pure-C# encoder with no production
caller. Repository references outside plans exist only in its dedicated tests and
three tests that exercise the encoder rather than `ConfigShareDocument`. Keeping
the implementation and self-tests adds about 650 lines without supporting the
actual Android scanner or file-based config share flow.

## What

- Delete `VPNRouter.Core/Services/QrCode.cs`.
- Delete `VPNRouter.Tests/QrCodeTests.cs`.
- Remove only the three `QrCode.EncodeText` tests from
  `VPNRouter.Tests/ConfigShareDocumentTests.cs`.
- Keep Android's ZXing AAR/JAR, scanner launcher, QR apply flow, and their NOTICE
  attribution unchanged.
- Do not add a replacement QR package or change import/share behavior.

```diff
- VPNRouter.Core/Services/QrCode.cs
- VPNRouter.Tests/QrCodeTests.cs
- three encoder-only ConfigShareDocumentTests
+ no replacement
```

## How

1. Have Qwen 3.8 independently inventory compile-time, reflection, generated,
   source-link, and documentation references to `QrCode`/`EncodeText`.
2. Trace Android scan/apply and config file share/import to their actual ZXing and
   `ConfigShareDocument` implementations.
3. Delete only the dead encoder and tests that exist to exercise it.
4. Re-run the repository reference search and verify NOTICE still describes the
   live ZXing dependency.
5. Build Release, run focused config-share/Android scanner tests, then the
   available regression suite and clean-environment CI.
6. Fill Outcome, push to `origin`, and keep the PR draft.

### Tests written

- None: production behavior is unchanged and the removed tests cover only the
  deleted implementation.

### Verification approach

- No production/reflection/generated reference remains to the deleted type.
- `ConfigShareDocument` serialization, parsing, validation, and round-trip tests
  stay green.
- Android QR source-surface/scanner tests stay green.
- Solution Release build and clean-environment CI pass.

## Verification gate

- [ ] **Gate 1 — Build clean**: solution Release build has 0 errors.
- [ ] **Gate 2 — Tests green**: focused config-share/Android tests and full CI pass.
- [ ] **Gate 3 — Docs**: Outcome is filled; NOTICE remains accurate.
- [ ] **Gate 4 — Self-review**: final Qwen reference/deletion review has no blocker.
- [ ] **Gate 5 — MCP verify**: N/A — no production or UI behavior changes.
- [ ] **Gate 6 — Characterization diff**: public production surface is unchanged because the deleted type had no caller.

## Outcome

To be filled after implementation and verification.
