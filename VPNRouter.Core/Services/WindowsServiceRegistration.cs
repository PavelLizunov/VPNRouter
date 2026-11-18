using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Native = VPNRouter.Core.Services.SplitTunnelDriverInterop;

namespace VPNRouter.Core.Services;

internal sealed record ServiceConfig(string Image, uint Type, uint Start);

internal interface IWindowsRegistration : IDisposable
{
    ServiceConfig Config { get; }
    bool IsActive { get; }
    object? ReadValue(string name);
    void WriteChoice(string name, string[]? value);
    void Configure(string image, uint start);
    void Stop();
    void Start();
    void Delete();
}

// Keep the SCM object open across verification and mutation. A deleted service name
// cannot be reassigned to a different registration while this handle exists.
[SupportedOSPlatform("windows")]
internal sealed class WindowsServiceRegistration(string name, IntPtr handle) : IWindowsRegistration
{
    internal static IWindowsRegistration? Open(string name, bool writable) =>
        OpenWithAccess(name, writable ? 2u | 16u | 32u | 0x10000u : 0u);

    internal static IWindowsRegistration? OpenWithAccess(string name, uint additionalAccess)
    {
        var manager = Native.OpenSCManager(null, null, 1); // SC_MANAGER_CONNECT
        if (manager == IntPtr.Zero) throw new Win32Exception();
        try
        {
            const uint query = 1 | 4; // QUERY_CONFIG | QUERY_STATUS
            var handle = Native.OpenService(manager, name, query | additionalAccess);
            if (handle != IntPtr.Zero) return new WindowsServiceRegistration(name, handle);
            var error = Marshal.GetLastWin32Error();
            if (error == Native.ERROR_SERVICE_DOES_NOT_EXIST) return null;
            throw new Win32Exception(error);
        }
        finally { Native.CloseServiceHandle(manager); }
    }

    public ServiceConfig Config
    {
        get
        {
            Native.QueryServiceConfig(handle, IntPtr.Zero, 0, out var size);
            if (size == 0) throw new Win32Exception();
            var buffer = Marshal.AllocHGlobal(checked((int)size));
            try
            {
                if (!Native.QueryServiceConfig(handle, buffer, size, out _)) throw new Win32Exception();
                var config = Marshal.PtrToStructure<Native.QUERY_SERVICE_CONFIGW>(buffer);
                return new(config.lpBinaryPathName ?? "", config.dwServiceType, config.dwStartType);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    public bool IsActive
    {
        get
        {
            if (!Native.QueryServiceStatus(handle, out var status)) throw new Win32Exception();
            return status.dwCurrentState != Native.SERVICE_STOPPED;
        }
    }

    private RegistryKey OpenKey(bool writable) =>
        Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name, writable)
        ?? throw new IOException("Could not read the Windows service registration.");

    public object? ReadValue(string key)
    {
        using var registration = OpenKey(false);
        return registration.GetValue(key, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    public void WriteChoice(string key, string[]? value)
    {
        using var registration = OpenKey(true);
        if (value == null) registration.DeleteValue(key, false);
        else registration.SetValue(key, value, RegistryValueKind.MultiString);
        registration.Flush();
    }

    public void Configure(string image, uint start)
    {
        if (!Native.ChangeServiceConfig(handle, Native.SERVICE_NO_CHANGE, start, Native.SERVICE_NO_CHANGE,
                image, null, IntPtr.Zero, null, null, null, null)) throw new Win32Exception();
    }

    public void Stop()
    {
        if (!IsActive) return;
        if (!ControlService(handle, 1, out _))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1062 && error != 1061) throw new Win32Exception(error);
        }
        if (!SpinWait.SpinUntil(() => !IsActive, TimeSpan.FromSeconds(5)))
            throw new TimeoutException("Timed out waiting for the Windows service to stop.");
    }

    public void Start()
    {
        if (!Native.StartService(handle, 0, null))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != Native.ERROR_SERVICE_ALREADY_RUNNING) throw new Win32Exception(error);
        }
    }

    public void Delete()
    {
        if (!DeleteService(handle))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != Native.ERROR_SERVICE_MARKED_FOR_DELETE) throw new Win32Exception(error);
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(IntPtr service, uint control, out Native.SERVICE_STATUS status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DeleteService(IntPtr service);

    public void Dispose() => Native.CloseServiceHandle(handle);
}
