# Resolve assistant applications on the device

The Windows preview refused a generic browser request and kept the prior
56-app routing list. Model display names and real executable names were
treated as the same field. A later request naming Edge reached candidate
verification and failed proxy health separately.

Resolve the one app named by the original request locally. Recognize common
Windows product names and local metadata for other running desktop apps,
preserve exact process spelling, and require a verified
executable or Windows browser registration. A generic browser request can use
the agreed Windows HTTP and HTTPS default if locally verified. Ambiguous,
conflicting or unresolvable targets should ask for an explicit name. Keep the
existing model request schema and route verification. The initial live replay
showed that the existing prompt often clarifies a default-browser request and
can offer a whole-tunnel toggle. Advertise the two process-bound press actions
when the local target resolves. Scoped application routing exposes neither
the whole-tunnel toggle nor arbitrary UI controls. Do not send process
inventory, executable paths or registry contents. Revert the task commits to
roll back the resolver.

Reuse the desktop repair branch. Verify the unchanged route policy, pure local
resolver, supported names, default browser, ambiguity and cross-platform
explicit IDs with an independently authored corpus. Use the exact hosted
Linux and Windows suites. Document real examples and what the tests measure.
The release and live protected-route checks are separate from this mapping work.

## Outcome

The exact product revision `2697557f` passed all six jobs in GitHub Actions
run `45190069764`. The Linux and Windows suites passed 4,284 and 4,497 tests
respectively. Each executed all 337 Astra target cases, seven local-Metadata
cases and five scope cases. The ViewModel fixture rejected a mismatched
proposal without saving settings or starting the tunnel.

A WINBRAT replay sent 337 synthetic requests to DeepSeek Flash with the
existing gateway prompt and actions. In the 119 cases with an unambiguous
expected target and a requested action, 85 replies named the correct target.
The model proposed an app route in 60 of the 122 requests where the target
was absent, unsupported or ambiguous. None of the replies was executed.

The same 15 default-browser requests produced zero usable routes under the
original action inventory. A second replay using the bound controls and the
gateway's actual fallback prompt produced 15 matching route choices. A
separate request phrased like the original browser complaint also matched.
These are model decisions against synthetic state, not live VPN traffic
tests. The Windows owner's HTTP and HTTPS registration was read separately
and resolved to the existing `msedge.exe`. WINBRAT lease 99 was released.

The desktop still accepts one user submission at a time. Clarify ends the
request. A local target chooser is described in
[the resolver design](../docs/assistant-app-target-resolution.md).
No new chooser, production rollout or installed desktop update was made.
