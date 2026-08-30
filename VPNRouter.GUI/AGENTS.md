# VPNRouter.GUI zone guidelines

`VPNRouter.GUI` is the standalone Go repair and trampoline zone for Windows desktop bootstrapping and repair. It is a Go project (not C# / .NET).

## Scope and responsibilities

- Backwards-compatibility launcher stub (`VPNRouter.GUI.exe`) for older shortcuts and legacy auto-updater integration.
- Post-update bootstrap host that completes replacement of locked binaries.
- Self-repair trampoline for launching recovery procedures.
- DLL product-version/commit integrity validation before trusting repaired payloads (`integrity.go`).
- Repair-loop suppression through bounded marker state (`marker.go`).

## Safety and guidelines

- Written in Go to avoid mapping .NET runtime DLLs so executable files can be safely replaced.
- Primary binary target is `VPNRouter.GUI.exe`.
- Follow canonical safety and git guidelines in [`docs/agent-contract.md`](../docs/agent-contract.md).

## Zone checks

```bash
cd VPNRouter.GUI && go test ./...
```
