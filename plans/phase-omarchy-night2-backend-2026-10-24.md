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
