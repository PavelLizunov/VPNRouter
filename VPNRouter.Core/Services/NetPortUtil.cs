using System.Net;
using System.Net.Sockets;

namespace VPNRouter.Core.Services;

/// <summary>
/// Shared loopback ephemeral-port helper (plan T2-C). The byte-identical
/// FindFreePort previously lived privately in FreeConfigDeepVerifier and
/// VlessDeepVerifier. Kept internal (VPNRouter.Tests has InternalsVisibleTo) so
/// the consolidation widens no public surface. (AndroidFreeConfigDeepVerifier
/// keeps its own copy — internal does not cross to the Android assembly, which
/// builds under the separate .NET 10 toolchain.)
/// </summary>
internal static class NetPortUtil
{
    /// <summary>Find a random free TCP port on loopback.</summary>
    public static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
