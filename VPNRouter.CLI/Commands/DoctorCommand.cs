using Spectre.Console;
using Spectre.Console.Cli;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.CLI.Commands;

/// <summary>
/// <c>vpnrouter doctor</c> — diagnostic health check. Verifies config,
/// catalogue, binaries, firewall residue, and other common trouble
/// spots in one run. Exit code 0 if everything is OK, non-zero if
/// any checks failed.
///
/// v2.24.0 Level 3 of plans/vpnrouter-self-healing.md.
/// </summary>
public class DoctorCommand : Command
{
    public override int Execute(CommandContext context)
    {
        AnsiConsole.Write(new Rule("[cyan]VPNRouter Doctor[/]"));
        AnsiConsole.MarkupLine($"Version: [green]{AppVersion.Version}[/]");
        AnsiConsole.MarkupLine($"Data dir: [dim]{AppPaths.DataDir}[/]");
        AnsiConsole.WriteLine();

        int warnings = 0;
        int errors = 0;

        // ── Config ──
        var configPath = AppPaths.ConfigYamlPath;
        if (File.Exists(configPath))
        {
            try
            {
                var yaml = File.ReadAllText(configPath);
                var settings = SettingsLoader.Parse(yaml);
                Ok($"config.yaml parses (schema v{settings.SchemaVersion})");

                if (settings.SchemaVersion < AppSettings.CurrentSchemaVersion)
                    Warn($"config.yaml is schema v{settings.SchemaVersion}, current is v{AppSettings.CurrentSchemaVersion} — will migrate on next normal launch");

                if (settings.Vless != null && settings.Vless.Servers.Count == 0 &&
                    string.IsNullOrWhiteSpace(settings.Vless.Server))
                {
                    Warn("VLESS config has no servers — VPN will not start");
                }
            }
            catch (Exception ex)
            {
                Err($"config.yaml parse failed: {ex.Message}");
            }
        }
        else
        {
            Warn($"config.yaml missing at {configPath} (will be created on first launch)");
        }

        // ── Profile catalogue ──
        var userCatalogue = Path.Combine(AppPaths.ProfilesDir, "default.json");
        if (File.Exists(userCatalogue))
        {
            try
            {
                var json = File.ReadAllText(userCatalogue);
                var collection = Newtonsoft.Json.JsonConvert
                    .DeserializeObject<ProfileCollection>(json);
                if (collection == null || collection.Profiles == null)
                {
                    Err($"user catalogue {userCatalogue} is empty/unparseable — will be quarantined on next launch");
                }
                else
                {
                    Ok($"user catalogue has {collection.Profiles.Count} profiles");

                    var expected = new[] {
                        "Discord_Privacy", "Messengers", "AI_Tools", "Browsers",
                        "Work_Suite", "Streaming", "Gaming", "Privacy_Shell"
                    };
                    var present = collection.Profiles.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var missing = expected.Count(e => !present.Contains(e));
                    if (missing >= 3)
                        Warn($"user catalogue missing {missing}/{expected.Length} v2.22 standard groups — will be quarantined on next launch");
                }
            }
            catch (Exception ex)
            {
                Err($"user catalogue {userCatalogue}: {ex.Message} — will be quarantined");
            }
        }
        else
        {
            Ok("no user catalogue override (using bundled — recommended)");
        }

        // ── sing-box binary ──
        var singboxPath = AppPaths.SingBoxExePath;
        if (File.Exists(singboxPath))
        {
            var size = new FileInfo(singboxPath).Length;
            Ok($"sing-box at {singboxPath} ({size / 1024 / 1024} MB)");
        }
        else
        {
            var bundled = Path.Combine(AppContext.BaseDirectory,
                OperatingSystem.IsWindows() ? "sing-box.exe" : "sing-box");
            if (File.Exists(bundled))
                Warn($"sing-box not deployed to {singboxPath} — will be copied from bundle on first start");
            else
                Err($"sing-box not found at {singboxPath} OR bundled at {bundled}");
        }

        // ── Update receipt ──
        var receipt = UpdateChecker.CheckInstallReceipt(AppVersion.Version);
        if (!string.IsNullOrEmpty(receipt))
            Warn(receipt);

        // ── State / running indicator ──
        var statePath = AppPaths.StatePath;
        if (File.Exists(statePath))
        {
            var state = StateFile.Read();
            if (state != null && state.SingBoxPid > 0)
            {
                try
                {
                    var proc = System.Diagnostics.Process.GetProcessById(state.SingBoxPid);
                    if (!proc.HasExited)
                        Ok($"sing-box running (PID {state.SingBoxPid}, started {state.StartedAt:HH:mm:ss})");
                    else
                        Warn($"state.json references dead sing-box PID {state.SingBoxPid} — orphan state, run 'vpnrouter stop' to clear");
                    try { proc.Dispose(); } catch { }
                }
                catch (ArgumentException)
                {
                    Warn($"state.json references dead sing-box PID {state.SingBoxPid} — orphan state");
                }
            }
        }
        else
        {
            Ok("no running-state file (app is stopped)");
        }

        // ── Lock file (crash detection) ──
        var lockCheck = LockFile.DetectPreviousCrash();
        if (!string.IsNullOrEmpty(lockCheck))
            Warn(lockCheck);

        // ── AppPaths directories ──
        foreach (var dir in new[] { AppPaths.DataDir, AppPaths.LogsDir, AppPaths.CacheDir, AppPaths.BinDir, AppPaths.ProfilesDir })
        {
            if (!Directory.Exists(dir))
                Warn($"directory missing: {dir} (will be created on first launch)");
        }

        // ── Summary ──
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule());
        if (errors == 0 && warnings == 0)
        {
            AnsiConsole.MarkupLine("[bold green]All checks passed.[/]");
            return 0;
        }
        AnsiConsole.MarkupLine(
            $"[bold]Summary:[/] [green]OK[/], [yellow]{warnings} warning(s)[/], [red]{errors} error(s)[/]");
        return errors > 0 ? 2 : 1;

        void Ok(string msg)    { AnsiConsole.MarkupLine($"[green]\u2714 OK[/]    {msg}"); }
        void Warn(string msg)  { AnsiConsole.MarkupLine($"[yellow]\u26A0 WARN[/]  {msg}"); warnings++; }
        void Err(string msg)   { AnsiConsole.MarkupLine($"[red]\u2716 ERR[/]   {msg}"); errors++; }
    }
}
