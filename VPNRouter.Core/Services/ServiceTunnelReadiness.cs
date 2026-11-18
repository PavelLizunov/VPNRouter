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
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        var (controller, credential) = ClashSingBoxApi.ReadLocalController(configJson);
        var netstat = Path.Combine(Environment.SystemDirectory, "netstat.exe");
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
            ConnectCallback = async (_, token) =>
            {
                var socket = await ClashSingBoxApi.OpenControlSocketAsync(controller, token).ConfigureAwait(false);
                try
                {
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
        if (!string.IsNullOrEmpty(credential))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
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
