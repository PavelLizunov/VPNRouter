#nullable enable

using System;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VPNRouter.Tools.LoadTest.Protocol;

var secret = Environment.GetEnvironmentVariable("VPNROUTER_LOADTEST_SECRET");
if (string.IsNullOrWhiteSpace(secret)) throw new InvalidOperationException("VPNROUTER_LOADTEST_SECRET is required.");

var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders();
builder.Services.AddSingleton(new UdpEchoProcessor(System.Text.Encoding.UTF8.GetBytes(secret)));
builder.Services.AddHostedService<UdpEchoService>();
var app = builder.Build();
app.UseWebSockets();

app.MapGet("/health", () => Results.Text("ok", "text/plain"));
app.MapGet("/blob", () => Results.Bytes(new byte[LoadTestContract.BlobBytes], "application/octet-stream"));
app.MapGet("/browser", () => Results.Content(BrowserPage.Html, "text/html"));
app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var buffer = new byte[64];
    while (true)
    {
        var received = await socket.ReceiveAsync(buffer, context.RequestAborted);
        if (received.MessageType == WebSocketMessageType.Close) break;
        if (received.MessageType != WebSocketMessageType.Binary || received.Count != buffer.Length || !received.EndOfMessage) break;
        await socket.SendAsync(buffer, WebSocketMessageType.Binary, true, context.RequestAborted);
    }
});
await app.RunAsync();

sealed class UdpEchoService(UdpEchoProcessor processor) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, LoadTestContract.UdpPort));
        while (!stoppingToken.IsCancellationRequested)
        {
            var packet = await udp.ReceiveAsync(stoppingToken);
            var disposition = processor.Process(packet.RemoteEndPoint.Address, packet.Buffer, DateTimeOffset.UtcNow, out var response);
            if (disposition == UdpEchoDisposition.Echo && response is not null && response.Length <= packet.Buffer.Length)
                await udp.SendAsync(response, packet.RemoteEndPoint, stoppingToken);
        }
    }
}

static class BrowserPage
{
    public const string Html = """
<!doctype html><meta charset="utf-8"><title>VPNRouter BrowserBurst</title><pre id=out>starting</pre><script>
const out=document.querySelector('#out'), state={fetchOk:0,fetchFail:0,wsOk:0,wsFail:0};
const show=()=>out.textContent=JSON.stringify(state);
async function burst(){await Promise.all([...Array(32)].map(async(_,i)=>{try{let r=await fetch('/blob?run='+Date.now()+'-'+i,{cache:'no-store'});if((await r.arrayBuffer()).byteLength===65536)state.fetchOk++;else state.fetchFail++;}catch{state.fetchFail++;}}));show();}
for(let i=0;i<4;i++){let ws=new WebSocket((location.protocol==='https:'?'wss://':'ws://')+location.host+'/ws');ws.binaryType='arraybuffer';ws.onmessage=e=>{if(e.data.byteLength===64)state.wsOk++;else state.wsFail++;show()};ws.onerror=()=>{state.wsFail++;show()};setInterval(()=>{if(ws.readyState===1)ws.send(new Uint8Array(64));},1000);}
burst();setInterval(burst,5000);</script>
""";
}
