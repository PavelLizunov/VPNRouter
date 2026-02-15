using Newtonsoft.Json;

namespace VPNRouter.CLI.Commands;

/// <summary>
/// Persists running state so stop/status commands can find the sing-box PID
/// even from a different CLI invocation.
/// </summary>
public class RunState
{
    public string ActiveProfile { get; set; } = string.Empty;
    public int SingBoxPid { get; set; }
    public DateTime StartedAt { get; set; }
    public List<string> ProcessNames { get; set; } = new();
}

public static class StateFile
{
    private static readonly string Path =
        System.IO.Path.Combine(
            Environment.ExpandEnvironmentVariables(@"%ProgramData%\VPNRouter"),
            "state.json");

    public static void Write(RunState state)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonConvert.SerializeObject(state, Formatting.Indented));
    }

    public static RunState? Read()
    {
        if (!File.Exists(Path)) return null;
        try
        {
            return JsonConvert.DeserializeObject<RunState>(File.ReadAllText(Path));
        }
        catch
        {
            return null;
        }
    }

    public static void Clear()
    {
        if (File.Exists(Path))
            File.Delete(Path);
    }
}
