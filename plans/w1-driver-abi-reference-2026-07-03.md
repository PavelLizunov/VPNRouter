# W1 — Mullvad split-tunnel driver ABI reference (pinned)

Extracted 2026-07-03 from the driver's own public headers + the Rust reference agent.
This is the **spec W1.1 (`SplitTunnelDriverManager.cs`) ports to C#** + closes research
open-q #3 (pin exact commits to the bundled `.sys`). Goal:
[`goal-w1-mullvad-split-driver-2026-07-03.md`](goal-w1-mullvad-split-driver-2026-07-03.md).

## Pins (record; re-verify on any driver bump)

| Artifact | Repo | Commit | Notes |
|---|---|---|---|
| `.sys`/`.cat`/`.inf` | `mullvad/mullvadvpn-app-binaries` | `cc0affb2f06e870fb594e2dd6d61049611991586` | `x86_64-pc-windows-msvc/split-tunnel/` |
| ABI headers (`src/defs/*.h`) | `mullvad/win-split-tunnel` | `0a0eb97f67d1dbcb3d08bda66d3b24f465d95475` | source of truth |
| Reference agent (`driver.rs`) | `mullvad/mullvadvpn-app` | `15fca6c856b6609b57fda8dfbcd5b3ffbcb01e25` | `talpid-core/src/split_tunnel/windows/` |

**File sha256 (the bundle pin for W1.4 `build.ps1`):**
- `mullvad-split-tunnel.sys`  `10cf25bbcfe51fd663a1fec88a98e9b858f3a579589bb2ec496b66e4fdd1b201`  (98400 b)
- `mullvad-split-tunnel.cat`  `c599926a0327d7ae06b534f4cd039db30392e1897bb9d03e4fec3631744a4e6d`  (12350 b)
- `mullvad-split-tunnel.inf`  `3dd5905e5fb98d61a942a33e8c9a5ba07c3a2de1e4f319e1fec3e54df6591608`  (1796 b)

**Signatures (verified locally `Get-AuthenticodeSignature`, 2026-07-03):** `.sys` Valid,
`CN=Mullvad VPN AB` (DigiCert, to 2027-02-07); `.cat` Valid, `CN=Microsoft Windows Hardware
Compatibility Publisher` (attestation → loads on prod Win10/11 x64, Secure Boot, no test-signing).

## Device / service

- Symbolic device: `\\.\MULLVADSPLITTUNNEL` (open R/W, **share_mode=0 exclusive**; agent uses
  `FILE_FLAG_OVERLAPPED` — needed only for the DEQUEUE_EVENT inverted-call, not for the
  one-shot control IOCTLs).
- Kernel service: `sc create mullvad-split-tunnel type= kernel binPath= <sys>` (or `CreateService`
  with `SERVICE_KERNEL_DRIVER`), then `StartService`.

## IOCTL codes  `CTL_CODE = (0x8000<<16)|(access<<14)|(func<<2)|method`, access=0

| IOCTL | func | method | value | in / out |
|---|---|---|---|---|
| INITIALIZE | 1 | BUFFERED(0) | `0x80000004` | in: `ST_SUBLAYER_GUIDS` (32b) |
| DEQUEUE_EVENT | 2 | BUFFERED | `0x80000008` | out: `ST_EVENT_HEADER`+payload (inverted-call) |
| REGISTER_PROCESSES | 3 | BUFFERED | `0x8000000C` | in: process-registry buffer |
| REGISTER_IP_ADDRESSES | 4 | BUFFERED | `0x80000010` | in: `SplitTunnelAddresses` (40b) |
| GET_IP_ADDRESSES | 5 | BUFFERED | `0x80000014` | out |
| SET_CONFIGURATION | 6 | BUFFERED | `0x80000018` | in: configuration buffer |
| GET_CONFIGURATION | 7 | BUFFERED | `0x8000001C` | out |
| CLEAR_CONFIGURATION | 8 | NEITHER(3) | `0x80000023` | none |
| GET_STATE | 9 | BUFFERED | `0x80000024` | out: `u64` state (8b) |
| QUERY_PROCESS | 10 | BUFFERED | `0x80000028` | in/out |
| RESET | 11 | NEITHER(3) | `0x8000002F` | none (before unload) |

## State machine  (`GET_STATE` returns `u64`)

`NONE=0 → STARTED=1 → INITIALIZED=2 → READY=3 → ENGAGED=4` (`ZOMBIE/TERMINATING=5`).
Engage flow (agent `reinitialize` + engage):
1. `GET_STATE`; if `!= STARTED` → `RESET`.
2. `INITIALIZE`(sublayer GUIDs) → INITIALIZED.
3. `REGISTER_PROCESSES`(full snapshot) → READY.
4. `REGISTER_IP_ADDRESSES`(tunnel + internet v4/v6) **and** `SET_CONFIGURATION`(excluded NT paths)
   → **ENGAGED** (both required; order between them free).
