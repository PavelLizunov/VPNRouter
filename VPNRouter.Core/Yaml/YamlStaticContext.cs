#nullable enable

// Phase 6 — Wave 31a YamlDotNet StaticDeserializerBuilder swap (2026-05-18).
// Brief: plans/phase6-yamldotnet-staticgen-2026-05-18.md
//
// Why: Wave 30 (NativeAOT readiness audit, plans/phase6-nativeaot-readiness-
// 2026-05-18.md) found that the two `new DeserializerBuilder()` /
// `new SerializerBuilder()` ctor sites in SettingsLoader.cs are the only
// remaining IL3050 (dynamic-code) warnings rooted in the YamlDotNet
// reflective path. Vecc.YamlDotNet.Analyzers.StaticGenerator 15.1.2 ships
// a Roslyn source generator that consumes this partial class + the
// `[YamlSerializable(typeof(T))]` attributes below and emits a sibling
// `YamlStaticContext.g.cs` with a fully-populated static type table.
// Build-time generation means `StaticDeserializerBuilder` / `StaticSerializerBuilder`
// look up `IObjectFactory` / `ITypeInspector` from compile-time-known
// tables instead of reflection — AOT-clean.
//
// Composition rule: the source generator walks every type registered below
// AND recursively discovers nested DTOs by inspecting their properties
// (`ClassSyntaxReceiver.CheckForSupportedGeneric`). For correctness AND
// for explicit-is-better-than-implicit reviewability, the user-defined
// DTO classes (not the collection wrappers around them) are listed
// individually below. That way:
//   1. Adding a new DTO branch surfaces as a missing-type error from the
//      generator instead of a runtime "type not registered" exception.
//   2. The list doubles as documentation for the persisted YAML surface.
//   3. Refactoring a DTO out of the persistence path (rare but happens)
//      surfaces here too — orphan registrations get caught by the
//      build-time "type referenced but never serialized" warning.
//
// Collections (List<T>, Dictionary<K,V>) are deliberately NOT registered
// explicitly with `[YamlSerializable]` here. The Vecc 15.1.2 analyzer
// crashes with `IndexOutOfRangeException` when given an explicit
// `[YamlSerializable(typeof(Dictionary<string, List<string>>))]`-style
// nested-generic registration — reproduced isolated against the
// minimal-test harness in /tmp/yamltest. The analyzer's transitive
// discovery handles collection types correctly when they appear as a
// property type on a registered DTO, so each `[YamlSerializable]`
// attribute targets a leaf DTO class only.

using VPNRouter.Core.Models;
using YamlDotNet.Serialization;

namespace VPNRouter.Core.Yaml;

/// <summary>
/// Phase 6 — Wave 31a: the single <see cref="YamlStaticContext"/> for
/// VPNRouter.Core's persistent <c>config.yaml</c> surface.
///
/// <para>Source-generated at build time by
/// <c>Vecc.YamlDotNet.Analyzers.StaticGenerator</c>. The generator inspects
/// every <c>[YamlSerializable(typeof(T))]</c> attribute below and emits a
/// sibling <c>YamlStaticContext.g.cs</c> that:
/// <list type="bullet">
///   <item>Implements the <c>StaticContext</c> base class with concrete
///   factory + inspector implementations.</item>
///   <item>Wires per-type <c>IYamlSerializable</c> handlers so
///   <see cref="StaticDeserializerBuilder.Build"/> + <see cref="StaticSerializerBuilder.Build"/>
///   don't touch <see cref="System.Reflection"/>.</item>
/// </list>
/// Result: registered types serialize without reflection, satisfying the
/// AOT trimmer's "no dynamic code" guarantee that Wave 30 audited for.</para>
///
/// <para>Maintenance: when a new DTO branch lands in <see cref="AppSettings"/>,
/// add a matching <c>[YamlSerializable(typeof(T))]</c> line here. Order is
/// alphabetical by type name for deterministic generated-file diffs.</para>
///
/// <para><b>Visibility</b>: declared <c>public partial</c> because the
/// analyzer-emitted counterpart is hard-coded to <c>public partial</c>
/// (see <c>StaticContextFile.Write</c> in Vecc.YamlDotNet.Analyzers
/// .StaticGenerator 15.1.2 — it emits
/// <c>public partial class {ContextName} : YamlDotNet.Serialization.StaticContext</c>).
/// C# requires matching accessibility across all partial declarations,
/// so the user-authored half must be public too. Despite the public
/// declaration this type is meant to be constructed only by
/// <see cref="VPNRouter.Core.Services.SettingsLoader"/> — external
/// callers continue to go through <see cref="VPNRouter.Core.Services.ISettingsStore"/>.</para>
///
/// <para><b>Base class</b>: the analyzer adds the
/// <c>: YamlDotNet.Serialization.StaticContext</c> base in the
/// generated half, so we deliberately leave it off here. Adding it
/// twice would be a CS0263 (partial declarations of a type must not
/// specify different base classes) compile error.</para>
/// </summary>
[YamlStaticContext]
[YamlSerializable(typeof(AppConfig))]
[YamlSerializable(typeof(AppSettings))]
[YamlSerializable(typeof(CustomCategory))]
[YamlSerializable(typeof(CustomConfigEntry))]
[YamlSerializable(typeof(CustomDirectRule))]
[YamlSerializable(typeof(CustomRule))]
[YamlSerializable(typeof(DnsSettings))]
[YamlSerializable(typeof(EmergencyChannelSettings))]
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
[YamlSerializable(typeof(WgturnEntry))]
public partial class YamlStaticContext
{
}
