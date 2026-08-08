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
builder.Services.AddSingleton<FixedRateLimiter>();
builder.Services.AddHostedService<UdpEchoService>();
var app = builder.Build();
app.UseWebSockets();

app.MapGet("/health", (HttpContext context, FixedRateLimiter rate) => RateLimited(context, rate) ? Results.StatusCode(429) : Results.Text("ok", "text/plain"));
app.MapGet("/blob", (HttpContext context, FixedRateLimiter rate) => RateLimited(context, rate) ? Results.StatusCode(429) : Results.Bytes(new byte[LoadTestContract.BlobBytes], "application/octet-stream"));
app.MapGet("/browser", (HttpContext context, FixedRateLimiter rate) => RateLimited(context, rate) ? Results.StatusCode(429) : Results.Content(BrowserPage.Html, "text/html"));
app.Map("/ws", async context =>
{
    var rate = context.RequestServices.GetRequiredService<FixedRateLimiter>();
    if (RateLimited(context, rate)) { context.Response.StatusCode = StatusCodes.Status429TooManyRequests; return; }
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    using var session = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    session.CancelAfter(TimeSpan.FromMinutes(10));
    var buffer = new byte[64];
    try
    {
        while (true)
        {
            var received = await socket.ReceiveAsync(buffer, session.Token);
            if (received.MessageType == WebSocketMessageType.Close) break;
            if (received.MessageType != WebSocketMessageType.Binary || received.Count != buffer.Length || !received.EndOfMessage) break;
            if (RateLimited(context, rate)) { await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "rate", session.Token); break; }
            await socket.SendAsync(buffer, WebSocketMessageType.Binary, true, session.Token);
        }
    }
    catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested) { }
});
await app.RunAsync();

static bool RateLimited(HttpContext context, FixedRateLimiter rate) =>
    context.Connection.RemoteIpAddress is not { } source || !rate.TryTake(source, DateTimeOffset.UtcNow);

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
const out=document.querySelector('#out'), state={fetchOk:0,fetchFail:0,wsOk:0,wsFail:0,done:false};
const show=()=>out.textContent=JSON.stringify(state);
let busy=false,stopped=false,burstTimer,stopTimer;const sockets=[],sendTimers=[];
async function burst(){if(busy||stopped)return;busy=true;try{await Promise.all([...Array(32)].map(async(_,i)=>{try{let r=await fetch('/blob?run='+Date.now()+'-'+i,{cache:'no-store'});if((await r.arrayBuffer()).byteLength===65536)state.fetchOk++;else state.fetchFail++;}catch{state.fetchFail++;}}));}finally{busy=false;show();}}
function stop(){if(stopped)return;stopped=true;clearInterval(burstTimer);clearTimeout(stopTimer);sendTimers.forEach(clearInterval);sockets.forEach(ws=>ws.close());state.done=true;show();}
for(let i=0;i<4;i++){let ws=new WebSocket((location.protocol==='https:'?'wss://':'ws://')+location.host+'/ws');sockets.push(ws);ws.binaryType='arraybuffer';ws.onmessage=e=>{if(e.data.byteLength===64)state.wsOk++;else state.wsFail++;show()};ws.onerror=()=>{state.wsFail++;show()};ws.onclose=()=>{if(!stopped){state.wsFail++;show()}};sendTimers.push(setInterval(()=>{if(!stopped&&ws.readyState===1)ws.send(new Uint8Array(64));},1000));}
burst();burstTimer=setInterval(burst,5000);stopTimer=setTimeout(stop,600000);</script>
""";
}
