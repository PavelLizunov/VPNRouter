# Business activity recovery and header rendering

- Why: two live nodes have stale management health/traffic; the Business header downsamples its mascot without interpolation.
- Scope: diagnose and repair collection, distinguish stale activity in RU/EN, and reuse Standard UI high-quality bitmap rendering.
- Invariants: preserve pinned SSH verification, grants, node identity, user selection, routing, tunnel state and private configuration.
- Evidence: exact controller59477de; direct management SSH to two nodes times out while their VPN services remain active. Audit shows direct-to-SE management routing at19:18:53UTC and SE-to-NL at19:34:46UTC; collection resumes at19:19 and all four nodes are fresh. The current diagnostic process performed no production mutation. The underlying direct-path outage is not explained by this evidence.
- Risk/rollback: small presentation change; revert this commit for UI. Any management-route repair must preserve and verify the original policy and client detours.
- Verification: exact-SHA CI build/full tests and existing Business headless captures; live read-only metadata for four nodes, without publishing endpoints or raw logs.
- Gates: exact30176a85 has11SUCCESS/2expectedSKIPPED checks; build/full tests/Business captures pass in CI; docs recorded; independent source diagnoses completed; no routing/VM public API changes; no live UI acceptance claimed.
- Outcome: read-only live API verifies SE high, CH medium, IS/NL low at19:35UTC. UI source fixes have a completed independent correctness review without findings; exact30176a85 build/tests/Business captures pass. No new live UI/installer acceptance is claimed. The issued private installer is unchanged until a new exact-source build is verified.
