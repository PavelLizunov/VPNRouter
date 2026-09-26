# System Contracts, State Machines & Critical Data Flows

Authoritative reference for lifecycle state transitions, concurrency primitives, data flow pipelines, and security invariants.

---

## 1. Core Lifecycle State Machine (`VpnEngine` & `SingBoxManager`)

```
   ┌──────────────────────────────────────────────────────────────┐
   │                                                              │
   ▼                                                              │
[Stopped] ─── StartAsync() ───► [Starting]                        │ StopAsync()
   ▲                               │ (Process launch & TUN init)  │ or Error
   │                               ▼                              │
   │                          [Warmup]                            │
   │                               │ (DNS & Connectivity probes)  │
   │                               ▼                              │
   └───────────────────────── [Connected] ────────────────────────┘
                                   │
                                   │ Network glitch / probe fail
                                   ▼
                            [Reconnecting] ──► [AutoFailover]
```

### Transition Specifications:
- **`Stopped` → `Starting`**:
  - *Trigger*: `VpnEngine.StartAsync()`.
  - *Guards*: Single instance lock (`TunOwnershipLock` / `LinuxTunOwnership`), valid config generation.
  - *Side-effects*: Increments `_engineGeneration`, starts `sing-box` child process, initializes ETW / split-tunneling.
- **`Starting` → `Warmup`**:
  - *Trigger*: Sing-box reports startup readiness (stdout/log scan or API handshake).
  - *Side-effects*: Dispatches HTTP warmup probes to Google/Cloudflare/Yandex.
- **`Warmup` → `Connected`**:
  - *Trigger*: Warmup probe succeeds OR probe deadline expires with basic connectivity verified.
  - *Side-effects*: Activates DNS lockdown (`EnableDnsLockdownAsync`), fires `StateChanged(Connected)`.
- **`Connected` → `Reconnecting` / `Failover`**:
  - *Trigger*: Health monitor detects consecutive failed probes.
  - *Side-effects*: Selects next candidate server from pool, performs seamless hot-reload or fast process restart.

---

## 2. Headless Protocol v1 Request Processing Flow

```
[STDIN fd 0]
      │
      ▼
[ProtocolLineReader] (Max 256 KiB, unbuffered chunking, trims \r\n)
      │
      ▼
[ProtocolParser] (Depth <= 32, no duplicate keys, v=1, strict ID)
      │
      ▼
[ProtocolServer Admission Gate] (In-flight cap <= 5)
      │
      ▼
[ProtocolDispatcher]
      ├─ "cancel"      ──► Preempts active CTS, returns {"cancelled": true/false}
      ├─ "disconnect"  ──► Cancels active ordinary op, executes session.DisconnectAsync()
      └─ Ordinary      ──► If busy: returns error "busy"
                           If free: claims slot, executes handler via Task.Run()
                                    └─ Catch: converts exception to safe ProtocolError
                                       │
                                       ▼
                             [ProtocolOutputQueue]
                                       │ (Max 8 frames, coalesces state events)
                                       │ (3.0s write/flush stall timeout)
                                       ▼
                                 [STDOUT fd 1]
```

---

## 3. Concurrency & Locking Primitives

| Lock Primitive | Scope | Location | Purpose & Guarantees |
|---|---|---|---|
| `_gate` (`SemaphoreSlim(1,1)`) | Engine Lifecycle | `VpnEngine.cs`, `RouterSession.cs` | Synchronizes `StartAsync`, `StopAsync`, `ApplyAsync` against overlapping invocations. |
| `_stateLock` (`object`) | State Invariants | `RouterSession.cs`, `SingBoxManager.Lifecycle.cs` | Protects atomic reads/writes to connection state enum and generation IDs. |
| `LinuxTunOwnership` (`flock`) | Host Process Mutex | `LinuxTunOwnership.cs` | Kernel-enforced advisory lock on `/run/user/<uid>/vpnrouter-tun.lock` (replaces Windows named semaphore). |
| `_configLock` (`object`) | Config Generation | `ConfigGenerator.cs` | Ensures thread-safe JSON generation and outbound list building. |
| `_sync` (`object`) | Protocol Dispatcher | `ProtocolDispatcher.cs` | Guards active operation ID and urgent operation count. |
| `_sync` (`object`) | Output Queue | `ProtocolOutputQueue.cs` | Guards 8-frame output list and state event coalescing. |

---

## 4. Security & Privilege Boundaries

1. **Ambient Runtime Policy (`SingBoxRuntimePolicy`)**:
   - In Linux Headless, production execution defaults to `DefaultProduction` (untrusted).
   - Only explicitly authorized, SHA-256 verified, owner-private binaries may be executed.
   - Constructors in `RouterBackend` and `RouterSession` do NOT perform network I/O or start engines.
2. **Private File Permissions (`0700` / `0600`)**:
   - `AppPaths.cs` enforces `0700` on data/config directories on POSIX systems (`chmod 0700`).
   - Private text files (settings, secrets) use `0600` creation masks to prevent other local users from inspecting configurations.
3. **Zero-Secret-Leak Logging & Exceptions**:
   - `RouterException.GetSafeMessage()` maps all error codes to static, non-identifying English messages.
   - Raw exceptions, stack traces, host file paths, proxy passwords, and URLs must NEVER be written to stdout NDJSON frames.
