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
    public override async Task<int> ExecuteAsync(CommandContext context, TestUpdateSettings settings, CancellationToken cancellationToken)
    {
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
            Channel = "experimental",
        };

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
