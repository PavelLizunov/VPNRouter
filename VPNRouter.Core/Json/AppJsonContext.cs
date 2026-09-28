#nullable enable

using System.Collections.Generic;
using System.Text.Json.Serialization;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.FreeConfigs;
using VPNRouter.Core.Services.UpdateSources;

namespace VPNRouter.Core.Json;

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    MaxDepth = 32,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(CacheRecovery.SchemaProbe))]
[JsonSerializable(typeof(ClashSelectProxyDto))]
[JsonSerializable(typeof(ClashSetConfigDto))]
[JsonSerializable(typeof(ClashSingBoxApi.ConnectionsDto))]
[JsonSerializable(typeof(ClashSingBoxApi.ProxiesEnvelopeDto))]
[JsonSerializable(typeof(ClashSingBoxApi.VersionDto))]
[JsonSerializable(typeof(ConfigShareDocument))]
[JsonSerializable(typeof(CustomRule))]
[JsonSerializable(typeof(FreeConfigCache.CacheFile))]
[JsonSerializable(typeof(GitHubAsset))]
[JsonSerializable(typeof(GitHubRelease))]
[JsonSerializable(typeof(GitHubRelease[]))]
[JsonSerializable(typeof(LaunchFailureCounter.State))]
[JsonSerializable(typeof(List<CustomRule>))]
[JsonSerializable(typeof(List<SubscriptionEntry>))]
[JsonSerializable(typeof(List<VlessServerEntry>))]
[JsonSerializable(typeof(ProcessRule))]
[JsonSerializable(typeof(Profile))]
[JsonSerializable(typeof(ProfileCacheFile))]
[JsonSerializable(typeof(ProfileCollection))]
[JsonSerializable(typeof(List<Services.CanaryTarget>))]
[JsonSerializable(typeof(Services.ServerHealthFileDto))]
[JsonSerializable(typeof(SingBoxConfig))]
[JsonSerializable(typeof(SubscriptionEntry))]
[JsonSerializable(typeof(VlessServerEntry))]
internal sealed partial class AppJsonContext : JsonSerializerContext
{
}
