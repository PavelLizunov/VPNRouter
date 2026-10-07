using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VPNRouter.Core;
using VPNRouter.Core.Services;
using VPNRouter.Headless.Features;
using VPNRouter.Headless.Storage;

namespace VPNRouter.Headless;

/// <summary>
/// Headless router backend coordinating Linux feature endpoints and session lifecycle.
/// Implements protocol v1 contract (omarchy-protocol-v1.md).
/// Invariants:
/// - Strictly serialized ordinary operations; urgent controls bypass busy.
/// - Detached candidate mutations preserving settings on validation, save, or apply failures.
/// - Per-field params allowlists.
/// - No secret or raw exception logging.
/// </summary>
public sealed class RouterBackend : IAsyncDisposable
{
    private static readonly Regex MethodNameRegex = new(@"^[a-zA-Z0-9_\-\.]{1,64}$", RegexOptions.Compiled);
    private static readonly Regex RequestIdRegex = new(@"^[a-zA-Z0-9_\-]{1,64}$", RegexOptions.Compiled);

    private readonly IRouterSession _session;
    private readonly ConfigStorage _storage;
    private readonly ILogger _logger;
    private SingBoxRuntimePolicy? _policy;

    private SingBoxRuntimePolicy? EffectivePolicy => SingBoxRuntimePolicy.Capture(ref _policy);

    private readonly ServerFeature _serverFeature;
    private readonly SubscriptionFeature _subscriptionFeature;
    private readonly FreeConfigFeature _freeConfigFeature;
    private readonly AppRoutingFeature _appRoutingFeature;
    private readonly ProfileFeature _profileFeature;
    private readonly RulesFeature _rulesFeature;
    private readonly CustomConfigFeature _customConfigFeature;
    private readonly SettingsFeature _settingsFeature;
    private readonly DiagnosticsFeature _diagnosticsFeature;

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlightRequests = new();
    private int _busyState = 0;
    private bool _isDisposed = false;

    public event Action<object>? StateChanged;
    public event Action<object>? ProgressChanged;

    public RouterBackend(IRouterSession session, string? dataDir = null, ILogger? logger = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _logger = logger ?? Log.Logger;
        _policy = SingBoxRuntimePolicy.Current ?? (OperatingSystem.IsLinux() ? SingBoxRuntimePolicy.DefaultProduction : null);

        using var _ = SingBoxRuntimePolicy.EnterScope(_policy);
        _storage = new ConfigStorage(dataDir, _logger);

        _serverFeature = new ServerFeature(_storage, _logger);
        _subscriptionFeature = new SubscriptionFeature(_storage, _logger);
        _freeConfigFeature = new FreeConfigFeature(_storage, _logger);
        _appRoutingFeature = new AppRoutingFeature(_storage, _logger);
        _profileFeature = new ProfileFeature(_storage, _logger);
        _rulesFeature = new RulesFeature(_storage, _logger);
        _customConfigFeature = new CustomConfigFeature(_storage, _logger);
        _settingsFeature = new SettingsFeature(_storage, () => _session.SupportsDnsLockdown, _logger);
        _diagnosticsFeature = new DiagnosticsFeature(() => string.Equals(_session.State, "connected", StringComparison.OrdinalIgnoreCase), _logger);

        _session.Changed += OnSessionChanged;
    }

    /// <summary>
    /// Executes a protocol v1 request method.
    /// </summary>
    public async Task<object> ExecuteAsync(string method, JsonElement parameters, CancellationToken ct = default)
    {
        using var _ = SingBoxRuntimePolicy.EnterScope(EffectivePolicy);
        try
        {
            if (string.IsNullOrWhiteSpace(method) || !MethodNameRegex.IsMatch(method))
                throw new RouterException("invalid_request", "Invalid or missing method name");

            // Urgent non-blocking control operations bypass busy concurrency check
            if (method == "cancel")
                return HandleCancel(parameters);

            if (method == "disconnect")
            {
                EnsureEmptyParameters(parameters);
                return await HandleDisconnectAsync(ct);
            }

            if (method == "snapshot")
            {
                EnsureEmptyParameters(parameters);
                return GetSnapshot();
            }

            // Ordinary operations are strictly serialized (at most 1 active)
            if (Interlocked.CompareExchange(ref _busyState, 1, 0) != 0)
                throw new RouterException("busy", "Another operation is currently in progress");

            try
            {
                EmitStateChanged();
                return await DispatchOrdinaryMethodAsync(method, parameters, ct);
            }
            finally
            {
                Interlocked.Exchange(ref _busyState, 0);
                EmitStateChanged();
            }
        }
        catch (SingBoxRuntimePolicyException)
        {
            throw new RouterException("unavailable", "VPN runtime is unavailable");
        }
    }

    public object GetSnapshot()
    {
        using var _ = SingBoxRuntimePolicy.EnterScope(EffectivePolicy);
        var settings = _storage.GetSettings();
        var configMode = settings.App?.ConfigMode?.ToLowerInvariant() ?? "generated";
        string activeServer = string.Empty;

