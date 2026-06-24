using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace VPNRouter.Core.Models;

/// <summary>A saved custom sing-box config entry.</summary>
public class CustomConfigEntry
{
    /// <summary>Display name (derived from filename on import, e.g. "brat-pc").</summary>
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Path to the ProgramData copy (e.g. %ProgramData%\VPNRouter\config\custom-brat-pc.json).</summary>
    [YamlMember(Alias = "path")]
    public string Path { get; set; } = string.Empty;
}
