using CommunityToolkit.Mvvm.ComponentModel;
using VPNRouter.Core.Models;

namespace VPNRouter.Mac.ViewModels;

/// <summary>
/// ViewModel for a single VLESS server entry.
/// </summary>
public partial class ServerViewModel : ViewModelBase
{
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

    public ServerViewModel() { }

    public ServerViewModel(VlessServerEntry entry)
    {
        Name = entry.Name;
        Server = entry.Server;
        Port = entry.Port;
        Uuid = entry.Uuid;
        Flow = entry.Flow;
        Security = entry.Security;
        ServerName = entry.Reality?.ServerName ?? "yahoo.com";
        Fingerprint = entry.Reality?.Fingerprint ?? "firefox";
        PublicKey = entry.Reality?.PublicKey ?? "";
        ShortId = entry.Reality?.ShortId ?? "";
    }

    public VlessServerEntry ToEntry() => new()
    {
        Name = Name,
        Server = Server,
        Port = Port,
        Uuid = Uuid,
        Flow = Flow,
        Security = Security,
        Reality = new VlessRealityConfig
        {
            Enabled = Security == "reality",
            ServerName = ServerName,
            Fingerprint = Fingerprint,
            PublicKey = PublicKey,
            ShortId = ShortId
        }
    };

    public string DisplayName => string.IsNullOrEmpty(Name) ? Server : Name;
}
