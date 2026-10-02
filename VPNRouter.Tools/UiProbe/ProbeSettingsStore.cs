using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tools.UiProbe;

// Settings store that never touches the disk or the real app data: every probe starts from the defaults.
public sealed class ProbeSettingsStore : ISettingsStore
{
    private AppSettings? _saved;

    public AppSettings Load(string? path = null) => (_saved ?? new AppSettings()).EnsureSane();

    public void Save(AppSettings settings, string? path = null) => _saved = settings;

    public string? ResetToDefaults(string? path = null)
    {
        _saved = new AppSettings().EnsureSane();
        return null;
    }

    public string? ConsumeRecoveryNotice() => null;

    public (int Count, string AtUtc) ConsumePlaceholderPruneNotice(AppSettings settings) => (0, string.Empty);

    public void StartWatching(string? path = null, Action<AppSettings>? onReload = null)
    {
    }

    public void StopWatching()
    {
    }
}
