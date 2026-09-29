#nullable enable
using System.Runtime.InteropServices;
#if PLATFORM_WINDOWS
#endif
using Native = VPNRouter.Core.Services.SplitTunnelDriverInterop;
using Proto = VPNRouter.Core.Services.SplitTunnelDriverProtocol;

namespace VPNRouter.Core.Services;

internal sealed partial class SplitTunnelDriverManager
{
    private bool EnsureSublayersLocked()
    {
        if (_sublayersCreated) return true;
        uint status = Native.FwpmEngineOpen0(null, Native.RPC_C_AUTHN_WINNT, IntPtr.Zero, IntPtr.Zero, out IntPtr engine);
        if (status != 0)
        {
            LastFailureReason = $"Windows Filtering Platform is unavailable (FwpmEngineOpen0 status=0x{status:X8}).";
            _log.Warning("[SplitTunnel] FwpmEngineOpen0 failed (0x{S:X8}) — BFE stopped? post-capture stands", status);
            return false;
        }
        try
        {
            if (!AddSublayerLocked(engine, Proto.SublayerBaseline, Proto.SublayerWeightBaseline, "VPNRouter split baseline")) return false;
            if (!AddSublayerLocked(engine, Proto.SublayerDns, Proto.SublayerWeightDns, "VPNRouter split dns")) return false;
            _sublayersCreated = true;
            return true;
        }
        finally { Native.FwpmEngineClose0(engine); }
    }

    private bool AddSublayerLocked(IntPtr engine, Guid key, ushort weight, string name)
    {
        IntPtr namePtr = Marshal.StringToHGlobalUni(name);
        try
        {
            var sub = new Native.FWPM_SUBLAYER0 { subLayerKey = key, weight = weight };
            sub.displayData.name = namePtr;
            uint status = Native.FwpmSubLayerAdd0(engine, ref sub, IntPtr.Zero);
            if (status != 0 && status != Native.FWP_E_ALREADY_EXISTS)
            {
                LastFailureReason = $"True-split WFP sublayer '{name}' could not be created (status=0x{status:X8}).";
                _log.Warning("[SplitTunnel] FwpmSubLayerAdd0({Name}) failed (0x{S:X8})", name, status);
                return false;
            }
            return true;
        }
        finally { Marshal.FreeHGlobal(namePtr); }
    }

    private void DeleteSublayersLocked()
    {
        if (!_sublayersCreated) return;
        uint status = Native.FwpmEngineOpen0(null, Native.RPC_C_AUTHN_WINNT, IntPtr.Zero, IntPtr.Zero, out IntPtr engine);
        if (status != 0) { _log.Debug("[SplitTunnel] FwpmEngineOpen0 (delete) failed 0x{S:X8}", status); return; }
        try
        {
            Guid baseline = Proto.SublayerBaseline, dns = Proto.SublayerDns;
            uint s1 = Native.FwpmSubLayerDeleteByKey0(engine, ref baseline);
            uint s2 = Native.FwpmSubLayerDeleteByKey0(engine, ref dns);
            if (s1 != 0) _log.Debug("[SplitTunnel] delete baseline sublayer → 0x{S:X8} (tolerated)", s1);
            if (s2 != 0) _log.Debug("[SplitTunnel] delete dns sublayer → 0x{S:X8} (tolerated)", s2);
            _sublayersCreated = false;
        }
        finally { Native.FwpmEngineClose0(engine); }
    }
}