        if (configMode == "custom")
        {
            activeServer = settings.App?.ActiveCustomConfig ?? string.Empty;
        }
        else if (configMode == "subscribe")
        {
            activeServer = settings.App?.ActiveSubscriptionServer ?? string.Empty;
        }
        else
        {
            activeServer = settings.Vless?.ActiveServer ?? string.Empty;
        }

        return new
        {
            state = _session.State,
            revision = _storage.CurrentRevision,
            backendVersion = AppVersion.Version,
            activeServer,
            routingMode = settings.App?.RoutingMode?.ToLowerInvariant() ?? "split",
            routingAppsMode = settings.App?.RoutingAppsMode?.ToLowerInvariant() ?? "include",
            configMode,
            busy = _busyState != 0,
            errorCode = _session.ErrorCode,
            capabilities = new
            {
                connect = _session.CanConnect,
                killSwitch = _session.SupportsKillSwitch,
                dnsLockdown = _session.SupportsDnsLockdown
            }
        };
    }

    private async Task<object> DispatchOrdinaryMethodAsync(string method, JsonElement parameters, CancellationToken ct)
    {
        switch (method)
        {
            case "connect":
            {
                EnsureAllowedProperties(parameters, "revision");
                if (!parameters.TryGetProperty("revision", out var revProp) ||
                    revProp.ValueKind != JsonValueKind.String)
                {
                    throw new RouterException("invalid_argument", "Missing revision parameter");
                }

                _storage.ValidateRevision(revProp.GetString());
                var settings = _storage.GetSettings();
                await _session.ConnectAsync(settings, ct);
                return GetSnapshot();
            }

            case "servers.list":
                return _serverFeature.List(parameters);

            case "servers.import":
                return await ExecuteMutationAsync(() => { _serverFeature.Import(parameters); return Task.CompletedTask; }, ct);

            case "servers.select":
                return await ExecuteMutationAsync(() => { _serverFeature.Select(parameters); return Task.CompletedTask; }, ct);

            case "servers.remove":
                return await ExecuteMutationAsync(() => { _serverFeature.Remove(parameters); return Task.CompletedTask; }, ct);

            case "servers.test":
                return await _serverFeature.TestAsync(parameters, ct);

            case "servers.verify":
                return await _serverFeature.VerifyAsync(parameters, p => ProgressChanged?.Invoke(p), ct);

            case "subscriptions.list":
                return _subscriptionFeature.List(parameters);

            case "subscriptions.add":
                return await ExecuteMutationAsync(() => { _subscriptionFeature.Add(parameters); return Task.CompletedTask; }, ct);

            case "subscriptions.remove":
                return await ExecuteMutationAsync(() => { _subscriptionFeature.Remove(parameters); return Task.CompletedTask; }, ct);

            case "subscriptions.enable":
                return await ExecuteMutationAsync(() => { _subscriptionFeature.Enable(parameters); return Task.CompletedTask; }, ct);

            case "subscriptions.refresh":
                return await ExecuteMutationAsync(() => _subscriptionFeature.RefreshAsync(parameters, p => ProgressChanged?.Invoke(p), ct), ct);

            case "free.list":
                return _freeConfigFeature.List(parameters);

            case "free.refresh":
                return await _freeConfigFeature.RefreshAsync(parameters, ct);

            case "free.test":
                return await _freeConfigFeature.TestAsync(parameters, ct);

            case "free.verify":
                return await _freeConfigFeature.VerifyAsync(parameters, p => ProgressChanged?.Invoke(p), ct);

            case "free.apply":
                return await ExecuteMutationAsync(() => { _freeConfigFeature.Apply(parameters); return Task.CompletedTask; }, ct);

            case "apps.list":
                return _appRoutingFeature.List(parameters);

            case "apps.set":
                return await ExecuteMutationAsync(() => { _appRoutingFeature.SetApps(parameters); return Task.CompletedTask; }, ct);

            case "routing.set":
                return await ExecuteMutationAsync(() => { _appRoutingFeature.SetRouting(parameters); return Task.CompletedTask; }, ct);

            case "profiles.list":
                return await _profileFeature.ListAsync(parameters, ct);

            case "profiles.select":
                return await ExecuteMutationAsync(() => _profileFeature.SelectAsync(parameters, ct), ct);

            case "profiles.refresh":
                return await _profileFeature.RefreshAsync(parameters, ct);

            case "rules.get":
                return _rulesFeature.Get(parameters);

            case "rules.set":
                return await ExecuteMutationAsync(() => { _rulesFeature.Set(parameters); return Task.CompletedTask; }, ct);

            case "rules.import":
                return await ExecuteMutationAsync(() => { _rulesFeature.Import(parameters); return Task.CompletedTask; }, ct);

            case "rules.export":
                return _rulesFeature.Export(parameters);

            case "custom.list":
                return _customConfigFeature.List(parameters);

            case "custom.import":
                return await ExecuteMutationAsync(() => { _customConfigFeature.Import(parameters); return Task.CompletedTask; }, ct);

            case "custom.select":
                return await ExecuteMutationAsync(() => { _customConfigFeature.Select(parameters); return Task.CompletedTask; }, ct);

            case "custom.remove":
                return await ExecuteMutationAsync(() => { _customConfigFeature.Remove(parameters); return Task.CompletedTask; }, ct);

            case "settings.get":
                return _settingsFeature.Get(parameters);

            case "settings.set":
                return await ExecuteMutationAsync(() => { _settingsFeature.Set(parameters); return Task.CompletedTask; }, ct);

            case "diagnostics.check":
                return _diagnosticsFeature.Check(parameters);

            case "diagnostics.export":
                return _diagnosticsFeature.Export(parameters);

            default:
                throw new RouterException("invalid_request", $"Unknown method '{method}'");
        }
    }

