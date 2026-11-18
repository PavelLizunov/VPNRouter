using Serilog;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public partial class VpnEngine
{
    private sealed class VpnEngineStartupHost : StartupHostInternal
    {
        private readonly VpnEngine _engine;
        private readonly long _generation;
        private readonly CancellationTokenSource? _sessionCts;
        private SingBoxManager? _singBoxManager;
        private IProcessHandle? _startedHandle;

        public VpnEngineStartupHost(VpnEngine engine)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _generation = engine._failoverGeneration;
            _sessionCts = engine._sessionCts;
        }

        public ILogger? Logger => _engine._logger;
        public IProcessScanner Scanner => _engine._scanner;
        public Func<IFirewallManager> FirewallFactory => _engine._firewallFactory;
        public Func<IProcessMonitor> MonitorFactory => _engine._monitorFactory;

        public SingBoxManager? SingBox => _engine._singBox;
        public IFirewallManager? Firewall => _engine._firewall;

        public void OnStatus(string message) => _engine.OnStatus(message);

        public void OnWarning(string message) => _engine.Warning?.Invoke(message);

        public void OnSingBoxStarted(int pid)
        {
            _startedHandle = _singBoxManager?.OwnedProcessHandle;
            if (_engine._policySession is { } session && ReferenceEquals(session.Cancellation, _sessionCts))
                session.HasRun = true;
            _engine.EnterPostStartPhase();
            _engine.SingBoxStarted?.Invoke(pid);
        }

        public bool CallerConfirmsProxy => _engine._confirmingFreeConfig;

        public bool IsCurrentStart(int pid)
        {
            try
            {
                if (_engine._disposed) return false;
                if (_generation != _engine._failoverGeneration) return false;
                if (_sessionCts == null || _sessionCts.IsCancellationRequested || !ReferenceEquals(_engine._sessionCts, _sessionCts))
                    return false;

                if (_singBoxManager == null || !ReferenceEquals(_engine._singBox, _singBoxManager) || _singBoxManager.Pid != pid || _singBoxManager.State != SingBoxState.Running)
                    return false;

                return _startedHandle != null && !_startedHandle.HasExited
                    && ReferenceEquals(_singBoxManager.OwnedProcessHandle, _startedHandle);
            }
            catch
            {
                return false;
            }
        }

        public void OnConnected(int pid)
        {
            if (!IsCurrentStart(pid) || _engine._confirmingFreeConfig) return;

            _engine._warmupConfirmed = true;
            _engine._failover?.ResetCycle();
            _engine.Connected?.Invoke(pid);
        }

        public void OnRestartAttempted(int attempt, int max) =>
            _engine.RestartAttempted?.Invoke(attempt, max);

        public void OnAutoFailoverTriggered(string message) =>
            _engine.AutoFailoverTriggered?.Invoke(message);

        public void OnFailoverRequested(string reason)
        {
            // The monitor raises this event under its settings gate. Freeze the active policy
            // here; a queued task may begin after Apply accepts a different one.
            var token = _engine._sessionCts?.Token ?? CancellationToken.None;
            var failover = WireFailoverWithStop(new ConfigSanityCheck(_engine._logger));
            _ = Task.Run(async () =>
            {
                try
                {
                    if (token.IsCancellationRequested || failover.IsCurrentIntent?.Invoke() != true) return;
                    var outcome = await failover.HandleDeadConfigAsync(reason, token);
                    if (outcome.UserFacingMessage != null && failover.IsCurrentIntent?.Invoke() == true)
                        _engine.AutoFailoverTriggered?.Invoke(outcome.UserFacingMessage);
                }
                catch (Exception)
                {
                    _engine._logger?.Warning(
                        "[VpnEngine] HealthMonitor-requested failover failed — leaving VPN stopped");
                }
            });
        }

        public void OnProcessDetected(string name, int pid) =>
            _engine.ProcessDetected?.Invoke(name, pid);

        public void SetActiveServerAddress(string address) =>
            _engine.ActiveServerAddress = address;

        public void SetActiveModes(string configMode, string routingMode, string tunFingerprint)
        {
            _engine.ActiveConfigMode = configMode;
            _engine.ActiveRoutingMode = routingMode;
            _engine.TunFingerprint = tunFingerprint;
        }

        public void SetActiveProfile(Profile profile) => _engine._activeProfile = profile;

        public void SetScanResult(ScanResult result) => _engine._scanResult = result;

        public void SetSingBoxManager(SingBoxManager manager)
        {
            _singBoxManager = manager;
            _engine._singBox = manager;
            manager.PolicyConfigurationAccepted += launch =>
                _engine.ObservePolicyAcceptance(manager, launch, _sessionCts);
            manager.Crashed += (_, _) =>
            {
                if (ReferenceEquals(_engine._singBox, manager))
                    _engine.SingBoxCrashed?.Invoke();
            };
        }

        public void StartDnsTunnelTransport(VlessServerEntry activeServer, AppSettings settings)
        {
            if (settings.App?.DnsLeakLockdown == true)
                _engine._logger?.Warning(
                    "[VpnEngine] dns-tunnel active WITH DnsLeakLockdown — the lockdown may block " +
                    "slipstream-client's DNS to the resolvers (the tunnel's own transport). " +
                    "If the tunnel won't connect, disable DnsLeakLockdown.");

            var slip = _engine._slipstream ??= new SlipstreamManager(_engine._logger);
            _engine.OnStatus("Starting DNS-tunnel transport...");
            slip.Start(activeServer, SlipstreamManager.DefaultLocalPort);

            if (!slip.WaitForPortListening(5000))
            {
                slip.Stop();
                throw new SlipstreamException(
                    "slipstream-client did not start listening on 127.0.0.1:" +
                    SlipstreamManager.DefaultLocalPort + " within 5s");
            }
            _engine.OnStatus("DNS-tunnel transport up (127.0.0.1:" + SlipstreamManager.DefaultLocalPort + ")");
        }

        public void SetFirewallManager(IFirewallManager firewall) => _engine._firewall = firewall;

        public void SetProcessMonitor(IProcessMonitor etw) => _engine._etw = etw;

        public void SetHealthMonitor(HealthMonitor monitor) => _engine._healthMonitor = monitor;

        public void EnsureSanityCheckScaffolding(AppSettings settings, out ConfigSanityCheck sanityCheck)
        {
            CaptureSettings(settings);
            _engine._sanityCheck ??= new ConfigSanityCheck(_engine._logger);
            sanityCheck = _engine._sanityCheck;
        }

        public AutoFailoverEngine WireFailover(ConfigSanityCheck sanityCheck)
            => WireFailoverCore(sanityCheck);

        public AutoFailoverEngine WireFailoverWithStop(ConfigSanityCheck sanityCheck)
            => WireFailoverCore(sanityCheck);

        private AutoFailoverEngine WireFailoverCore(ConfigSanityCheck sanityCheck)
        {
            var settings = _engine._failoverSettingsContext ?? CapturedSettings();
            var generation = _engine._failoverGeneration;
            var session = _engine._sessionCts;
            _engine._failover ??= new AutoFailoverEngine(
                settings,
                sanityCheck,
                restart: (innerCt) =>
                    _engine.ExecuteFailoverRestartAsync(settings, innerCt, generation),
                logger: _engine._logger)
            {
                IsSelectionConfirmed = () => _engine._warmupConfirmed,
                ProbeCandidate = (server, probeCt) => TcpTlsProbe.ProbeServerAsync(server, probeCt),
                IsCurrentIntent = () =>
                {
                    var sessionCts = _engine._sessionCts;
                    return !_engine._disposed && !_engine._confirmingFreeConfig
                        && _engine._failoverGeneration == generation && ReferenceEquals(session, sessionCts)
                        && (sessionCts == null || !sessionCts.IsCancellationRequested);
                }
            };
            return _engine._failover;
        }

        private AppSettings _capturedSettings = null!;
        public void CaptureSettings(AppSettings settings) => _capturedSettings = settings;
        private AppSettings CapturedSettings() =>
            _capturedSettings ?? throw new InvalidOperationException(
                "StartupHost: CaptureSettings was not called before F-E wire-up.");

        public CancellationToken BeginStartupProbes(CancellationToken ct)
        {
            _engine._probeCts?.Cancel();
            _engine._probeCts?.Dispose();
            _engine._probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            return _engine._probeCts.Token;
        }

        public void SchedulePostStartProbe(
            AppSettings settings,
            ConfigSanityCheck sanityCheck,
            CancellationToken ct)
        {
            CaptureSettings(settings);

            _engine.TryStartConnectionHealthStream(settings);

            var pid = _startedHandle?.Pid ?? -1;
            var port = ParseClashApiPort(settings.SingBox.ClashApi);
            var secret = settings.SingBox.ClashApiSecret;
            var failover = WireFailoverWithStop(sanityCheck);
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), ct);
                    var probe = await ProbeCurrentStartAsync(
                        () => IsCurrentStart(pid),
                        token => sanityCheck.ProbeAsync(port, secret, token), ct);
                    if (probe == null || ct.IsCancellationRequested) return;
                    var warmedUp = _engine._warmupConfirmed;
                    if (!ShouldAutoFailoverAfterProbe(probe.IsDead, ct.IsCancellationRequested, warmedUp))
                    {
                        if (probe.IsDead && warmedUp)
                            _engine._logger?.Warning(
                                "[VpnEngine] F-E post-start probe failed after TUN warmup confirmed connectivity, keeping current server");
                        return;
                    }

                    _engine._logger?.Warning("[VpnEngine] F-E post-start probe failed, requesting failover");
                    var outcome = await failover.HandleDeadConfigAsync(probe.Reason ?? "probe failed", ct);
                    if (outcome.UserFacingMessage != null && failover.IsCurrentIntent?.Invoke() == true)
                        _engine.AutoFailoverTriggered?.Invoke(outcome.UserFacingMessage);
                }
                catch (OperationCanceledException) { }
                catch (Exception)
                {
                    _engine._logger?.Debug(
                        "[VpnEngine] F-E probe task threw (non-fatal)");
                }
            }, ct);
        }
    }
}
