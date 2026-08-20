## 2026-08-20 - Diagnostics Redactor Fail-Open on Numeric String Secrets
**Vulnerability:** String scalars in `DiagnosticsRedactor.cs` with all-digit string values passed through redaction unredacted unless explicitly matched by a `SecretKeys` blacklist.
**Learning:** Mixing a blacklist (`SecretKeys`) into a fail-closed allowlist (`SafeKeys`) created a fail-open flaw for unlisted keys containing numeric string values.
**Prevention:** Enforce strict allowlisting (`SafeKeys`) for all string scalar values without exception.
