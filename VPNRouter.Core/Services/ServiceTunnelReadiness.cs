using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;

namespace VPNRouter.Core.Services;

internal static class ServiceTunnelReadiness
{
    // The pinned core starts its Clash listener after routes, endpoints and inbounds finish starting.
    // Pin the accepted TCP connection to the replacement PID before sending its control credential.
    internal static async Task<bool> WaitAsync(IProcessHandle child, string configJson, CancellationToken ct)
    {
        using var config = JsonDocument.Parse(configJson);
        var clash = config.RootElement.GetProperty("experimental").GetProperty("clash_api");
        var address = clash.GetProperty("external_controller").GetString();
        if (!Uri.TryCreate("http://" + address, UriKind.Absolute, out var controller)) return false;
        if (controller.UserInfo.Length != 0 || controller.AbsolutePath != "/" ||
            controller.Query.Length != 0 || controller.Fragment.Length != 0) return false;

        var host = controller.IdnHost.Trim('[', ']');
        if (host == "0.0.0.0")
            host = "127.0.0.1";
        else if (host == "::")
            host = "::1";

        var localAddresses = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address).ToHashSet();
        var resolved = IPAddress.TryParse(host, out var literal) ? new[] { literal } :
            await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        var targets = resolved.Where(a => IPAddress.IsLoopback(a) || localAddresses.Contains(a)).ToArray();
        if (targets.Length == 0) return false;
        var netstat = Path.Combine(Environment.SystemDirectory, "netstat.exe");
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
            ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(targets, controller.Port, token).ConfigureAwait(false);
                    var table = await new ProcessRunner().RunAsync(
                        new ProcessRequest(netstat, ["-ano"],
                            WorkingDirectory: Environment.SystemDirectory, Timeout: TimeSpan.FromSeconds(2)), token)
                        .ConfigureAwait(false);
                    if (table.ExitCode != 0 || table.TimedOut || child.QueryHasExited() != false ||
                        !OwnsConnection(table.Stdout, child.Pid, (IPEndPoint)socket.RemoteEndPoint!, (IPEndPoint)socket.LocalEndPoint!))
                        throw new IOException("The control connection does not belong to the replacement process.");
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        using var client = new HttpClient(handler) { MaxResponseContentBufferSize = 4096 };
        if (clash.TryGetProperty("secret", out var secret) && secret.GetString() is { Length: > 0 } credential)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            while (child.QueryHasExited() == false)
            {
                deadline.Token.ThrowIfCancellationRequested();
                try
                {
                    using var response = await client.GetAsync("http://localhost/version", deadline.Token).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false));
                        if (json.RootElement.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(version.GetString()))
                            return child.QueryHasExited() == false;
                    }
                }
                catch (HttpRequestException) { }
                catch (JsonException) { }
                await Task.Delay(150, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        return false;
    }

    internal static bool OwnsConnection(string table, int pid, IPEndPoint server, IPEndPoint client)
    {
        static bool SameEndpoint(IPEndPoint a, IPEndPoint b) =>
            a.Port == b.Port && a.Address.MapToIPv6().Equals(b.Address.MapToIPv6());

        foreach (var line in table.Split('\n'))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 5 && fields[0] == "TCP" &&
                int.TryParse(fields[4], out var owner) && owner == pid &&
                IPEndPoint.TryParse(fields[1], out var local) && SameEndpoint(local, server) &&
                IPEndPoint.TryParse(fields[2], out var remote) && SameEndpoint(remote, client))
                return true;
        }
        return false;
    }
}
