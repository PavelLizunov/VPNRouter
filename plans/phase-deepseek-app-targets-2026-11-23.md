# Resolve assistant applications on the device

The Windows preview refused a generic browser request and kept the prior
56-app routing list. Model display names and real executable names were
treated as the same field. A later request naming Edge reached candidate
verification and failed proxy health separately.

Resolve the one app named by the original request locally. Recognize common
Windows product names, preserve exact process spelling, and require a verified
executable or Windows browser registration. A generic browser request can use
the agreed Windows HTTP and HTTPS default if locally verified. Ambiguous,
conflicting or unresolvable targets should ask for an explicit name. Keep the
existing model request schema and route verification. Do not send process
inventory, executable paths or registry contents. Revert the task commits to
roll back the resolver.

Reuse the desktop repair branch. Verify the unchanged route policy, pure local
resolver, supported names, default browser, ambiguity and cross-platform
explicit IDs with an independently authored corpus. Use the exact hosted
Linux and Windows suites. Document real examples and what the tests measure.
The release and live protected-route checks are separate from this mapping work.

## Outcome

Pending.
