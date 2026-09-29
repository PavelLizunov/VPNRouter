#nullable enable

using VPNRouter.Core.Models;
using YamlDotNet.Serialization;

namespace VPNRouter.Core.Yaml;

[YamlStaticContext]
[YamlSerializable(typeof(AppConfig))]
[YamlSerializable(typeof(AppSettings))]
[YamlSerializable(typeof(AwgConfig))]
[YamlSerializable(typeof(CustomCategory))]
[YamlSerializable(typeof(CustomConfigEntry))]
[YamlSerializable(typeof(CustomDirectRule))]
[YamlSerializable(typeof(CustomRule))]
[YamlSerializable(typeof(DnsSettings))]
[YamlSerializable(typeof(MonitoringSettings))]
[YamlSerializable(typeof(ProfileSource))]
[YamlSerializable(typeof(SingBoxSettings))]
[YamlSerializable(typeof(SubscriptionEntry))]
[YamlSerializable(typeof(TunSettings))]
[YamlSerializable(typeof(UpdateSettings))]
[YamlSerializable(typeof(UserFreeSource))]
[YamlSerializable(typeof(VlessConfig))]
[YamlSerializable(typeof(VlessRealityConfig))]
[YamlSerializable(typeof(VlessServerEntry))]
[YamlSerializable(typeof(VlessTlsConfig))]
[YamlSerializable(typeof(VlessTransportConfig))]
public partial class YamlStaticContext
{
}