- Split happens in-kernel for matching images (config) among registered + auto-tracked-arriving
  processes. Consuming DEQUEUE_EVENT is FYI only — not required for splitting (spike skips it).

## Sublayer GUIDs (INITIALIZE input — exact, match `winfw/mullvadguids.cpp`)

- Baseline: `{21E068A2-2851-43C5-8A29-7AFE3F260384}`
- Dns:      `{E65841B6-82F6-4D55-BDE2-61F84D4508D4}`

`ST_SUBLAYER_GUIDS { GUID Baseline; GUID Dns; }` — 2×16 = **32 bytes**.

## Struct layouts (x64: SIZE_T/HANDLE=8, u16=2, GUID=16, IN_ADDR=4, IN6_ADDR=16)

**SplitTunnelAddresses** (REGISTER_IP_ADDRESSES, **40 b**, note the order):
`tunnel_ipv4 IN_ADDR@0 · internet_ipv4 IN_ADDR@4 · tunnel_ipv6 IN6_ADDR@8 · internet_ipv6 IN6_ADDR@24`.
`internet_ipv4` = physical NIC IP (excluded sockets bind here); `tunnel_ipv4` = wintun/TUN IP.
Zero unused v6.

**Configuration buffer** (SET_CONFIGURATION) = header + N entries + wide-string blob:
- `ST_CONFIGURATION_HEADER { SIZE_T NumEntries@0; SIZE_T TotalLength@8; }` (16 b).
- N × `ST_CONFIGURATION_ENTRY { SIZE_T ImageNameOffset@0; USHORT ImageNameLength@8; }` (**16 b**,
  6 pad). `ImageNameOffset` = byte offset **within the string region** (region starts after
  header+entries); `ImageNameLength` = byte length of the **non-null-terminated** UTF-16 string.
- string blob: excluded NT device paths concatenated (no NULs).

**Process-registry buffer** (REGISTER_PROCESSES) = header + N entries + wide-string blob:
- `ST_PROCESS_DISCOVERY_HEADER { SIZE_T NumEntries@0; SIZE_T TotalLength@8; }` (16 b).
- N × `ST_PROCESS_DISCOVERY_ENTRY { HANDLE ProcessId@0; HANDLE ParentProcessId@8;
  SIZE_T ImageNameOffset@16; USHORT ImageNameLength@24; }` (**32 b**, 6 pad). Same
  string-region-relative offset semantics.
- string blob: each process's **NT device path** (UTF-16, no NUL). Empty path → offset/len 0.
- pid-recycle guard: if a mapped parent's creation-time is *newer* than the child, set parent=0.

**DEQUEUE_EVENT** out (W1.1, not spike): `ST_EVENT_HEADER { ST_EVENT_ID EventId(u32)@0;
SIZE_T EventSize@8; UCHAR EventData[] }`; ids `START/STOP_SPLITTING_PROCESS=0/1`,
`ERROR_*=0x80000001..`; `ST_SPLITTING_EVENT { HANDLE Pid; u32 Reason; USHORT ImageNameLen; WCHAR[] }`
(Reason bitflags: INHERITANCE=1 CONFIG=2 ARRIVING=4 DEPARTING=8).

## Device-path resolution (both must yield matching `\Device\HarddiskVolumeN\...`)

- **process image path** → `QueryFullProcessImageName(hProc, PROCESS_NAME_NATIVE=1, ...)` (NT form).
  Open each pid `PROCESS_QUERY_LIMITED_INFORMATION`; skip System/Idle/csrss (access-denied/invalid).
- **config app DOS path** → split drive `C:` → `QueryDosDevice("C:")` = `\Device\HarddiskVolumeN`
  → prepend to the remainder. (Reference `get_device_path`.)

## Spike (W1.0) minimal path
install service → open device → GET_STATE/RESET → INITIALIZE → REGISTER_PROCESSES →
REGISTER_IP_ADDRESSES(NIC+TUN) → SET_CONFIGURATION(one excluded, e.g. `curl.exe` NT path) →
GET_STATE==ENGAGED. Verify: excluded `curl api.ipify.org` shows the **NIC WAN IP** (not VPN exit)
while sing-box full-tunnel runs; `kill sing-box.exe` → excluded curl still egresses (unaffected).
Skip DEQUEUE_EVENT. Teardown: RESET → stop → delete service.
