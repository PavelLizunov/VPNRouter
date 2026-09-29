# H-33: remove the unused using directives that the file splits copied

## Why

H-21 and H-23 copied the whole using list of the original file into every new partial file, so most of the
40 new files carry many unused `using` lines (about 500 using lines in total). They are noise and count
towards the line total.

## What

`dotnet format style --diagnostics IDE0005` on `windows-worker`, limited with `--include` to the partial
files of `MainWindowViewModel`, `FreeConfigsPageViewModel`, `CustomConfigInjector`, `VpnEngine`,
`UpdateChecker` and `SplitTunnelDriverManager`. The tool only reported unused usings in the Windows
configuration; the patch removes 336 lines in 38 files and adds none (the temporary `.editorconfig` and
documentation-file settings used to enable the analyzer were not committed).

## Risk

A using that is needed only in a non-Windows `#if` branch could look unused in the Windows configuration.
The Ubuntu test job and the Android compile job build those configurations and would fail.

## Verification

Exact-head CI (Windows, Ubuntu, Android compile); App, CLI and Service builds on `windows-worker`.

## Outcome

Pending CI.
