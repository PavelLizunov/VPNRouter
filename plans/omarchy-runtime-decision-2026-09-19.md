# Omarchy runtime and privilege decision research

Research only, 2026-09-19. Backend64e1e805, plugin541b112, both draft PRs.
No source changes, binary execution, remote operations or policy installation.

Verdict: Extend — existing Core lifecycle/process seams and Linux platform
authorization mechanisms, with a Headless-scoped runtime policy. This is not
approval to implement a root broker or to trust plugin-relative executables.

## Confirmed source evidence

- Headless PlatformCapabilityVerifier.cs:76-78 checks AppPaths.SingBoxExePath.
- StartupPipeline.cs:1116-1144 copies bundle to data-dir, comparing size only.
- SingBoxManager.Lifecycle.cs:56-58,657-659 selects data-dir on non-Windows;
  lines915-929 choose capability execution or pkexec.
- SingBoxFeatures.cs:105-133 prefers bundle and executes version to read tags.
- LinuxStop.cs:211-218 permits App/CLI signal-helper hosts, not Headless.
- LinuxFirewallManager.cs:410-420 creates host-wide output drop with LAN and
  endpoint exceptions. Per-UID table names alone cannot isolate its effects.
- Plugin setup stages bin/backend. Its presence does not establish Core runtime
  selection, binary integrity, authorization or successful TUN operation.

Gemini mapped additional deep-verifier, doctor, diagnostics and ownership
consumers. Those pointers are implementation audit inputs, not independently
verified completeness. Before changing resolver behavior trace every reachable
Headless consumer and preserve other clients' defaults.

## Review disposition

Reject global AppPaths fallback and unconditional Linux/macOS ExecutablePath
honoring: they alter existing clients and extend trust without policy.
Reject self-reported version/build tags as authenticity evidence. Reading tags
requires executing code; a malicious binary can report arbitrary text.
Reject a broad claim that false firewall/DNS flags prevent elevation: Core still
has a pkexec fallback. Explicit administrative execution is not itself proof of
an exploit; risk depends on consent, retained authority and inputs.
Reject automatic firewall removal on client loss as an unconditional safety
policy: it can restore direct traffic while kill-switch was requested.
Reject presenting retained blocking as harmless: it can interrupt all host users
and requires explicit recovery semantics. Do not weaken full Linux parity into
status-only operation as a solution.

Official pkexec documentation confirms that arguments are not validated by
pkexec and retained/implicit authorization requires a constrained trusted API:
https://www.freedesktop.org/software/polkit/docs/latest/pkexec.1.html

## Proposed next boundary

First seek explicit approval for source-only Headless runtime-selection policy,
no implicit elevation, unified consumer identity and isolated negative tests.
Production remains unavailable without approved trusted runtime metadata and
provisioning; missing prerequisites must not silently fall back to bundle, PATH,
data-dir, downloads or pkexec. Tests inject fixture identities without claiming
fixture hashes establish release provenance. Existing desktop defaults stay intact.

Full broker design remains separate: authenticated session ownership, host-global
coordination for full-tunnel protection, least-authority process/config handling,
explicit authorization and recovery after client/broker loss. Recommend explicit
admin consent (not allow_active=yes or auth_admin_keep), retaining protection on
unexpected loss rather than silently unblocking, with an separately authorized
recovery operation. These recommendations are not implemented or approved.

No pinned release hash/version was invented. Root-owned storage alone does not
prove binary provenance or restrict the effects of caller-controlled configuration.
Source-only tests do not prove real Linux firewall/DNS/dataplane behavior.
