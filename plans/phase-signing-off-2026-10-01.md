# Code signing is not pursued (owner decision)

## Why

On 2026-10-01 the owner decided not to do code signing for now ("too complex"). PR #421 had just added a "Code signing
policy" section to both READMEs that said the project is applying to the SignPath Foundation, and the ledger still held a
P1 entry waiting for the enrollment. Both would now be untrue or misleading.

## What

- `README.md` and `README.ru.md`: the policy section is replaced by a short, honest note: the Windows binaries are not
  signed, SmartScreen or an antivirus may warn, verify downloads with the `.sha256` file; signing is not planned and the
  runbook keeps the steps.
- `docs/AGENTS.md` and `docs/code-signing-application.md`: marked ON HOLD; nothing was ever submitted.
- `plans/OPEN-DEFECTS.md`: the P1 signing entry is closed as an owner decision (it was one of the owner-gated items of the
  cut-stable gate).
- The installer work (I2 Inno Setup, I3 `cleanup`) continues without signing: the Defender exclusions stay a choice in the
  installer and the SmartScreen warning is accepted.

## Verification

Docs only: CI (grep and contract tests).

## Outcome

Merged after green exact-head CI.