    private async Task<object> ExecuteMutationAsync(Func<Task> mutationAction, CancellationToken ct)
    {
        var previousSettings = _storage.GetSettings();

        // Reject cancellation already observed before invoking the mutation.
        // Later cancellation does not interrupt synchronous persistence; failed
        // connected-session apply still uses guarded compensation below.
        ct.ThrowIfCancellationRequested();
        // 1. Execute mutation (validates candidate, writes atomic CAS to storage)
        await mutationAction();
        var committedRevision = _storage.CurrentRevision;

        // 2. If session is connected, apply mutation to the active connection
        if (string.Equals(_session.State, "connected", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var updatedSettings = _storage.GetSettings();
                await _session.ApplyAsync(updatedSettings, ct);
            }
            catch (Exception)
            {
                // Guarded compensation using committed revision:
                // Only revert if disk is still at our committed revision.
                // If an external edit occurred, fail closed with conflict to avoid overwriting user edits.
                try
                {
                    _storage.SaveSettings(previousSettings, committedRevision);
                }
                catch (RouterException conflictEx) when (conflictEx.Code == "conflict")
                {
                    _logger.Error("[RouterBackend] Guarded rollback conflict: external modification preserved: {ErrorType}", conflictEx.GetType().Name);
                    throw;
                }
                catch (Exception rollbackEx)
                {
                    _logger.Error("[RouterBackend] Guarded rollback failed: {ErrorType}", rollbackEx.GetType().Name);
                    throw new RouterException("storage_error", "Rollback failed after apply failure");
                }

                throw;
            }
        }

        return GetSnapshot();
    }

    private object HandleCancel(JsonElement parameters)
    {
        EnsureAllowedProperties(parameters, "id");
        if (!parameters.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.String)
        {
            throw new RouterException("invalid_argument", "Missing id parameter for cancel");
        }

        var id = idProp.GetString()!;
        if (!RequestIdRegex.IsMatch(id))
        {
            throw new RouterException("invalid_argument", "Invalid request id syntax");
        }

        if (_inFlightRequests.TryRemove(id, out var cts))
        {
            try { cts.Cancel(); } catch { }
            try { cts.Dispose(); } catch { }
            return new { id, cancelled = true };
        }

        return new { id, cancelled = false };
    }

    private async Task<object> HandleDisconnectAsync(CancellationToken ct)
    {
        await _session.DisconnectAsync(ct);
        return GetSnapshot();
    }

    private void OnSessionChanged()
    {
        EmitStateChanged();
    }

    private void EmitStateChanged()
    {
        try
        {
            StateChanged?.Invoke(GetSnapshot());
        }
        catch
        {
            _logger.Warning("[RouterBackend] Failed to emit StateChanged event");
        }
    }

    public static void EnsureAllowedProperties(JsonElement parameters, params string[] allowedProperties)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
            throw new RouterException("invalid_request", "Expected JSON object parameters");

        var allowed = new HashSet<string>(allowedProperties, StringComparer.Ordinal);
        foreach (var prop in parameters.EnumerateObject())
        {
            if (!allowed.Contains(prop.Name))
            {
                throw new RouterException("invalid_argument", $"Unexpected parameter '{prop.Name}'");
            }
        }
    }

    public static void EnsureEmptyParameters(JsonElement parameters)
    {
        if (parameters.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in parameters.EnumerateObject())
            {
                throw new RouterException("invalid_argument", $"Unexpected parameter '{prop.Name}'");
            }
        }
        else if (parameters.ValueKind != JsonValueKind.Undefined && parameters.ValueKind != JsonValueKind.Null)
        {
            throw new RouterException("invalid_request", "Expected JSON object parameters");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
            return;

        using var _ = SingBoxRuntimePolicy.EnterScope(EffectivePolicy);
        _isDisposed = true;
        _session.Changed -= OnSessionChanged;

        foreach (var kv in _inFlightRequests)
        {
            try { kv.Value.Cancel(); } catch { }
            try { kv.Value.Dispose(); } catch { }
        }
        _inFlightRequests.Clear();

        await _session.DisposeAsync();
    }
}
