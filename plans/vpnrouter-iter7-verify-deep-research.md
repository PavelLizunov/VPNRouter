# Iter#7 — Verification deep research + architecture proposal

**Trigger**: user feedback 2026-05-04 with screenshot of Servers tab showing
two real servers:
- `is-01-grpc-test (grpc + reality)` — TLS ✗ (red)
- `is-01-hy2-test (hysteria2 + salamander)` — selected

User's questions:
1. «А разве нельзя делать проверку даже включенным VPN и получать корректные результаты?»
2. «Также есть ли у проверки логи?»
3. «Как проверка работает на не-vless конфиги?» — user reports false negatives:
   they connect fine to these servers but our check says "not working".
4. «Нужен глубокий research, review кода и computer-use».

## Findings — current state of verification

### Bug #1 (P0): TcpTlsProbe assumes VLESS + Reality + TCP + TLS

`VPNRouter.Core/Services/TcpTlsProbe.cs` — used by Servers/Subscribe
"Test all" + Free Configs bulk test:

```csharp
public static async Task<ServerProbeResult> ProbeAsync(
    string host, int port, string? sni,
    bool requireTls = true, ...)
{
    // ── Stage 1: TCP (2 attempts) ──   ← TcpClient, AddressFamily.InterNetwork
    // ── Stage 2: TLS handshake ──     ← TLS 1.2/1.3 + cert chain + SNI match
}
```

The probe does **TCP connect** then **standard TLS handshake** with cert
chain validation + SNI-to-cert-name match. This is correct **only** for
VLESS+Reality+TCP servers where the host genuinely speaks TLS on that port
(Reality proxies to a real domain like `yahoo.com`, presents the real cert,
TLS handshake passes; only the post-handshake VLESS framing is custom).

**Where it fails**:

| Protocol | Why TcpTlsProbe is wrong |
|---|---|
| VLESS+gRPC+Reality | After TLS, server expects HTTP/2 GRPC frame; raw probe stops at handshake → may pass OR fail depending on SNI quirks. User's `is-01-grpc-test` shows **TLS ✗** because of this. |
| VLESS+WebSocket | After TLS, server expects HTTP/1.1 Upgrade frame; probe stops at handshake. Test result depends on whether the gateway answers TLS for non-WS clients. |
| **Hysteria2** | Pure UDP+QUIC. `TcpClient.ConnectAsync` cannot connect to UDP-only port → returns `Unreachable`. **100% false negative**. User's `is-01-hy2-test` falls into this. |
| **TUIC** | Pure UDP+QUIC. Same as Hysteria2 — TCP connect fails. **100% false negative**. |
| Shadowsocks | Encrypted from byte 0, no TLS handshake. Probe TLS stage fails → `TlsFailed`. False negative. |

### Bug #2 (P0): VlessDeepVerifier hardcodes outbound type=vless

`VPNRouter.Core/Services/VlessDeepVerifier.cs`:

```csharp
private static string BuildSingleOutboundConfig(VlessServerEntry s, ...)
{
    var outbound = new JsonObject
    {
        ["type"] = "vless",      // ← hard-coded
        ["uuid"] = s.Uuid,
        ["flow"] = ...,
        ...
    };
}
```

**Where it fails**:

- **Hysteria2**: spawned sing-box config has `type=vless` but the server
  speaks Hysteria2; sing-box's outbound rejects the connection → **deep
  verify fails too** even though the server works in production.
- **TUIC**: same.
- **Shadowsocks**: same.

So both quick test AND deep verify give false negatives for non-VLESS
protocols. User's `is-01-hy2-test` would fail BOTH ways.

### Bug #3 (P0): Verification has zero logs

