# README polish: Windows renders, an honest clip caption, no emoji (R-2)

## Why

The owner asked to bring the README work (R-1, #538 and #539, merged) to an ideal state. A review of the merged result found:

- The screenshots and the clip were rendered by the Linux build of the UI renderer. The home screen therefore shows Linux-only text ("Configure VPN autostart at Linux boot", from `OsDisplayName`) and Linux fonts, on a README whose first install line is Windows.
- The clip is assembled from those renders with an animated cursor, a crossfade and a highlight. It is not a screen recording, and the README does not say so; the hero caption does not say which OS was rendered.
- The guide pages (`docs/guide/install.md`, English and Russian) still carry the platform emoji the contract allows only "until a dedicated presentation edit replaces them"; R-1 moved them unchanged.
- The merged task branches `dsh/readme-front-page` and `dsh/readme-clip-quality` are still on GitHub.

## What

1. `.github/workflows/build-ui-mcp.yml` also publishes the renderer for `win-x64` (self-contained, same project, `-p:UiMcpRid=win-x64`) and uploads it as `vpnrouter-ui-mcp-win-x64`. The Linux smoke session stays as it is; a Windows smoke is the render on WINBRAT below.
2. The README screens are rendered on WINBRAT with that binary: headless, fixture data, the installed app and the VPN are not touched (read-only preflight recorded below). The binary is copied to a task-owned folder, used, and the folder is removed afterwards.
3. The six PNG and two WebP files in `docs/images/` are replaced by the Windows renders (same file names, same size rules, clip rebuilt from a lossless intermediate at WebP quality 95 and checked with `clip_quality.py`).
4. `README.md` and `README.ru.md`: the hero caption says the screens are renders of the Windows build with sample data; the clip gets a caption saying it is assembled from such renders with an animated cursor and is not a screen recording.
5. `docs/guide/install.md` and `docs/guide/ru/install.md`: platform emoji replaced by plain text labels.
6. The two merged task branches are deleted on GitHub after the merge.

## Not covered

- Android screenshots: the renderer does not draw the Android UI and an emulator run needs a prepared app state; not part of this task.
- macOS and Linux renders: the README shows one platform (Windows) and says so.

## Invariants

- No product code changes. No credentials or real servers in any image. Image file names stay, so the README screenshot test needs no change.
- `VPNRouter-v{version}-android-arm64.apk` stays in both READMEs. README.ru mirrors README.md. No emoji added anywhere.
- Worker rules (`docs/test-workers.md`): preflight numbers recorded, one scenario at a time, task-owned paths only, cleanup on PASS and FAIL.

## Verification

Worker preflight (read-only, 2026-10-04): WINBRAT identity confirmed, 8 logical CPUs at 2 percent load, 13.4 GB of 16 GB RAM free, about 1 GB swap in use, 12 GB free on C:, no dotnet, msbuild, java or compiler server running, `win.lock` free; the installed app was already running and is left alone.

Renderer artifact from the PR run, render on WINBRAT, the six images and two clips looked at, `clip_quality.py` (no frame under 0.98), `readme_check.py` and `readme_phone.py` on both READMEs, link check, PR CI on the exact head.

## Rollback

Revert the PR. The previous images and workflow are in Git.

## Outcome

Pending.
