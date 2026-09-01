# Contributing

Thanks for your interest in VPNRouter. This is primarily a solo project, but
issues and pull requests are welcome.

## License

VPNRouter is licensed under **GPL-3.0** (see `LICENSE`). By contributing you
agree your contributions are licensed under the same terms.

## Building

Requires the .NET SDK pinned by global.json (10.0.301). Android additionally needs the .NET Android
workload.

    dotnet build VPNRouter.sln -c Release
    dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release
    dotnet run --project VPNRouter.App

Most VPN features need Administrator/root (TUN adapter, firewall, ETW).

## Project layout

- `VPNRouter.Core` — cross-platform business logic, no UI deps. Single source of
  truth; platform code gated behind `#if PLATFORM_WINDOWS` / `#if PLATFORM_ANDROID`.
- `VPNRouter.App` — Avalonia desktop UI.
- `VPNRouter.Android` — Avalonia.Android UI (source-links Core).
- `VPNRouter.CLI` — Spectre.Console CLI.
- `VPNRouter.Service` — Windows Service wrapper.
- `VPNRouter.Tests` — xUnit tests.

See `AGENTS.md` and `docs/agent-contract.md` for contract and architecture rules, and `CURRENT_STATE.md` for live release/platform facts.

## Code style

- `#nullable enable` in new files; `sealed` by default; records for immutable DTOs.
- Localized UI strings live only in `VPNRouter.Core/Localization/Strings.cs`
  (`Ru ? "..." : "..."`); App/Android use pass-through wrappers — never duplicate keys.
- sing-box `process_name` matching is **case-sensitive** — never lowercase
  process names.
- No emoji in source, config, or docs.

## Commits and PRs

- Conventional-commit subjects (`feat:`, `fix:`, `docs:`, `ci:`, `build:`, ...),
  under 72 characters; the body explains the why.
- Keep changes focused; add or adjust tests for behavior changes.
- CI builds the solution and runs the full test suite on macOS and Linux — make
  sure `dotnet build -c Release` and `dotnet test` are green locally first.

## Security issues

Do not open a public issue for security vulnerabilities — see `SECURITY.md`.
