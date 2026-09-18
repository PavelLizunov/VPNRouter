# VPNRouter.Headless - Zone Agent Instructions

This document governs the `VPNRouter.Headless` zone. Before any repository action, read `docs/agent-contract.md`, `plans/phase-omarchy-plugin-2026-09-17.md`, and `plans/omarchy-protocol-v1.md`.

## Purpose and Architecture

`VPNRouter.Headless` provides the foreground helper daemon and NDJSON protocol adapter for the Omarchy Quattro shell plugin (`omarchy-vpnrouter`). It connects the plugin QML frontend to `VPNRouter.Core` over standard input and output (`--stdio`), eliminating any dependency on Avalonia or GUI frameworks.

## Module Structure

- `Program.cs`: CLI entry point parsing `--stdio` and `--data-dir`, handling POSIX termination signals and console cancellation, and managing engine lifecycle.
- `RouterException.cs`: Sanitized domain and protocol exception with safe, non-leaking error codes and descriptions.
- `Protocol/`:
  - `ProtocolConstants.cs`: Protocol limits (v: 1, 256 KiB frame limit, depth 32, max output queue 8, ID regex, 100 row pagination, known methods).
  - `ProtocolRequest.cs`: Validated request representation (`v`, `id`, `method`, `params`).
  - `ProtocolFrame.cs`: Wire frames (`ProtocolResponseFrame`, `ProtocolStateEventFrame`, `ProtocolProgressEventFrame`) with bounded serialization.
  - `ProtocolParser.cs`: Strict UTF-8 JSON reader with depth checking (<= 32), duplicate key detection at all levels, and root schema enforcement.
  - `ProtocolLineReader.cs`: Bounded line reader enforcing the 256 KiB limit before line accumulation.
  - `ProtocolOutputQueue.cs`: Bounded 8-frame output queue with state event coalescing, bounded response backpressure, and a three-second write/flush stall deadline; no delivery guarantee to a dead peer.
  - `ProtocolDispatcher.cs`: Concurrency manager enforcing at most one ordinary operation, with urgent non-blocking `cancel` and `disconnect` operations.
  - `IProtocolHandler.cs`: Transport-independent abstraction for dispatching commands and receiving backend events.
  - `RouterBackendHandler.cs`: Adapter bridging `RouterBackend` to `IProtocolHandler`.
  - `ProtocolServer.cs`: Central server coordinating the line reader, parser, dispatcher, output queue, and graceful EOF/SIGTERM teardown.

## Critical Invariants and Security Rules

1. **No Secret Leaks**: Errors, log lines, and responses must never echo raw input, exception messages, inner exceptions, stack traces, subscription URLs, or credentials.
2. **Strict Framing and Bounds**: Every frame must have `v: 1`. Frame size is capped at 256 KiB BEFORE accumulation. Maximum JSON depth is 32. Duplicate keys at any nesting level fail closed.
3. **Dedicated NDJSON Stdout**: No arbitrary log lines, banners, or ANSI escape codes on stdout. Stdout is reserved exclusively for valid newline-delimited protocol JSON frames.
4. **Concurrency and Backpressure**: At most one ordinary operation runs at a time; subsequent ordinary operations immediately return error code `busy`. `cancel` and `disconnect` are urgent controls. Output queue is bounded to 8 frames and transport dispatch/response tasks to 5. Temporary backpressure pauses admission; stdout broken pipe or a three-second write/flush stall triggers bounded teardown.
5. **No Network in Constructor**: Session or backend constructors must never initiate network calls or spin up engines.
6. **No Emoji**: Adhere to project guidelines against emoji in code, configuration, or documentation.
