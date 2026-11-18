using VPNRouter.Core.Services;

namespace VPNRouter.Tests.Fakes;

internal sealed class FakeRegistration(ServiceConfig config) : IWindowsRegistration
{
    public ServiceConfig Config { get; private set; } = config;
    public bool IsActive { get; set; }
    public int Pid { get; set; }
    public (uint State, int Pid) ProcessStatus => IsActive ? (4u, Pid) : (1u, 0);
    public Dictionary<string, object> Values { get; } = [];
    public Action? OnStop { get; init; }
    public Action? OnDelete { get; init; }
    public int Changes { get; private set; }
    public object? ReadValue(string key) => Values.GetValueOrDefault(key);
    public void WriteChoice(string key, string[]? value)
    {
        if (value == null) Values.Remove(key);
        else Values[key] = value;
    }
    public void Configure(string image, uint start)
    {
        Config = Config with { Image = image, Start = start };
        Changes++;
    }
    public void Start() => IsActive = true;
    public void Stop() { IsActive = false; OnStop?.Invoke(); }
    public void Delete() => OnDelete?.Invoke();
    public void Dispose() { }
}
