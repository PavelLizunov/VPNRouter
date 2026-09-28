#nullable enable

using System;
using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace VPNRouter.Core.Yaml;

internal sealed class DateTimeOffsetYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) =>
        type == typeof(DateTimeOffset) || type == typeof(DateTimeOffset?);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        var scalar = parser.Consume<Scalar>();
        if (string.IsNullOrEmpty(scalar.Value))
        {
            return type == typeof(DateTimeOffset?)
                ? null
                : default(DateTimeOffset);
        }
        return DateTimeOffset.Parse(
            scalar.Value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        if (value == null)
        {
            emitter.Emit(new Scalar(string.Empty));
            return;
        }
        var dto = (DateTimeOffset)value;
        emitter.Emit(new Scalar(dto.ToString("O", CultureInfo.InvariantCulture)));
    }
}
