# VPNRouter.Tools zone guidelines

`VPNRouter.Tools` contains supplementary developer and automation tooling, including `PoolAggregator`.

## Scope and responsibilities

- `PoolAggregator`: C# tool invoked by GitHub Actions to aggregate, parse, deduplicate, and enrich public VLESS configurations into `pool.json`.
- Tools here complement core and release automation without embedding runtime VPN logic.

## Safety and guidelines

- Follow canonical repository contract in [`docs/agent-contract.md`](../docs/agent-contract.md).
- Changes to pool aggregation must preserve downstream schema expectations for `pool.json`.

## Zone checks

```powershell
dotnet build VPNRouter.Tools/PoolAggregator/PoolAggregator.csproj -c Release
```
