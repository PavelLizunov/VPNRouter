#nullable enable

// Phase 6 — Wave 31a (2026-05-18) compatibility shim. Brief:
// plans/phase6-yamldotnet-staticgen-2026-05-18.md.
//
// Vecc.YamlDotNet.Analyzers.StaticGenerator 15.1.2 omits DateTimeOffset
// from its scalar coercion table. The reflective DeserializerBuilder
// handled DateTimeOffset transparently via its default scalar resolver;
// the static replacement (`StaticDeserializerBuilder` / `StaticSerializerBuilder`)
// raises `ArgumentOutOfRangeException: Unknown type: System.DateTimeOffset`
// at deserialize time and emits an empty mapping (`{}`) at serialize
// time — silently lossy.
//
// Affected fields in the AppSettings tree:
//   - SubscriptionEntry.LastRefreshedAt (DateTimeOffset?)
//   - WgturnEntry.AddedAt              (DateTimeOffset)
//
// Both are user-facing persistence: subscription refresh time drives the
// "last refreshed N hours ago" UI badge, wgturn AddedAt drives the
// emergency-channel ComboBox sort. A lossy round-trip would erase these
// values silently on every Save, breaking the user-visible contract.
//
// Workaround: a hand-written `IYamlTypeConverter` registered via
// `WithTypeConverter(...)` on BOTH builders. Wire-format choice is
// ISO 8601 round-trip ("O" format specifier) — matches the reflective
// builder's default output and is the lossless format
// `DateTimeOffset.Parse(_, _, RoundtripKind)` round-trips byte-for-byte.
//
// Wave 31a deliberately does NOT pursue upstreaming a fix to
// Vecc.YamlDotNet.Analyzers.StaticGenerator (their issue tracker is
// fairly slow; we'd block this wave for weeks). When the upstream gets
// DateTimeOffset support in a future release, delete this file and
// the two `.WithTypeConverter(new DateTimeOffsetYamlConverter())` calls
// in SettingsLoader.cs.

using System;
using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace VPNRouter.Core.Yaml;

/// <summary>
/// Phase 6 Wave 31a — compatibility shim for
/// <see cref="DateTimeOffset"/> / <see cref="DateTimeOffset?"/> support in
/// <c>StaticSerializerBuilder</c> + <c>StaticDeserializerBuilder</c>.
/// See file header for rationale + retirement criteria.
/// </summary>
internal sealed class DateTimeOffsetYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) =>
        type == typeof(DateTimeOffset) || type == typeof(DateTimeOffset?);

    public object? ReadYaml(IParser parser, Type type)
    {
        var scalar = parser.Consume<Scalar>();
        if (string.IsNullOrEmpty(scalar.Value))
        {
            // Empty scalar = null for nullable receivers. Non-nullable
            // DateTimeOffset receivers (e.g. WgturnEntry.AddedAt) get
            // DateTimeOffset.MinValue as a sentinel — matches the
            // reflective default-construction behaviour we replaced.
            return type == typeof(DateTimeOffset?)
                ? null
                : default(DateTimeOffset);
        }
        return DateTimeOffset.Parse(
            scalar.Value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type)
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
