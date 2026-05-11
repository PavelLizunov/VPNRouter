using System.ComponentModel;
using Serilog;
using Spectre.Console;
using Spectre.Console.Cli;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.CLI.Commands;

/// <summary>
/// r9 Phase 2 — local verification harness for the wgturn-core
/// integration. Spins up <see cref="EmergencyChannelEngine"/> with a
/// user-supplied <c>wgturn://</c> URL + VK link, lets it spawn
/// <c>wgturn-cli.exe</c>, prints state transitions to the console,
/// and tears down on Ctrl+C.
///
/// <para>This is NOT a production user-facing command — Phase 3 will
/// drive the engine from the desktop UI. This command exists so the
/// Phase-2 chip can be locally smoke-tested before any UI work, per
/// the chip spec's "Verify locally (Windows VM)" step.</para>
/// </summary>
public class EmergencyChannelTestCommand : Command<EmergencyChannelTestCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandOption("--wgturn-url <URL>")]
        [Description("wgturn:// share URL issued by the wgturn server")]
        public string WgturnUrl { get; set; } = string.Empty;

        [CommandOption("--vk-link <URL>")]
        [Description("VK Calls invite (https://vk.com/call/join/<id>)")]
        public string VkLink { get; set; } = string.Empty;

        [CommandOption("--dummy")]
        [Description("Use a built-in dummy URL + VK link (smoke-test only — wgturn-cli will fail to connect, but the spawn lifecycle is exercised)")]
        public bool Dummy { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var wgturnUrl = settings.WgturnUrl;
        var vkLink = settings.VkLink;

        if (settings.Dummy)
        {
            wgturnUrl = "wgturn://eyJ2IjoxLCJzcCI6ImR1bW15In0#test";
            vkLink = "https://vk.com/call/join/dummy";
            AnsiConsole.MarkupLine("[yellow]Using --dummy values (wgturn-cli will fail to connect, that's expected)[/]");
        }

        if (!EmergencyChannelConfig.TryParse(wgturnUrl, vkLink, out var config))
        {
            AnsiConsole.MarkupLine("[red]✗ Invalid wgturn:// URL[/]");
            AnsiConsole.MarkupLine("[grey]Pass --wgturn-url and --vk-link, or --dummy for a smoke test.[/]");
            return 2;
        }

        var exePath = AppPaths.WgturnCliExePath;
        AnsiConsole.MarkupLine($"[grey]Binary:[/] {exePath}");
        AnsiConsole.MarkupLine($"[grey]Log:   [/] {AppPaths.WgturnCliLogPath}");
        if (!File.Exists(exePath))
        {
            AnsiConsole.MarkupLine($"[red]✗ wgturn-cli not found at {exePath}[/]");
            AnsiConsole.MarkupLine("[grey]Phase 1 chip drops this binary at install time. For local testing, place wgturn-cli.exe at the path manually.[/]");
            return 3;
        }

        AppPaths.EnsureDirectories();

        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        using var engine = new EmergencyChannelEngine(logger);

        engine.StateChanged += s =>
            AnsiConsole.MarkupLine($"[cyan]→ State[/]: [bold]{s}[/]");
        engine.ErrorOccurred += msg =>
            AnsiConsole.MarkupLine($"[red]→ Error[/]: {Markup.Escape(msg)}");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            AnsiConsole.MarkupLine("[yellow]Ctrl+C — stopping...[/]");
            cts.Cancel();
        };

        AnsiConsole.MarkupLine($"[grey]URL:    [/] {Markup.Escape(config.WgturnUrl)}");
        AnsiConsole.MarkupLine($"[grey]VK:     [/] {Markup.Escape(config.VkLink)}");
        AnsiConsole.MarkupLine($"[grey]Label:  [/] {Markup.Escape(config.Label ?? "(none)")}");

        try
        {
            engine.StartAsync(config, cts.Token).GetAwaiter().GetResult();
            AnsiConsole.MarkupLine($"[green]✓[/] Started (PID {engine.Pid})");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ Start failed:[/] {Markup.Escape(ex.Message)}");
            return 4;
        }

        // Block until Ctrl+C or until the engine reports Failed.
        while (!cts.IsCancellationRequested && engine.State == EmergencyChannelState.Connected)
        {
            Thread.Sleep(500);
        }

        engine.Stop();
        AnsiConsole.MarkupLine($"[green]✓[/] Stopped — final state {engine.State}");
        return 0;
    }
}
