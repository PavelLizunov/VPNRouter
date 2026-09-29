using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Management;
using Microsoft.Win32.SafeHandles;
using Serilog;
using Native = VPNRouter.Core.Services.SplitTunnelDriverInterop;
using Proto = VPNRouter.Core.Services.SplitTunnelDriverProtocol;

namespace VPNRouter.Core.Services;

internal sealed partial class SplitTunnelDriverManager
{
    private void SubscribeNetworkChangeLocked()
    {
        if (_netChangeSubscribed) return;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        _netChangeSubscribed = true;
    }

    private void UnsubscribeNetworkChangeLocked()
    {
        if (!_netChangeSubscribed) return;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _netChangeSubscribed = false;
        var cts = Interlocked.Exchange(ref _debounceCts, null);
        if (cts is not null)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            cts.Dispose();
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        var fresh = new CancellationTokenSource();
        // The superseder cancels then disposes the prior CTS; the task must never dispose its own CTS.
        var prior = Interlocked.Exchange(ref _debounceCts, fresh);
        if (prior is not null)
        {
            try { prior.Cancel(); }
            catch (ObjectDisposedException) { }
            finally { prior.Dispose(); }
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(NetChangeDebounce, fresh.Token).ConfigureAwait(false);
                await ReRegisterIfChangedAsync(fresh.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { _log.Debug(ex, "[SplitTunnel] NetworkChange handler error (ignored)"); }
        });
    }

    internal void RaiseNetworkAddressChangedForTest() => OnNetworkAddressChanged(this, EventArgs.Empty);

    private async Task ReRegisterIfChangedAsync(CancellationToken ct)
    {
        if (_disposed) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_engaged || _device is not { IsInvalid: false } || _lastRequest is null) return;
            var newAddrs = ResolveAddresses(_lastRequest);
            if (!SplitTunnelPolicy.ShouldReRegister(_lastAddrs, newAddrs)) return;
            _log.Information("[SplitTunnel] Internet address changed — re-registering (inet {Old} → {New})",
                _lastAddrs.inetV4, newAddrs.inetV4);
            if (TryReRegisterLocked(newAddrs)) { _lastAddrs = newAddrs; return; }
        }
        finally { _gate.Release(); }

        try { await Task.Delay(ReRegisterRetryDelay, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        if (_disposed) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_engaged || _device is not { IsInvalid: false } || _lastRequest is null) return;
            var retryAddrs = ResolveAddresses(_lastRequest);
            if (TryReRegisterLocked(retryAddrs)) { _lastAddrs = retryAddrs; return; }
            _log.Warning("[SplitTunnel] Re-register failed twice — disengaging (excluded fall back to post-capture)");
            bool before = _engaged;
            DisengageLocked();
            if (_engaged != before) RaiseEngagedChanged(_engaged);
        }
        finally { _gate.Release(); }
    }

    private bool TryReRegisterLocked((IPAddress? tunV4, IPAddress? inetV4, IPAddress? tunV6, IPAddress? inetV6) a)
    {
        if (a.inetV4 is null)
        {
            _log.Warning("[SplitTunnel] Re-register skipped — no internet NIC resolved (won't bind excluded apps to 0.0.0.0)");
            return false;
        }
        try
        {
            IoctlLocked(Proto.IoctlRegisterIpAddresses, Proto.BuildAddresses(a.tunV4, a.inetV4, a.tunV6, a.inetV6), null);
            return true;
        }
        catch (Exception ex) { _log.Warning(ex, "[SplitTunnel] REGISTER_IP_ADDRESSES re-register failed"); return false; }
    }
}
