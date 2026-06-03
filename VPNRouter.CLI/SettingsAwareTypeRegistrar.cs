using Spectre.Console.Cli;
using VPNRouter.Core.Services;

namespace VPNRouter.CLI;

/// <summary>
/// Minimal <see cref="ITypeRegistrar"/> for Spectre.Console.Cli so commands
/// with a dependency-injected constructor can be instantiated.
///
/// <para><strong>Why this exists (bug fix, 2026-06-03).</strong> Phase 4
/// Wave 19 added a testability ctor to several commands:
/// <c>public StartCommand() : this(null)</c> + <c>StartCommand(ISettingsStore?)</c>
/// (same for <c>ProfilesList/Show/Update</c>). Spectre.Console.Cli 0.49.1's
/// DEFAULT activator (used when <c>new CommandApp()</c> gets no registrar)
/// selects the GREEDIEST constructor and tries to resolve each parameter from
/// the registrar. With no registrar, <see cref="ISettingsStore"/> resolves to
/// null and Spectre reports <c>"Could not resolve type 'VPNRouter.CLI.Commands.StartCommand'"</c>
/// — so <c>vpnrouter start</c> and <c>vpnrouter profiles *</c> were BROKEN for
/// every CLI user since Wave 19. CI never caught it: it only exercises
/// <c>status</c> / <c>test-update</c>, which have no DI ctor. <c>status</c>,
/// <c>stop</c>, <c>doctor</c>, <c>service *</c> kept working for the same
/// reason.</para>
///
/// <para>This registrar resolves <see cref="ISettingsStore"/> to
/// <see cref="RealSettingsStore.Instance"/> (the same default the parameterless
/// ctor already used) so the greediest ctor binds. It changes NO command code,
/// so the InMemory-store test injection is untouched, and any future
/// DI ctor on a command works for free.</para>
/// </summary>
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

        // Spectre resolves its OWN internal extensibility points through the
        // registrar's resolver — notably IEnumerable<IHelpProvider>. The
        // M.E.DI-based sample registrar returns an empty collection for an
        // unregistered IEnumerable<T>; replicate that so Spectre falls back to
        // its built-in default instead of erroring "Could not resolve type
        // IEnumerable<...>".
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            return Array.CreateInstance(type.GetGenericArguments()[0], 0);

        // Interfaces/abstracts we don't know about → null, letting Spectre use
        // its own default for that service rather than throwing.
        if (type.IsInterface || type.IsAbstract) return null;

        return Construct(type, _instances, _factories);
    }

    /// <summary>
    /// Construct <paramref name="type"/> via its greediest constructor — the
    /// same one Spectre's default activator selects — but actually supply the
    /// registered dependencies (e.g. <see cref="ISettingsStore"/>) for its
    /// parameters instead of failing. Unresolved reference params fall back to
    /// the parameter default / null (commands here only need ISettingsStore or
    /// nothing); value-type params get their default.
    /// </summary>
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