Confirmed across all log files in `%ProgramData%\VPNRouter\logs\` —
ZERO log entries from `TcpTlsProbe`, `FreeConfigTester`, `VlessDeepVerifier`,
or `FreeConfigDeepVerifier`. Reasons:

- `TcpTlsProbe` is a `static class` with no logger field. No log calls
  whatsoever inside its 333 lines.
- `FreeConfigTester` has no logger (no `_logger` field).
- `VlessDeepVerifier` has `_logger` but only logs `"sing-box not found"`
  on the missing-binary branch. Per-server probe outcomes are not logged.
- `FreeConfigDeepVerifier` similar.

**Result**: when a test fails, the user sees only a red icon. No
diagnostic trail in the log file. Cannot tell if it was a TCP failure,
TLS failure, cert mismatch, SNI typo, network timeout, or genuine
server outage. **User has no path to debug failures.**

### Bug #4 (UX): Quick test fails when active VPN intercepts

Already addressed in r15 with the yellow warning banner — the TCP probe
goes through the active TUN if VPN is running, returning sub-5 ms
(Implausible). r15 surfaces "VPN is intercepting — use Deep verify".

But the user's question — "can we just test correctly through the VPN?"
— deserves a deeper answer:

**Quick test through VPN** = no, fundamentally can't. The TUN by design
captures the test's TCP traffic before it leaves the machine. Workarounds
all require routing the test traffic outside the active TUN, which is
either OS-level (full-tunnel makes this impossible) or requires marking
the test process as VPN-excluded (complex).

**Deep verify through VPN** = yes, *should* work because:
- We spawn a separate `sing-box.exe` subprocess with its own outbound.
- That subprocess's TCP traffic to the candidate server goes through
  the OS routing stack.
- In **split-tunnel** mode, only matching processes go through the
  active VPN's TUN; the test sing-box subprocess isn't in the
  process_name list, so its traffic goes direct → reaches the candidate
  server, gets a real response → deep verify reports correct status.
- In **full-tunnel** mode, the test sing-box's traffic ALSO goes through
  the active TUN, so it's wrapped by the active VPN before reaching the
  candidate. The candidate server receives traffic from the active
  VPN's exit IP (e.g. `194.87.222.111`), responds normally, but the
  end-to-end path is "host → active VPN exit IP → candidate VPN exit
  IP → test target". Latency is artificially inflated but **the test
  CAN succeed and return a real result**, just with the active VPN's
  RTT added to the candidate's RTT.

So yes — deep verify through VPN works (with caveats). The user's
intuition is correct.

### Bug #5 (UX, secondary): "Pinged 7/7" still misleading

r15 fixes the cross-tab leak. The "Pinged: N/M" count includes
Implausible (sub-5 ms) entries which are NOT actually pinged but
intercepted by TUN. The count semantically means "TCP responded N
times" but reads as "N servers pinged successfully" to a user. The r15
warning helps but doesn't eliminate the ambiguity.

## Architecture proposal

### Phase 1 (P0, must-ship, ~150 LOC) — Per-protocol quick test

Make `TcpTlsProbe.ProbeAsync` protocol-aware. Add new method
`ProbeServerAsync(VlessServerEntry)` that branches by `entry.Protocol`:

```csharp
public static async Task<ServerProbeResult> ProbeServerAsync(VlessServerEntry s, ...)
{
    var protocol = (s.Protocol ?? "vless").ToLowerInvariant();
    return protocol switch
    {
        "vless" when IsRealityOrTls(s) => await ProbeAsync(s.Server, s.Port, sni, requireTls: true),
        "vless"                        => await ProbeAsync(s.Server, s.Port, null, requireTls: false),
        "shadowsocks" or "ss"          => await ProbeTcpOnlyAsync(s.Server, s.Port),
        "hysteria2"                    => await ProbeUdpAsync(s.Server, s.Port),
        "tuic"                         => await ProbeUdpAsync(s.Server, s.Port),
        _                              => new ServerProbeResult(SkippedNotApplicable, ...),
    };
}
```

**New status**: `ServerProbeStatus.SkippedNotApplicable` — "quick test
not meaningful for this protocol; use Deep verify". UI shows neutral
"—" or "skipped" badge instead of red "TLS ✗".

**UDP probe**: `UdpClient.SendAsync(empty datagram)` + `ReceiveAsync`
with timeout. Even if the server doesn't echo, the absence of an ICMP
"port unreachable" within ~2s is a positive signal that the port is
bound. Not perfect, but eliminates the false negative for hy2/tuic.

**Result for user's screenshot**: `is-01-hy2-test` would show "—"
(skipped, use Deep verify) instead of red "×". `is-01-grpc-test` would
get a refined gRPC-aware test (or also fall back to skipped).

### Phase 2 (P1, should-ship, ~300 LOC) — Multi-protocol deep verify

Extend `VlessDeepVerifier` (or rename to `ServerDeepVerifier`) to build
the spawned sing-box outbound based on `entry.Protocol`:

```csharp
private static JsonObject BuildOutbound(VlessServerEntry s)
{
    return s.Protocol?.ToLowerInvariant() switch
    {
        "vless"       => BuildVlessOutbound(s),       // existing
        "hysteria2"   => BuildHysteria2Outbound(s),   // NEW
        "tuic"        => BuildTuicOutbound(s),        // NEW
        "shadowsocks" => BuildShadowsocksOutbound(s), // NEW
        _             => BuildVlessOutbound(s),       // fallback
    };
}
```

`VlessServerEntry` already carries protocol-specific fields
(`ObfsType`, `CongestionControl`, `Method`, `Plugin`) per
`ServerViewModel.HostSubtitle`'s switch. They're persisted but the
deep-verify spawn path ignores them.

This gives the user a working deep-verify even for hy2/tuic/ss
servers — same as the Public page experience for free configs of
those protocols (which works because `FreeConfigDeepVerifier` IS
multi-protocol — different code path).

### Phase 3 (P1, should-ship, ~50 LOC) — Verification logging

Add per-probe log entries to `TcpTlsProbe`:

```csharp
public static class TcpTlsProbe
{
    public static ILogger? Logger { get; set; }  // injected from VM

