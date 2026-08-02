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

- [x] **Gate 1 — Build clean**: desktop solution and Android Release builds have 0 errors.
- [ ] **Gate 2 — Tests green**: focused config-share/Android tests and full CI pass.
- [x] **Gate 3 — Docs**: Outcome is filled; live ZXing NOTICE is preserved.
- [x] **Gate 4 — Self-review**: final Qwen reference/deletion review returned `APPROVE` with no blocker.
- [x] **Gate 5 — MCP verify**: N/A — no production or UI behavior changes.
- [x] **Gate 6 — Characterization diff**: Android source characterization remains green; the deleted type had no caller.

## Outcome

- Deleted the 599-line `QrCode` encoder, its 142-line dedicated test file, and
  exactly three misplaced encoder-only methods from `ConfigShareDocumentTests`.
  Net working diff before this Outcome: 788 deletions and 9 additions.
- The remaining ConfigShareDocument tests retain schema, serialization, parsing,
  validation, preview, filename, and null-default coverage. Their stale summary
  no longer references the removed encoder.
- Repository production/reflection/generated symbol search for `QrCode` is
  empty. Android's source-link compile and its real ZXing camera scan/apply flow
  remain intact; NOTICE keeps the live ZXing Apache-2.0 entry.
- Qwen 3.8 max-preview returned `DELETE` before the edit and `APPROVE` after the
  edit. Two unrelated Android comment/license-name findings are recorded in
  `plans/refactor-backlog.md`; the final review found nothing else.
- Release solution build: 0 errors. Real `net10.0-android36.0` Release build: 0
  errors. Focused ConfigShare/Android characterization tests: 31 passed.
  Accessible regression: 2631 passed, 2 skipped, 0 failed — exactly nine fewer
  tests than the pre-change 2640 because only encoder self-tests were removed.
- Clean-environment CI is the remaining Gate 2 check.
