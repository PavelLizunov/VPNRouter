# VPNRouter.Tools zone guidelines

`VPNRouter.Tools` contains supplementary developer and automation tooling, including `PoolAggregator`, `UiProbe` and `UiMcp`.

## Scope and responsibilities

- `PoolAggregator`: C# tool invoked by GitHub Actions to aggregate, parse, deduplicate, and enrich public VLESS configurations into `pool.json`.
- `UiProbe`: library that renders desktop pages and windows headlessly (Avalonia.Headless + Skia) in chosen scenarios, themes, languages and sizes, lints the layout, dumps the control tree and clicks through controls with real pointer input at a chosen speed. Reused by `VPNRouter.Tests/UiProbeHeadlessTests.cs`.
- `UiMcp`: stdio MCP server (JSON-RPC, no protocol package) exposing `UiProbe` as tools (`ui_catalog`, `ui_render`, `ui_matrix`, `ui_tree`, `ui_sweep`, `ui_state_properties`). `.github/workflows/build-ui-mcp.yml` publishes a self-contained Linux build and runs a smoke session; `tools/ui-mcp/mcpcall.py` calls it from a shell. Plan: `plans/phase-m1-ui-mcp-2026-10-02.md`.
- The sweeper must not act on the machine: it skips controls whose text or command suggests install, start, stop, delete, update and similar, plain code-behind buttons, and everything except navigation on a Windows host, unless the caller opts in. Keep it that way.
- Tools here complement core and release automation without embedding runtime VPN logic.

## Safety and guidelines

- Follow canonical repository contract in [`docs/agent-contract.md`](../docs/agent-contract.md).
- Changes to pool aggregation must preserve downstream schema expectations for `pool.json`.

## Zone checks

```powershell
dotnet build VPNRouter.Tools/PoolAggregator/PoolAggregator.csproj -c Release
dotnet build VPNRouter.Tools/UiMcp/UiMcp.csproj -c Release
```
