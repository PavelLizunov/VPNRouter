#nullable enable
using System;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VPNRouter.Tests;

internal static class PublicSurfaceHashHelper
{
    private const BindingFlags AllMembersBindings =
        BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.DeclaredOnly;

    public static string Compute(Type t)
    {
        var descriptions = t.GetMembers(AllMembersBindings)
            .Where(IsRelevant)
            .Select(Describe)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        var json = JsonSerializer.Serialize(descriptions);
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexStringLower(hashBytes);
    }

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
        if (m.Name.Contains('<')) return false;

        if (m is MethodInfo mi)
        {
            if (mi.Name.StartsWith("get_", StringComparison.Ordinal)) return false;
            if (mi.Name.StartsWith("set_", StringComparison.Ordinal)) return false;
            if (mi.Name.StartsWith("add_", StringComparison.Ordinal)) return false;
            if (mi.Name.StartsWith("remove_", StringComparison.Ordinal)) return false;
        }

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
