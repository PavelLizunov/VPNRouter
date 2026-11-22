# DeepSeek connection repair

## Scope

Add a deliberate Repair connection action to the VPNRouter assistant.
Start with Android after a failed managed DNSTT connection and reuse the
desktop subscription gate, account quota, and typed plan model. The first
version can retry the current connection and refresh the registered source.
A verified alternate is eligible only when the existing policy already
permits automatic selection. Windows can reuse the same request contract
and its own existing start and Apply implementation.

Base the client on the setup assistant from PR #721. The server authorization
from vpnctl PR #358 now includes current main at `be1128df`. Release and
production gateway rollout are separate from this implementation.

## Evidence and action contract

Record a bounded snapshot of the current failed Android attempt at the
service boundary. Include whether TUN was established, the failure code,
the coarse protocol type, the DNSTT command and outbound stages, the
positive callback flag and elapsed time. Keep server identities, app lists,
subscription links, keys, raw logs and custom JSON on the device.

Extend vpnctl with a separate versioned repair request and the existing
subscription/device authentication. Supply the validated evidence and
locally feasible action identifiers to DeepSeek Flash. Validate its typed
reply against that same set. Use one action per tap. The client resolves
the current endpoint or registered source, applies through the normal
platform lifecycle, checks that the intended VPN is ready and that traffic
works, then commits or restores the previous settings. Guard this with
the submitted selection and lifecycle revision so a late response cannot
override another user action. Keep manual pins exact. Use local diagnosis
when the API is unreachable, and surface Android's existing consent when
the OS requires it.

The first scenario distinguishes DNSTT readiness timeout after TUN setup
from failures before setup. Configuration code defects such as an MTU
override require a separately verified runtime fix.

## Verification

Check conversation-free evidence on success, failure and a superseded
attempt. Exercise typed responses, account authorization, payload bounds
and unsupported actions through vpnctl. On Android verify connected and
failed repairs, rollback, manual pin preservation, offline fallback and
VPN permission handling. Run exact-head Android compile and shared tests,
then one device or emulator trace before enabling the feature. Windows
uses the existing assistant lifecycle fixtures and a focused repair test.
Keep the feature behind the assistant gateway for rollback.

## First implementation block

Write the native DNSTT readiness summary with the published tunnel state
and include its bounded, validated fields in the Android diagnostics
export. Clear stale readiness when a new attempt starts. The diagnostic
report is useful on its own and can land before the assistant UI or repair
endpoint. Input contract tests reject unknown fields or attached text.
