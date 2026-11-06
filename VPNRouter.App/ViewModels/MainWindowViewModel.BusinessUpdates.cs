using Avalonia.Threading;
using VPNRouter.Core.Localization;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel
{
    private async Task PrepareBusinessUpdateAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (IsConnecting || IsApplying || _isReconnecting)
                throw new InvalidOperationException(Strings.BusinessUpdateWaitForConnection);
            IsConnecting = true;
        });
        try { await Task.Run(_stopOwnedEngineForUpdate).ConfigureAwait(false); }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsConnecting = false);
            throw new InvalidOperationException(Strings.BusinessUpdateStopFailed);
        }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsConnecting = false;
            if (_engine.IsRunning)
                throw new InvalidOperationException(Strings.BusinessUpdateStopFailed);
            IsConnected = false;
            ConnectButtonText = Strings.StartVPN;
            StatusText = Strings.NotConnected;
            _lastSuccessfulConnectAt = DateTime.MinValue;
        });
    }
}
