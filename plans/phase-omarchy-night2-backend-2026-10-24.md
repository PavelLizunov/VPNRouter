# Night 2: chained-server verification

Confirmed source defect at 75ac570c: ServerFeature.VerifyAsync passes a
DetourVia target into BuildSingleOutboundConfig, whose output has only one proxy
outbound and no upstream detour. A successful direct probe does not verify the
configured chain. The verifier API receives no server pool to resolve it.

Small correction: reject chained targets with the existing UnsupportedByVerifier
result before invoking the verifier, with no core/network/VPN lifecycle action.
A hermetic Headless feature check pins the wire-facing result and absence of
verification progress. No package install or deployment. Tests run in GitHub CI;
no local SDK build or backend execution. Broader chain support remains deferred.

Second confirmed defect: free.test on a cached Verified entry ignores failed
TcpPingOnlyAsync and returns old latency as a fresh success. Return the actual
TCP result and clear the measurement on failure without demoting historical
verification. A negative-port fixture fails before network I/O and asserts the
wire result. Non-verified failed probes also return null latency rather than
TLS-failure connect time. First commit 88b3282e passed all six test.yml jobs.
