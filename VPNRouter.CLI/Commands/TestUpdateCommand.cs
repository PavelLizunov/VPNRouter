using Serilog;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

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

        var checker = new UpdateChecker(updateSettings, AppVersion.Version);
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
            UpdateInfo? info;
            try
            {
                info = await checker.CheckForUpdateAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "CheckForUpdateAsync threw");
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

            Log.Information("Update found: {Latest} ({Mb} MB, lite={Lite})",
                info.LatestVersion,
                info.SizeBytes / 1024 / 1024,
                info.HasLiteUpdate);

            if (!string.Equals(info.LatestVersion, settings.TargetVersion, StringComparison.OrdinalIgnoreCase))
            {
                Log.Error(
                    "Latest published version {Latest} != requested target {Target}. " +
                    "Either the workflow polled prematurely, or the wrong release was tagged.",
                    info.LatestVersion, settings.TargetVersion);
                return 6;
            }

            try
            {
                extractedDir = await checker.DownloadAndStageAsync(info);
                Log.Information("Downloaded + extracted to: {Dir}", extractedDir);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "DownloadAndStageAsync failed");
                return 7;
            }
        }

        try
        {
            // ApplyUpdate writes helper.cmd to %TEMP%, launches it detached,
            // and returns immediately. The helper waits for THIS process
            // (the CLI) to exit before doing the file copy. So we exit
            // promptly — the CI workflow then polls update.log for completion.
            checker.ApplyUpdate(extractedDir);
            Log.Information("ApplyUpdate dispatched. helper.cmd will run after this process exits.");
            Log.Information("CI workflow should now poll {Log} for 'helper done' or fail patterns.",
                Path.Combine(AppPaths.LogsDir, "update.log"));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ApplyUpdate threw");
            return 8;
        }

        return 0;
    }
}
