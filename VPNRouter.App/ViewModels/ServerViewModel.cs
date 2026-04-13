using CommunityToolkit.Mvvm.ComponentModel;
using VPNRouter.Core.Models;

namespace VPNRouter.App.ViewModels;

/// <summary>
/// ViewModel for a single VLESS server entry.
/// Preserves the full VlessServerEntry so TLS, Transport, and Reality
/// configs survive SaveSettings → Load round-trips.
/// </summary>
public partial class ServerViewModel : ViewModelBase
{
    // Keep the original entry for fields not exposed in UI (TLS, Transport, etc.)
    private VlessServerEntry _originalEntry;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _server = string.Empty;
    [ObservableProperty] private int _port = 443;
    [ObservableProperty] private string _uuid = string.Empty;
    [ObservableProperty] private string _flow = "xtls-rprx-vision";
    [ObservableProperty] private string _security = "reality";
    [ObservableProperty] private string _serverName = "yahoo.com";
    [ObservableProperty] private string _fingerprint = "firefox";
    [ObservableProperty] private string _publicKey = string.Empty;
    [ObservableProperty] private string _shortId = string.Empty;
    [ObservableProperty] private bool _isSelected;

    public ServerViewModel()
    {
        _originalEntry = new VlessServerEntry();
    }

    public ServerViewModel(VlessServerEntry entry)
    {
        _originalEntry = entry;
        Name = entry.Name;
        Server = entry.Server;
        Port = entry.Port;
        Uuid = entry.Uuid;
        Flow = entry.Flow;
        Security = entry.Security;

        // Reality fields (for detail editor)
        if (entry.Reality != null)
        {
            ServerName = entry.Reality.ServerName ?? "yahoo.com";
            Fingerprint = entry.Reality.Fingerprint ?? "firefox";
            PublicKey = entry.Reality.PublicKey ?? "";
            ShortId = entry.Reality.ShortId ?? "";
        }
        // TLS fields (for display — SNI as ServerName)
        else if (entry.Tls != null && entry.Tls.Enabled)
        {
            ServerName = entry.Tls.ServerName ?? entry.Server;
            Fingerprint = entry.Tls.Fingerprint ?? "";
        }
    }

    /// <summary>
    /// Convert back to VlessServerEntry, preserving TLS/Transport/Reality
    /// that the UI doesn't edit directly.
    /// </summary>
    public VlessServerEntry ToEntry()
    {
        // Start from original entry to preserve TLS, Transport, etc.
        var entry = _originalEntry ?? new VlessServerEntry();

        // Apply UI-editable fields
        entry.Name = Name;
        entry.Server = Server;
        entry.Port = Port;
        entry.Uuid = Uuid;
        entry.Flow = Flow;
        entry.Security = Security;

        // Update Reality from UI fields
        if (Security?.Equals("reality", StringComparison.OrdinalIgnoreCase) == true)
        {
            entry.Reality ??= new VlessRealityConfig();
            entry.Reality.Enabled = true;
            entry.Reality.ServerName = ServerName;
            entry.Reality.Fingerprint = Fingerprint;
            entry.Reality.PublicKey = PublicKey;
            entry.Reality.ShortId = ShortId;
        }

        // Update TLS SNI from UI if TLS mode
        if (Security?.Equals("tls", StringComparison.OrdinalIgnoreCase) == true)
        {
            entry.Tls ??= new VlessTlsConfig();
            entry.Tls.Enabled = true;
            entry.Tls.ServerName = ServerName;
        }

        return entry;
    }

    public string DisplayName => string.IsNullOrEmpty(Name) ? Server : Name;
}
