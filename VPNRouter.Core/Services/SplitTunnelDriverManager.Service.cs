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
    public async Task SweepStaleStateAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(_sysPath)) return;

        try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        try
        {
            if (_engaged) return;
            if (!EnsureDeviceOpenLocked()) return;
            try
            {
                var state = GetStateLocked();
                if (state > Proto.DriverState.Started)
                {
                    _log.Information("[SplitTunnel] Stale driver state {State} from a prior session — RESET", state);
                    TryResetLocked();
                }
            }
            finally { CloseDeviceLocked(); }
        }
        catch (Exception ex) { _log.Debug(ex, "[SplitTunnel] Stale-state sweep failed (ignored)"); }
        finally { _gate.Release(); }
    }

    private void VerifySysIntegrityLocked()
    {
        try
        {
            var sidecarPath = Path.Combine(Path.GetDirectoryName(_sysPath) ?? string.Empty, "checksums.sha256");
            if (!File.Exists(sidecarPath))
            {
                _log.Debug("[SplitTunnel] No driver checksums.sha256 sidecar — skipping integrity diagnostic");
                return;
            }
            var expected = SplitTunnelPolicy.ParseSidecarHashFor(File.ReadAllText(sidecarPath), DriverFileName);
            if (expected == null)
            {
                _log.Debug("[SplitTunnel] {File} not listed in checksums.sha256 — skipping integrity diagnostic", DriverFileName);
                return;
            }
            using var fs = File.OpenRead(_sysPath);
            var actual = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(fs));
            if (actual == expected)
                _log.Debug("[SplitTunnel] Driver integrity OK (sha256 matches sidecar)");
            else
                _log.Warning("[SplitTunnel] Driver sha256 MISMATCH vs checksums.sha256 (expected {Exp}, got {Act}) — " +
                    "stale/partial .sys? ABI mismatch possible; engaging anyway (Windows signature check is the tamper gate)",
                    expected, actual);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "[SplitTunnel] Driver integrity diagnostic failed (ignored)");
        }
    }

    private bool EnsureServiceLocked()
    {
        IntPtr scm = Native.OpenSCManager(null, null, Native.SC_MANAGER_ALL_ACCESS);
        if (scm == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            LastFailureReason = $"True-split needs administrator access to Service Control Manager (OpenSCManager err={err}).";
            _log.Warning("[SplitTunnel] OpenSCManager failed (err={Err}) — need admin; post-capture stands",
                err);
            return false;
        }
        try
        {
            IntPtr svc = Native.OpenService(scm, Proto.ServiceName, Native.SERVICE_ALL_ACCESS);
            if (svc == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == Native.ERROR_SERVICE_DOES_NOT_EXIST)
                    return CreateAndStartServiceLocked(scm);
                if (err == Native.ERROR_SERVICE_MARKED_FOR_DELETE)
                    LastFailureReason = "True-split driver service is being deleted by Windows; reboot Windows, then retry True Split.";
                else
                    LastFailureReason = $"True-split driver service could not be opened (OpenService err={err}).";
                _log.Warning("[SplitTunnel] OpenService failed (err={Err}) — post-capture stands", err);
                return false;
            }
            try
            {
                string? existing = QueryServiceBinPath(svc);
                var action = SplitTunnelPolicy.ClassifyServiceBinPath(existing ?? string.Empty, _sysPath);
                switch (action)
                {
                    case Proto.ServiceCollisionAction.BailForeign:
                        LastFailureReason =
                            $"True-split driver service '{Proto.ServiceName}' is owned by another install ({existing ?? "unknown path"}).";
                        _log.Warning("[SplitTunnel] '{Svc}' exists with a foreign binPath ({Path}) — not touching it " +
                            "(real Mullvad or unknown); post-capture stands", Proto.ServiceName, existing);
                        return false;

                    case Proto.ServiceCollisionAction.AdoptMovedInstall:
                        _log.Information("[SplitTunnel] Adopting our relocated service — ChangeServiceConfig binPath → {Path}", _sysPath);
                        if (!Native.ChangeServiceConfig(svc, Native.SERVICE_NO_CHANGE, Native.SERVICE_NO_CHANGE,
                                Native.SERVICE_NO_CHANGE, _sysPath, null, IntPtr.Zero, null, null, null, null))
                            _log.Warning("[SplitTunnel] ChangeServiceConfig failed (err={Err}) — trying start anyway",
                                Marshal.GetLastWin32Error());
                        break;

                    case Proto.ServiceCollisionAction.StartExisting:
                        break;
                }
                if (StartServiceLocked(svc, out int startErr)) return true;
                _log.Warning("[SplitTunnel] StartService failed (err={Err}); not deleting/recreating kernel service at runtime", startErr);
                return false;
            }
            finally
            {
                if (svc != IntPtr.Zero)
                    Native.CloseServiceHandle(svc);
            }
        }
        finally { Native.CloseServiceHandle(scm); }
    }

    private bool CreateAndStartServiceLocked(IntPtr scm)
    {
        IntPtr svc = Native.CreateService(scm, Proto.ServiceName, ServiceDisplayName,
            Native.SERVICE_ALL_ACCESS, Native.SERVICE_KERNEL_DRIVER, Native.SERVICE_DEMAND_START,
            Native.SERVICE_ERROR_NORMAL, _sysPath, null, IntPtr.Zero, null, null, null);
        if (svc == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            if (err == Native.ERROR_SERVICE_EXISTS)
            {
                svc = Native.OpenService(scm, Proto.ServiceName, Native.SERVICE_ALL_ACCESS);
                if (svc == IntPtr.Zero)
                {
                    _log.Warning("[SplitTunnel] Service appeared then vanished (err={Err})", Marshal.GetLastWin32Error());
                    return false;
                }
            }
            else
            {
                _log.Warning("[SplitTunnel] CreateService failed (err={Err}) — post-capture stands", err);
                if (err == Native.ERROR_SERVICE_MARKED_FOR_DELETE)
                    LastFailureReason = "True-split driver service is being deleted by Windows; reboot Windows, then retry True Split.";
                else
                    LastFailureReason = $"True-split driver service could not be created (CreateService err={err}).";
                return false;
            }
        }
        try { return StartServiceLocked(svc, out _); }
        finally { Native.CloseServiceHandle(svc); }
    }

    private bool StartServiceLocked(IntPtr svc, out int err)
    {
        err = 0;
        if (Native.StartService(svc, 0, null)) return true;
        err = Marshal.GetLastWin32Error();
        if (err == Native.ERROR_SERVICE_ALREADY_RUNNING) return true;
        if (err == Native.ERROR_ALREADY_EXISTS)
        {
            if (Native.QueryServiceStatus(svc, out var status)
                && status.dwCurrentState == Native.SERVICE_STOPPED)
            {
                var path = QueryServiceBinPath(svc) ?? "unknown";
                LastFailureReason = DescribeRunningForeignSplitDriverOwner() ??
                    $"True-split driver service '{Proto.ServiceName}' is stopped after StartService err=183 " +
                    $"(Win32ExitCode={status.dwWin32ExitCode}, Path={path}). Windows says the driver object already exists; " +
                    "close Mullvad/other VPN using mullvad-split-tunnel or reboot Windows, then retry True Split.";
                _log.Warning(
                    "[SplitTunnel] StartService returned ERROR_ALREADY_EXISTS but service is STOPPED " +
                    "(Win32ExitCode={Exit}, Path={Path}) - stale/foreign driver object; trying safe repair if service is ours",
                    status.dwWin32ExitCode, path);
                return false;
            }
            _log.Information("[SplitTunnel] StartService returned ERROR_ALREADY_EXISTS — continuing; device open will verify driver usability");
            return true;
        }
        _log.Warning("[SplitTunnel] StartService failed (err={Err}) — post-capture stands", err);
        LastFailureReason = err switch
        {
            Native.ERROR_SERVICE_MARKED_FOR_DELETE =>
                "True-split driver service is being deleted by Windows; reboot Windows, then retry True Split.",
            Native.ERROR_SERVICE_DISABLED =>
                "True-split driver service is disabled; re-run the VPNRouter installer, or in an admin console " +
                "`sc config mullvad-split-tunnel start= demand`, then retry True Split.",
            _ => $"True-split driver service could not start (StartService err={err}).",
        };
        return false;
    }

    private string? DescribeRunningForeignSplitDriverOwner()
    {
        foreach (var foreign in FindRunningForeignSplitDrivers())
            return SplitTunnelPolicy.FormatForeignSplitDriverOwner(foreign.ServiceName, foreign.DisplayName, foreign.PathName);
        return null;
    }

    private List<ForeignSplitDriverService> FindRunningForeignSplitDrivers()
    {
        var result = new List<ForeignSplitDriverService>();
#if PLATFORM_WINDOWS
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DisplayName, State, PathName FROM Win32_SystemDriver WHERE State = 'Running'");
            foreach (ManagementObject driver in searcher.Get())
            {
                string name = Convert.ToString(driver["Name"]) ?? "";
                string path = Convert.ToString(driver["PathName"]) ?? "";
                if (!SplitTunnelPolicy.IsForeignSplitDriverService(name, path))
                    continue;

                string displayName = Convert.ToString(driver["DisplayName"]) ?? "";
                result.Add(new ForeignSplitDriverService(name, displayName, path));
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "[SplitTunnel] Failed to inspect Win32_SystemDriver for foreign split driver owner");
        }
#endif
        return result;
    }

    private readonly record struct ForeignSplitDriverService(string ServiceName, string DisplayName, string PathName);

    private string? QueryServiceBinPath(IntPtr svc)
    {
        Native.QueryServiceConfig(svc, IntPtr.Zero, 0, out uint needed);
        if (needed == 0 || Marshal.GetLastWin32Error() != Native.ERROR_INSUFFICIENT_BUFFER)
            return null;
        IntPtr buf = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!Native.QueryServiceConfig(svc, buf, needed, out _)) return null;
            return Marshal.PtrToStructure<Native.QUERY_SERVICE_CONFIGW>(buf).lpBinaryPathName;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
