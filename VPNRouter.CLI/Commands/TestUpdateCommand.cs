using Serilog;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Platform;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.UpdateSources;

namespace VPNRouter.CLI.Commands;

// CI-only test harness for the auto-update path. Driven by
// .github/workflows/test-windows-update.yml. Gated behind the env var
// VPNROUTER_CI=1 so it can never be invoked accidentally on a real install.
//
// Background: v2.31.7-r10 helper.cmd had a CMD parser bug
// (`set /a SVC_TRIES` referenced before EnableDelayedExpansion was set,
// see UpdateChecker.cs ~line 441). It went undetected for ~7 days because
// no automated test exercised the helper.cmd cmd.exe path — unit tests
// only see the C# string template, not the runtime parser. This command
// is the entry point for integration tests that drive the real helper.
//
// Phase 4 (Wave 18, 2026-05-18): migrated from UpdateChecker.CheckForUpdateAsync
// to IUpdateSource.CheckAsync — same GitHub API + asset pick + version
// compare flow underneath, but routed through the platform-neutral
// IUpdateSource contract that the rest of v3.0 uses. UpdateChecker still
// owns the staging + helper.cmd dispatch via IDesktopInstaller.

public class TestUpdateSettings : CommandSettings
{
    [CommandOption("--target <VERSION>")]
    [Description("Target version label (e.g. 2.31.10-r2). Used for assertions; required.")]
    public string TargetVersion { get; set; } = string.Empty;

    [CommandOption("--staged-dir <DIR>")]
    [Description(
        "Pre-extracted update payload dir. When set, GitHub download is skipped " +
        "and the payload is applied directly. Used by CI to test the helper.cmd " +
        "parser without depending on a published release.")]
    public string? StagedDir { get; set; }

    [CommandOption("--repo <REPO>")]
    [Description("GitHub repo in owner/name form (default: PavelLizunov/VPNRouter). Ignored when --staged-dir is set.")]
    public string Repo { get; set; } = "PavelLizunov/VPNRouter";
}

public class TestUpdateCommand : AsyncCommand<TestUpdateSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, TestUpdateSettings settings)
    {
        // Hard CI gate. This command writes to %ProgramData%\VPNRouter and
        // launches a detached cmd.exe that overwrites the running app dir —
        // not something we want a curious user invoking from a working
        // install.
        var ciEnv = Environment.GetEnvironmentVariable("VPNROUTER_CI");
        if (!string.Equals(ciEnv, "1", StringComparison.Ordinal))
        {
            AnsiConsole.MarkupLine("[red]✗ test-update is CI-only.[/]");
            AnsiConsole.MarkupLine("[grey]  Set VPNROUTER_CI=1 to enable. Refusing to run on a real install.[/]");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(settings.TargetVersion))
        {
            AnsiConsole.MarkupLine("[red]✗ --target <version> is required[/]");
            return 2;
        }

        Log.Information("=== test-update CI command ===");
        Log.Information("Current AppVersion : {Cur}", AppVersion.Version);
        Log.Information("Target version     : {Tgt}", settings.TargetVersion);
        Log.Information("App base dir       : {Dir}", AppContext.BaseDirectory);
        Log.Information("Staged dir         : {Dir}", settings.StagedDir ?? "(none — will download from GitHub)");
        Log.Information("GitHub repo        : {Repo}", settings.Repo);

        var updateSettings = new UpdateSettings
        {
            GitHubRepo = settings.Repo,
            // Always experimental in CI — we may be testing prerelease -rN tags.
            Channel = "experimental",
        };

        // Phase 4 migration — UpdateChecker stays as the IDesktopInstaller
        // adapter (staging + helper.cmd dispatch) and supplies the legacy
        // event stream we log here. The new entry point is IUpdateSource;
        // it shares the same UpdateChecker instance under the hood.
        var checker = new UpdateChecker(updateSettings, AppVersion.Version);
        var source = PlatformServices.CreateUpdateSource(
            updateSettings,
            AppVersion.Version,
            PolicyHttpClient.Shared,
            desktopInstaller: checker);
        checker.StatusChanged += s => Log.Information("[update] {Status}", s);
        var lastLoggedPercent = -1;
        checker.DownloadProgress += p =>
        {
            // Throttle to every 10% so we don't spam CI logs.
            if (p / 10 != lastLoggedPercent / 10)
            {
                Log.Information("[update] download {Pct}%", p);
                lastLoggedPercent = p;
            }
        };

        string extractedDir;
        UpdateSourceInfo? info = null;

        if (!string.IsNullOrEmpty(settings.StagedDir))
        {
            extractedDir = Path.GetFullPath(settings.StagedDir);
            if (!Directory.Exists(extractedDir))
            {
                Log.Error("--staged-dir does not exist: {Dir}", extractedDir);
                return 3;
            }
            Log.Information("Skipping GitHub download — using pre-staged payload at {Dir}", extractedDir);
        }
        else
        {
            try
            {
                info = await source.CheckAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "IUpdateSource.CheckAsync threw");
                return 4;
            }

            if (info == null)
            {
                Log.Error(
                    "No update available — current {Cur} reported up-to-date by GitHub. " +
                    "Either the target release isn't published yet, or AppVersion.Version " +
                    "in this binary is already >= target. CI runs typically extract a " +
                    "previous-stable install ZIP first to make this assertion meaningful.",
                    AppVersion.Version);
                return 5;
            }

            Log.Information("Update found: {Latest} ({Mb} MB, source={Src})",
                info.Version,
                info.AssetSize / 1024 / 1024,
                source.SourceId);

            if (!string.Equals(info.Version, settings.TargetVersion, StringComparison.OrdinalIgnoreCase))
            {
                Log.Error(
                    "Latest published version {Latest} != requested target {Target}. " +
                    "Either the workflow polled prematurely, or the wrong release was tagged.",
                    info.Version, settings.TargetVersion);
                return 6;
            }

            try
            {
                extractedDir = await source.DownloadAsync(info).ConfigureAwait(false);
                Log.Information("Downloaded + extracted to: {Dir}", extractedDir);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "IUpdateSource.DownloadAsync failed");
                return 7;
            }
        }

        try
        {
            // ApplyAsync writes helper.cmd to %TEMP%, launches it detached,
            // and returns immediately. The helper waits for THIS process
            // (the CLI) to exit before doing the file copy. So we exit
            // promptly — the CI workflow then polls update.log for completion.
            //
            // When --staged-dir was supplied we don't have a real
            // UpdateSourceInfo; synthesize a minimal one with the target
            // version so the installer logs receipt with the correct
            // version stamp.
            info ??= new UpdateSourceInfo(
                Version: settings.TargetVersion,
                ReleaseUrl: string.Empty,
                AssetName: $"VPNRouter-v{settings.TargetVersion}-win.zip",
                DownloadUrl: string.Empty,
                AssetSize: 0,
                AssetSha256: null,
                IsPrerelease: false,
                ReleaseNotes: string.Empty);
            await source.ApplyAsync(info, extractedDir).ConfigureAwait(false);
            Log.Information("ApplyAsync dispatched. helper.cmd will run after this process exits.");
            Log.Information("CI workflow should now poll {Log} for 'helper done' or fail patterns.",
                Path.Combine(AppPaths.LogsDir, "update.log"));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "IUpdateSource.ApplyAsync threw");
            return 8;
        }

        return 0;
    }
}
