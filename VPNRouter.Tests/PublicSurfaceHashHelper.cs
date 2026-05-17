#nullable enable
using System;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VPNRouter.Tests;

/// <summary>
/// Reusable helper for god-class characterization snapshot tests
/// (Phase 2B MainWindowViewModel split, Phase 2C AndroidApp split).
/// Enumerates a type's public + internal surface via reflection, normalizes
/// each member to a stable description string, sorts deterministically,
/// JSON-serializes, then SHA-256 hashes the bytes.
///
/// <para><strong>Why hash?</strong> A god-class split moves members between
/// partials. The class identity stays the same; what we want to verify is
/// that no method / property / event / field accidentally drifted in
/// signature or got dropped. A surface hash captures that invariant in
/// one diff-friendly string.</para>
///
/// <para><strong>What's included</strong>:</para>
/// <list type="bullet">
///   <item>Public + non-public instance + static methods (excluding accessors)</item>
///   <item>Public + non-public properties (by name + property type)</item>
///   <item>Public + non-public fields (by name + field type)</item>
///   <item>Public + non-public events (by name + handler type)</item>
///   <item>Constructors (by parameter type list)</item>
/// </list>
///
/// <para><strong>What's excluded</strong>:</para>
/// <list type="bullet">
///   <item>Compiler-generated members (names starting with <c>&lt;</c>)</item>
///   <item>Property/event accessor methods (<c>get_</c>, <c>set_</c>, <c>add_</c>, <c>remove_</c>) —
///   already captured via the PropertyInfo / EventInfo entries</item>
///   <item>Members inherited from <see cref="object"/> — these never change with a split</item>
/// </list>
/// </summary>
internal static class PublicSurfaceHashHelper
{
    private const BindingFlags AllMembersBindings =
        BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.DeclaredOnly;

    /// <summary>
    /// Compute the lowercase-hex SHA-256 of the type's public surface
    /// (see class docs for inclusion rules).
    /// </summary>
    public static string Compute(Type t)
    {
        var descriptions = t.GetMembers(AllMembersBindings)
            .Where(IsRelevant)
            .Select(Describe)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        var json = JsonSerializer.Serialize(descriptions);
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Detailed dump of every member description — useful when a hash mismatch
    /// reveals drift and you want to see exactly which member changed.
    /// </summary>
    public static string[] DumpMembers(Type t)
    {
        return t.GetMembers(AllMembersBindings)
            .Where(IsRelevant)
            .Select(Describe)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsRelevant(MemberInfo m)
    {
        // Skip compiler-generated members (lambdas, closures, async state machines)
        if (m.Name.Contains('<')) return false;

        // Skip property/event accessor methods — covered by their PropertyInfo/EventInfo entries
        if (m is MethodInfo mi)
        {
            if (mi.Name.StartsWith("get_", StringComparison.Ordinal)) return false;
            if (mi.Name.StartsWith("set_", StringComparison.Ordinal)) return false;
            if (mi.Name.StartsWith("add_", StringComparison.Ordinal)) return false;
            if (mi.Name.StartsWith("remove_", StringComparison.Ordinal)) return false;
        }

        // Skip object's universal members — they never change with a split
        if (m.DeclaringType == typeof(object)) return false;

        return true;
    }

    private static string Describe(MemberInfo m) => m switch
    {
        PropertyInfo p =>
            $"P:{p.Name}:{p.PropertyType.FullName}",
        FieldInfo f =>
            $"F:{f.Name}:{f.FieldType.FullName}",
        MethodInfo mi =>
            $"M:{mi.Name}:{mi.ReturnType.FullName}:({JoinParams(mi.GetParameters())})",
        EventInfo e =>
            $"E:{e.Name}:{e.EventHandlerType?.FullName}",
        ConstructorInfo c =>
            $"C:.ctor:({JoinParams(c.GetParameters())})",
        _ => $"?:{m.MemberType}:{m.Name}"
    };

    private static string JoinParams(ParameterInfo[] ps) =>
        string.Join(",", ps.Select(p => p.ParameterType.FullName ?? p.ParameterType.Name));
}
