## 2026-08-25 - Redact HTTP Basic-Auth UserInfo in CrashReporter Scrubber
**Vulnerability:** `CrashReporter.ScrubSecrets` retained basic-auth `userinfo@` (e.g., `https://username:password@domain.com/path`) when scrubbing HTTP/HTTPS URLs, leading to potential leakage of credentials in crash reports, logs, and UI status strings.
**Learning:** Generic HTTP URL redactors that preserve domain/host for diagnostic purposes must explicitly match and discard any optional `userinfo@` component prior to the hostname.
**Prevention:** Ensure URL sanitizers separate scheme, user credentials, hostname, and path/query parameters cleanly so that user credentials never survive string redaction.
