#nullable enable

using System.Collections.Generic;
using System.Text.Json.Serialization;
using VPNRouter.Core.Models;

namespace VPNRouter.Android.Json;

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(CustomCategory))]
[JsonSerializable(typeof(Dictionary<string, AndroidStorage.ServerTestResultDto>))]
[JsonSerializable(typeof(List<CustomCategory>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(AndroidStorage.ServerTestResultDto))]
internal sealed partial class AndroidJsonContext : JsonSerializerContext
{
}
