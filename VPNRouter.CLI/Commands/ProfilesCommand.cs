using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using VPNRouter.Core.Services;

namespace VPNRouter.CLI.Commands;

// ─── profiles list ────────────────────────────────────────────────────────────

public class ProfilesListCommand : AsyncCommand
{
    public override async Task<int> ExecuteAsync(CommandContext context)
    {
        var appSettings = SettingsLoader.Load();
        var sources = ProfileSourceFactory.Create(appSettings);
        var manager = new ProfileManager(sources);

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Loading profiles...", async ctx =>
            {
                await manager.LoadAsync();
            });

        var profiles = manager.ListProfiles();

        if (profiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No profiles found.[/]");
            return 1;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .Title("[cyan]Available Profiles[/]");

        table.AddColumn("[bold]Name[/]");
        table.AddColumn("[bold]Description[/]");
        table.AddColumn("[bold]Processes[/]");
        table.AddColumn("[bold]DNS Mode[/]");
        table.AddColumn("[bold]Block on Fail[/]");

        foreach (var p in profiles)
        {
            table.AddRow(
                $"[cyan]{p.Name}[/]",
                p.Description,
                p.Processes.Count.ToString(),
                $"[yellow]{p.DnsMode}[/]",
                p.BlockOnVpnFail ? "[red]Yes[/]" : "[grey]No[/]"
            );
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"\n[grey]Total: {profiles.Count} profiles[/]");
        AnsiConsole.MarkupLine("[grey]Usage: vpnrouter start --profile <name>[/]");
        return 0;
    }
}

// ─── profiles show ────────────────────────────────────────────────────────────

public class ProfilesShowSettings : CommandSettings
{
    [CommandArgument(0, "<profile>")]
    [Description("Profile name to show details for")]
    public string Name { get; set; } = string.Empty;
}

public class ProfilesShowCommand : AsyncCommand<ProfilesShowSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ProfilesShowSettings settings)
    {
        var appSettings = SettingsLoader.Load();
        var sources = ProfileSourceFactory.Create(appSettings);
        var manager = new ProfileManager(sources);
        await manager.LoadAsync();

        try
        {
            var profile = manager.GetProfile(settings.Name);

            AnsiConsole.Write(new Rule($"[cyan]{profile.Name}[/]"));
            AnsiConsole.MarkupLine($"[grey]Description:[/] {profile.Description}");
            AnsiConsole.MarkupLine($"[grey]DNS Mode:[/]     [yellow]{profile.DnsMode}[/]");
            AnsiConsole.MarkupLine($"[grey]Block on Fail:[/] {(profile.BlockOnVpnFail ? "[red]Yes[/]" : "[grey]No[/]")}");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Process Rules:[/]");

            foreach (var proc in profile.Processes)
            {
                AnsiConsole.MarkupLine($"  [cyan]•[/] [bold]{proc.Name}[/] (children: {(proc.IncludeChildren ? "yes" : "no")})");
                if (proc.ScanPatterns.Length > 0)
                {
                    AnsiConsole.MarkupLine($"    Patterns: [grey]{string.Join(", ", proc.ScanPatterns)}[/]");
                }
            }

            AnsiConsole.Write(new Rule());
        }
        catch (KeyNotFoundException ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ {ex.Message}[/]");
            return 1;
        }

        return 0;
    }
}

// ─── profiles update ──────────────────────────────────────────────────────────

public class ProfilesUpdateCommand : AsyncCommand
{
    public override async Task<int> ExecuteAsync(CommandContext context)
    {
        var appSettings = SettingsLoader.Load();
        var githubSources = appSettings.ProfileSources
            .Where(s => s.Type == "github" && !string.IsNullOrEmpty(s.Url))
            .ToList();

        if (githubSources.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No GitHub profile sources configured.[/]");
            AnsiConsole.MarkupLine("[grey]Add to config.yaml:[/] profile_sources: [{type: github, url: ...}]");
            return 1;
        }

        var success = false;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Fetching profiles from GitHub...", async ctx =>
            {
                foreach (var source in githubSources)
                {
                    try
                    {
                        ctx.Status($"Fetching: {source.Url}");
                        var ghSource = new GitHubProfileSource(source.Url!);
                        var collection = await ghSource.LoadAsync();

                        if (collection != null)
                        {
                            AnsiConsole.MarkupLine($"[green]✓[/] Updated {collection.Profiles.Count} profiles from GitHub");
                            success = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]✗ Failed:[/] {ex.Message}");
                    }
                }
            });

        return success ? 0 : 1;
    }
}
