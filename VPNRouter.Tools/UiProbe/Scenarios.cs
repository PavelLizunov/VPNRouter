using System.Reflection;
using System.Text.Json;
using VPNRouter.App.ViewModels;
using VPNRouter.Core.Models;

namespace VPNRouter.Tools.UiProbe;

public sealed record Scenario(string Name, string Description, Action<MainWindowViewModel> Apply);

// Presets are an approximation of a state through the public view model surface; `state` (property name -> value) reaches the rest.
public static class Scenarios
{
    public static IReadOnlyList<Scenario> All { get; } = new List<Scenario>
    {
        new("default", "Fresh install defaults, nothing connected, no servers", _ => { }),
        new("connected", "Tunnel up", vm =>
        {
            vm.IsConnecting = false;
            vm.IsConnected = true;
            vm.StatusText = "Connected";
        }),
        new("connecting", "Connect in progress", vm =>
        {
            vm.IsConnected = false;
            vm.IsConnecting = true;
        }),
        new("configured", "One saved server link, not connected (add state IsConnected / IsConnecting / SmpErrorText)", AddOneServer),
        new("alert", "One saved server, the tunnel reported a failover alert (the dead-config warning of the home screen)", vm =>
        {
            AddOneServer(vm);
            vm.IsConnecting = false;
            vm.IsConnected = true;
            typeof(MainWindowViewModel).GetField("_lastConnectionAlert", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.SetValue(vm, "⚠ The server does not respond and the subscription has no other servers.");
            typeof(MainWindowViewModel).GetMethod("RaiseSimpleAlertProps", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.Invoke(vm, null);
        }),
        new("problems", "Tunnel up in full mode, the home problems card lists game traffic through the VPN and a too large MTU", vm =>
        {
            vm.IsSplitTunnel = false;
            vm.IsConnecting = false;
            vm.IsConnected = true;
            vm.StatusText = "Connected";
            typeof(MainWindowViewModel).GetMethod("SetHomeProblems", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.Invoke(vm, new object[]
                {
                    new[]
                    {
                        new VPNRouter.Core.Services.DetectedProblem(VPNRouter.Core.Services.DetectedProblemKind.GameTrafficThroughFullTunnel),
                        new VPNRouter.Core.Services.DetectedProblem(VPNRouter.Core.Services.DetectedProblemKind.MtuAbovePath, 1380),
                    }
                });
        }),
        new("versions", "Tunnel up, the version list of Settings > Updates is open with two older stable versions", vm =>
        {
            vm.IsConnecting = false;
            vm.IsConnected = true;
            var updates = vm.UpdateVm;
            updates.StableVersions.Clear();
            updates.StableVersions.Add(new RollbackReleaseItemViewModel("2.50.3", true, null, null));
            foreach (var version in new[] { "2.50.2", "2.50.1" })
            {
                var info = new VPNRouter.Core.Services.UpdateSources.UpdateSourceInfo(
                    version, "https://example.invalid", "app.zip", "https://example.invalid/app.zip", 1, null, false, string.Empty);
                updates.StableVersions.Add(new RollbackReleaseItemViewModel(version, false, info, _ => { }));
            }
            var stateField = typeof(UpdateNotificationViewModel).GetField("_versionHistoryState", BindingFlags.NonPublic | BindingFlags.Instance);
            if (stateField != null)
                stateField.SetValue(updates, Enum.Parse(stateField.FieldType, "Ready"));
            updates.IsVersionHistoryVisible = true;
        }),
        new("many-servers", "200 servers, every tenth with a very long name", vm =>
        {
            for (var i = 0; i < 200; i++)
            {
                var name = i % 10 == 0
                    ? $"Extremely long server display name number {i} that has to wrap or be trimmed somewhere sensible"
                    : $"Server {i}";
                vm.Servers.Add(new ServerViewModel(new VlessServerEntry
                {
                    Name = name,
                    Server = $"203.0.113.{i % 250 + 1}",
                    Port = 443,
                }));
            }
        }),
    };

    private static void AddOneServer(MainWindowViewModel vm)
    {
        var server = new ServerViewModel(new VlessServerEntry
        {
            Name = "Frankfurt 1",
            Server = "203.0.113.10",
            Port = 443,
        });
        vm.Servers.Add(server);
        vm.SelectedServer = server;
    }

    public static Scenario? Find(string? name) =>
        All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    // Sets public view model properties by name. Returns what could not be applied.
    public static List<string> ApplyState(MainWindowViewModel vm, IReadOnlyDictionary<string, JsonElement>? state)
    {
        var problems = new List<string>();
        if (state == null) return problems;
        foreach (var (name, value) in state)
        {
            var prop = vm.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop == null || !prop.CanWrite || prop.GetSetMethod() == null)
            {
                problems.Add($"{name}: no public settable property");
                continue;
            }
            try
            {
                var converted = JsonSerializer.Deserialize(value.GetRawText(), prop.PropertyType);
                prop.SetValue(vm, converted);
            }
            catch (Exception ex)
            {
                problems.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return problems;
    }

    // Writable scalar properties of the view model, for choosing a `state`.
    public static IReadOnlyList<string> SettableProperties(string? filter)
    {
        var result = new List<string>();
        foreach (var p in typeof(MainWindowViewModel).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanWrite || p.GetSetMethod() == null) continue;
            var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            if (!(t == typeof(bool) || t == typeof(string) || t == typeof(int) || t == typeof(double) || t.IsEnum)) continue;
            if (!string.IsNullOrEmpty(filter) && p.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            result.Add($"{p.Name}: {(t.IsEnum ? string.Join("|", Enum.GetNames(t)) : t.Name)}");
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }
}
