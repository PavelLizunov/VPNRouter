#nullable enable

using System.Text.Json.Serialization;
using VPNRouter.CLI.Commands;

namespace VPNRouter.CLI.Helpers;

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true)]
[JsonSerializable(typeof(RunState))]
internal sealed partial class CliJsonContext : JsonSerializerContext
{
}
