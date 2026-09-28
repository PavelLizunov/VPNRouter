using Spectre.Console.Cli;
using VPNRouter.Core.Services;

namespace VPNRouter.CLI;

internal sealed class SettingsAwareTypeRegistrar : ITypeRegistrar
{
    private readonly Dictionary<Type, object> _instances = new();
    private readonly Dictionary<Type, Func<object>> _factories = new();

    public void Register(Type service, Type implementation)
        => _factories[service] = () => SettingsAwareTypeResolver.Construct(implementation, _instances, _factories)!;

    public void RegisterInstance(Type service, object implementation)
        => _instances[service] = implementation;

    public void RegisterLazy(Type service, Func<object> factory)
        => _factories[service] = factory;

    public ITypeResolver Build() => new SettingsAwareTypeResolver(_instances, _factories);
}

internal sealed class SettingsAwareTypeResolver : ITypeResolver
{
    private readonly Dictionary<Type, object> _instances;
    private readonly Dictionary<Type, Func<object>> _factories;

    public SettingsAwareTypeResolver(
        Dictionary<Type, object> instances,
        Dictionary<Type, Func<object>> factories)
    {
        _instances = instances;
        _factories = factories;
    }

    public object? Resolve(Type? type)
    {
        if (type is null) return null;
        if (_instances.TryGetValue(type, out var inst)) return inst;
        if (_factories.TryGetValue(type, out var factory)) return factory();

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            return Array.CreateInstance(type.GetGenericArguments()[0], 0);

        if (type.IsInterface || type.IsAbstract) return null;

        return Construct(type, _instances, _factories);
    }

    internal static object? Construct(
        Type type,
        Dictionary<Type, object> instances,
        Dictionary<Type, Func<object>> factories)
    {
        var ctor = type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();
        if (ctor is null) return Activator.CreateInstance(type);

        var args = ctor.GetParameters().Select(p =>
        {
            if (instances.TryGetValue(p.ParameterType, out var v)) return v;
            if (factories.TryGetValue(p.ParameterType, out var f)) return f();
            if (p.HasDefaultValue) return p.DefaultValue;
            return p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
        }).ToArray();

        return ctor.Invoke(args);
    }
}