    public static async Task<ServerProbeResult> ProbeAsync(...)
    {
        Logger?.Debug("[TcpTlsProbe] Probing {Host}:{Port} (sni={Sni}, requireTls={Tls})", host, port, sni, requireTls);
        ...
        if (result.Status == TlsFailed)
            Logger?.Information("[TcpTlsProbe] {Host}:{Port} TLS failed: {Err}", host, port, result.Error);
        ...
    }
}
```

Same for `VlessDeepVerifier.VerifyAsync` — log spawn config path,
sing-box stderr snippet on failure, HTTP probe outcome.

User can then `tail -f vpnrouter*.log` while running Test all to see
exactly why each server fails. No UI change required (logs already
exist; we just populate them).

### Phase 4 (P2, polish) — Active-VPN-aware testing

Current Active-VPN warning is purely advisory. Future iteration could:

1. **Auto-fallback** to Deep verify when Quick test detects >50%
   Implausible — instead of just warning, run the deep verify.
2. **Mark sing-box subprocess as VPN-excluded** (Windows split-tunnel)
   so quick test traffic bypasses TUN. Requires firewall rule + ETW
   process monitor coordination.
3. **"Test through VPN" toggle** — explicit option for the user to
   accept the longer latency in exchange for not having to disconnect.

## Recommendation for r16

**Ship Phase 1 + Phase 3** (per-protocol quick test + logging) in r16.

- Phase 1 directly fixes the user's reported bug (false negatives on
  non-VLESS protocols).
- Phase 3 gives the user the diagnostic trail they asked about
  («есть ли у проверки логи?»).
- Both are localised changes with clear test boundaries.

**Defer Phase 2** to r17 — multi-protocol deep verify needs careful
testing per protocol (hy2 obfs salamander config quirks, tuic
congestion algorithms, ss plugin chain). Can't do it well in a single
r-cycle.

**Defer Phase 4** to a future iteration — process-VPN-exclusion is a
new feature class.

## Cross-references

- Iter#6 audit: `plans/vpnrouter-iter6-verify-audit.md` (status leak,
  active-VPN warning, FreeConfigTester dedup)
- TcpTlsProbe: `VPNRouter.Core/Services/TcpTlsProbe.cs`
- VlessDeepVerifier: `VPNRouter.Core/Services/VlessDeepVerifier.cs`
- FreeConfigDeepVerifier: `VPNRouter.Core/Services/FreeConfigs/FreeConfigDeepVerifier.cs`
  — multi-protocol-aware (ALREADY supports hy2/tuic/ss); use as
  reference for Phase 2.
- ServerTesting wire-up: `VPNRouter.App/ViewModels/MainWindowViewModel.ServerTesting.cs`
- Server entry model: `VPNRouter.Core/Models/AppSettings.cs:VlessServerEntry`
  (carries Protocol, ObfsType, CongestionControl, Method, Plugin fields)
