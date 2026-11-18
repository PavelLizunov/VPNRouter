using System.Collections.Concurrent;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace VPNRouter.Tests.Fakes;

internal sealed class CapturingSink : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _events = new();
    public IReadOnlyList<LogEvent> Events => _events.ToArray();

    public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
    public void Clear() => _events.Clear();
    public Logger CreateLogger() => new LoggerConfiguration()
        .MinimumLevel.Verbose().WriteTo.Sink(this).CreateLogger();

    public void AssertExcludes(params string[] privateValues)
    {
        Assert.All(Events, entry => Assert.Null(entry.Exception));
        AssertNoPrivateValues(privateValues);
    }

    public void AssertNoPrivateValues(params string[] privateValues)
    {
        var events = Events;
        Assert.NotEmpty(events);
        foreach (var entry in events)
        {
            foreach (var value in privateValues)
            {
                Assert.DoesNotContain(value, entry.RenderMessage(), StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(value, entry.Exception?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
                foreach (var property in entry.Properties)
                {
                    Assert.DoesNotContain(value, property.Key, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain(value, property.Value.ToString(), StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }
}
