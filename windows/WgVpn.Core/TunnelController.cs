// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

using WgVpn.Core.Ipc;

namespace WgVpn.Core;

public sealed record TunnelTimeouts(TimeSpan Preflight, TimeSpan UdpListener, TimeSpan Handshake, TimeSpan HandshakePoll)
{
    public static TunnelTimeouts Default { get; } = new(
        TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));
}

/// <summary>
/// Orchestrates connect, teardown and recovery over <see cref="ITunnelPlatform"/>.
/// <para>
/// Concurrency model. <c>_gate</c> guards the state machine, LastError, the current session and
/// the retired list; it is never held across an await or a token cancellation. <c>_opLock</c>
/// serializes every operation with side effects (a workflow, a teardown, recovery, a bundle store).
/// </para>
/// <para>
/// Each connect attempt is a <see cref="Session"/>. Ending one, for any reason, retires it under
/// the gate: it stops being current, so anything it does later (a handshake seen, a failure, a
/// wstunnel exit) changes no state. Whoever next holds the op lock tears down retired sessions
/// before doing anything else, so a new connection can never overlap an old one regardless of
/// the order in which op lock waiters are released.
/// </para>
/// </summary>
public sealed class TunnelController
{
    private const string NoBundle = "no bundle imported";
    private const string DisconnectFirst = "disconnect first";

    private readonly ITunnelPlatform _platform;
    private readonly TunnelTimeouts _timeouts;
    private readonly Action<string> _log;

    private readonly object _gate = new();
    private readonly ConnectionStateMachine _state = new();
    private readonly List<Session> _retired = [];
    private readonly HashSet<Task> _background = [];
    private Session? _current;
    private string? _lastError;
    private string? _cleanupError;
    private bool _serverHostKnown;
    private string? _serverHost;

    private readonly SemaphoreSlim _opLock = new(1, 1);

    // What a failed cleanup left to undo, mirroring the kept state file. Guarded by the op lock.
    private RecoveryState? _pendingCleanup;

    public TunnelController(ITunnelPlatform platform, TunnelTimeouts timeouts, Action<string>? log = null)
    {
        _platform = platform;
        _timeouts = timeouts;
        _log = log ?? (_ => { });
    }

    private sealed class Session
    {
        private readonly CancellationTokenSource _cts = new();

        public CancellationToken Token => _cts.Token;
        public void Cancel() => _cts.Cancel();

        // Written only by this session's workflow, read only by its teardown; both hold the op lock.
        public bool RecoveryStarted;
        public IWstunnelHandle? Wstunnel;
        public readonly List<string> AddedRoutes = [];
        public bool ConfWritten;
        public bool TunnelTouched;
    }

    // ---- requests ----

    /// <summary>Replies at once with the current status; a started workflow runs in the background.</summary>
    public Task<StatusReply> ConnectAsync()
    {
        Session? started = null;
        lock (_gate)
        {
            if (_state.Connect())
            {
                _lastError = null;
                _current = started = new Session();
            }
        }
        if (started != null) Track(Task.Run(() => RunWorkflowAsync(started)));
        return GetStatusAsync();
    }

    /// <summary>Works in any state, and retries a cleanup that failed earlier. Completes after teardown.</summary>
    public async Task<StatusReply> DisconnectAsync()
    {
        Session? ended;
        lock (_gate)
        {
            _state.Disconnect();
            _lastError = null; // a deliberate disconnect ends the failed attempt; _cleanupError stays
            ended = RetireCurrent();
        }
        ended?.Cancel();

        await _opLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await RetryPendingCleanupAsync().ConfigureAwait(false);
            await DrainRetiredAsync().ConfigureAwait(false);
        }
        finally
        {
            _opLock.Release();
        }
        return await GetStatusAsync().ConfigureAwait(false);
    }

    public async Task<StatusReply> GetStatusAsync()
    {
        var serverHost = await GetServerHostAsync().ConfigureAwait(false);
        lock (_gate)
        {
            var error = _lastError != null && _cleanupError != null
                ? $"{_lastError} {_cleanupError}"
                : _lastError ?? _cleanupError;
            return new StatusReply(_state.State, serverHost, error);
        }
    }

    public async Task<ImportBundleReply> ImportBundleAsync(string bundleJson)
    {
        if (IsActive()) return new ImportBundleReply(false, DisconnectFirst);
        ConnectionBundle parsed;
        try
        {
            parsed = BundleParser.Parse(bundleJson);
        }
        catch (BundleFormatException e)
        {
            return new ImportBundleReply(false, e.Message);
        }

        await _opLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // A Connect may have started while waiting. Any Connect after this check blocks on the op lock.
            if (IsActive()) return new ImportBundleReply(false, DisconnectFirst);
            await _platform.SaveBundleAsync(bundleJson, CancellationToken.None).ConfigureAwait(false);
            lock (_gate)
            {
                _serverHost = parsed.Server.Host;
                _serverHostKnown = true;
            }
            return new ImportBundleReply(true, null);
        }
        catch (Exception e)
        {
            _log($"Storing the bundle failed: {e.Message}");
            return new ImportBundleReply(false, $"Could not store the bundle: {e.Message}");
        }
        finally
        {
            _opLock.Release();
        }
    }

    /// <summary>Cleans up after a crash or a failed cleanup. Call once at service start, before serving requests.</summary>
    public async Task RecoverAsync()
    {
        await GetServerHostAsync().ConfigureAwait(false); // caches it, so polling never reads the bundle file

        await _opLock.WaitAsync().ConfigureAwait(false);
        try
        {
            RecoveryState? state = null;
            var unreadable = false;
            try
            {
                state = await _platform.LoadRecoveryStateAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                unreadable = true;
                _log($"Recovery: state file is unreadable, cleaning what is known: {e.Message}");
            }
            if (state == null && !unreadable)
            {
                // The WireGuard tunnel service starts automatically at boot. Without a state file
                // it may still be installed and, with wstunnel gone, would blackhole all traffic.
                bool leftover;
                try
                {
                    leftover = await _platform.TunnelExistsAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    _log($"Recovery: could not check for a leftover tunnel: {e.Message}");
                    return;
                }
                if (!leftover) return;
                _log("Recovery: removing a leftover tunnel service.");
            }

            _log("Recovery: cleaning up after an unclean stop.");
            await CleanRecordedAsync(state ?? new RecoveryState(null, [])).ConfigureAwait(false);
        }
        finally
        {
            _opLock.Release();
        }
    }

    /// <summary>Service stop: tears everything down and waits for background work to finish.</summary>
    public async Task ShutdownAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        await IdleAsync().ConfigureAwait(false);
    }

    /// <summary>Completes when no workflow or exit-triggered teardown is running.</summary>
    public async Task IdleAsync()
    {
        while (true)
        {
            Task[] running;
            lock (_gate) running = _background.ToArray();
            if (running.Length == 0) return;
            await Task.WhenAll(running).ConfigureAwait(false);
            lock (_gate) _background.ExceptWith(running);
        }
    }

    // ---- connect workflow ----

    private async Task RunWorkflowAsync(Session session)
    {
        try
        {
            await _opLock.WaitAsync(session.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // Retired before it held anything; whoever retired it drains the list.
        }

        try
        {
            await DrainRetiredAsync().ConfigureAwait(false);

            // A Disconnect retires before it cancels. If this session was retired in that gap, the
            // drain above just consumed it, and nothing would tear down what its steps start.
            lock (_gate)
            {
                if (_current != session) return;
            }

            await RetryPendingCleanupAsync().ConfigureAwait(false);
            if (_pendingCleanup != null)
                throw new TunnelException("Cannot connect until the previous cleanup succeeds.");

            await ConnectStepsAsync(session).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            FailIfCurrent(session, e);
        }
        finally
        {
            await DrainRetiredAsync().ConfigureAwait(false);
            _opLock.Release();
        }
    }

    private async Task ConnectStepsAsync(Session s)
    {
        var ct = s.Token;
        ct.ThrowIfCancellationRequested();

        var json = await _platform.LoadBundleAsync(ct).ConfigureAwait(false) ?? throw new TunnelException(NoBundle);
        var bundle = BundleParser.Parse(json);

        var ips = await _platform.ResolveProxyIpv4Async(bundle.Proxy.Host, ct).ConfigureAwait(false);
        if (ips.Count == 0)
            throw new TunnelException($"Could not resolve proxy {bundle.Proxy.Host} to an IPv4 address.");

        var preflight = await WithTimeoutAsync(s, _timeouts.Preflight, "TLS pin preflight timed out",
            t => _platform.PinPreflightAsync(bundle, ips, t)).ConfigureAwait(false);
        if (!TlsPin.Matches(preflight.LeafCertDer, bundle.Server.TlsSha256))
            throw new TunnelException("The server's TLS certificate does not match the fingerprint in the bundle.");

        s.RecoveryStarted = true;
        await SaveRecoveryAsync(s, ct).ConfigureAwait(false);
        var handle = await _platform.StartWstunnelAsync(WstunnelArgs.Build(bundle, preflight.SelectedProxyIp), ct).ConfigureAwait(false);
        s.Wstunnel = handle;
        WatchForExit(s, handle);
        await SaveRecoveryAsync(s, ct).ConfigureAwait(false);

        await WithTimeoutAsync(s, _timeouts.UdpListener, "Waiting for wstunnel's UDP listener timed out",
            async t =>
            {
                await _platform.WaitForUdpListenerAsync(bundle.Wstunnel.LocalUdpPort, t).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);

        await AddRoutesAsync(s, ips, ct).ConfigureAwait(false);

        s.ConfWritten = true;
        await _platform.WriteConfAsync(WireguardConf.Render(bundle), ct).ConfigureAwait(false);
        s.TunnelTouched = true;
        await _platform.UninstallTunnelAsync(ct).ConfigureAwait(false); // leftover from an earlier run
        await _platform.InstallTunnelAsync(ct).ConfigureAwait(false);

        await WithTimeoutAsync(s, _timeouts.Handshake, "WireGuard handshake timed out", async t =>
        {
            while (!WgHandshake.HasHandshake(await _platform.GetLatestHandshakesAsync(t).ConfigureAwait(false)))
                await Task.Delay(_timeouts.HandshakePoll, t).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);

        lock (_gate)
        {
            if (_current == s) _state.OnTunnelUp();
        }
    }

    private async Task AddRoutesAsync(Session s, IReadOnlyList<string> ips, CancellationToken ct)
    {
        foreach (var ip in ips)
        {
            var route = await _platform.GetBestRouteAsync(ip, ct).ConfigureAwait(false);
            if (route.IsOnLink(ip)) continue;

            await _platform.SaveRecoveryStateAsync(
                new RecoveryState(s.Wstunnel?.ProcessId, [.. s.AddedRoutes, ip]), ct).ConfigureAwait(false);
            var add = ProxyRoute.BuildAdd([ip], route.NextHop, route.IfIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));

            // Recorded first: a cancel or timeout may land after route.exe has committed the route.
            // Only a clean non-zero exit proves it did not, and a route that was already there
            // (not ours to delete) is reported that way too.
            s.AddedRoutes.Add(ip);
            try
            {
                await _platform.RunProcessAsync(add[0], ct).ConfigureAwait(false);
            }
            catch (ProcessFailedException)
            {
                s.AddedRoutes.Remove(ip);
                throw;
            }
        }
    }

    private Task SaveRecoveryAsync(Session s, CancellationToken ct) =>
        _platform.SaveRecoveryStateAsync(new RecoveryState(s.Wstunnel?.ProcessId, s.AddedRoutes.ToList()), ct);

    /// <summary>Runs a step under a timeout. A timeout is a failure; the session's own cancellation stays a cancellation.</summary>
    private static async Task<T> WithTimeoutAsync<T>(Session s, TimeSpan timeout, string message, Func<CancellationToken, Task<T>> step)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(s.Token);
        cts.CancelAfter(timeout);
        try
        {
            return await step(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !s.Token.IsCancellationRequested)
        {
            throw new TunnelException(message);
        }
    }

    /// <summary>
    /// A cancelled or superseded workflow is no longer current, so it can never set Error
    /// here and corrupt a newer connection's state.
    /// </summary>
    private void FailIfCurrent(Session s, Exception e)
    {
        lock (_gate)
        {
            if (_current != s) return;
            _state.OnFailed();
            _lastError = e is TunnelException or BundleFormatException ? e.Message : $"Connection failed: {e.Message}";
            RetireCurrent();
        }
        _log($"Connection failed: {e.Message}");
    }

    // ---- wstunnel exit ----

    private void WatchForExit(Session s, IWstunnelHandle handle)
    {
        handle.Exited.ContinueWith(
            t => Track(OnWstunnelExitedAsync(s, handle, t.IsCompletedSuccessfully ? t.Result : -1)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task OnWstunnelExitedAsync(Session s, IWstunnelHandle handle, int exitCode)
    {
        lock (_gate)
        {
            // Not current: a stale exit from an earlier process, or the kill of an intended teardown.
            if (_current != s) return;
            _state.OnProcessExited();
            var tail = handle.OutputTail;
            _lastError = string.IsNullOrWhiteSpace(tail)
                ? $"wstunnel exited with code {exitCode}."
                : $"wstunnel exited with code {exitCode}: {tail}";
            RetireCurrent();
        }
        _log($"wstunnel exited with code {exitCode}.");
        s.Cancel();
        await DrainRetiredUnderOpLockAsync().ConfigureAwait(false);
    }

    // ---- teardown ----

    private Session? RetireCurrent()
    {
        var s = _current;
        if (s != null)
        {
            _current = null;
            _retired.Add(s);
        }
        return s;
    }

    private async Task DrainRetiredUnderOpLockAsync()
    {
        await _opLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await DrainRetiredAsync().ConfigureAwait(false);
        }
        finally
        {
            _opLock.Release();
        }
    }

    /// <summary>Caller holds the op lock, so no retired session's workflow is mid-step.</summary>
    private async Task DrainRetiredAsync()
    {
        while (true)
        {
            Session next;
            lock (_gate)
            {
                if (_retired.Count == 0) return;
                next = _retired[0];
                _retired.RemoveAt(0);
            }
            await TeardownAsync(next).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reverses setup, undoing only what this session started. Every step is attempted, except
    /// that the conf stays while the tunnel may still be installed: its service reads that file.
    /// </summary>
    private async Task TeardownAsync(Session s)
    {
        if (!s.RecoveryStarted) return; // nothing is started before the first recovery save

        var failures = new List<string>();
        var tunnelGone = !s.TunnelTouched || await AttemptAsync(failures, "uninstall tunnel",
            () => _platform.UninstallTunnelAsync(CancellationToken.None)).ConfigureAwait(false);

        int? pidLeft = null;
        if (s.Wstunnel is { } handle)
        {
            if (!await AttemptAsync(failures, "kill wstunnel", handle.KillAndWaitAsync).ConfigureAwait(false))
                pidLeft = handle.ProcessId;
            try
            {
                handle.Dispose();
            }
            catch (Exception e)
            {
                _log($"Cleanup: release wstunnel handle: {e.Message}"); // the process state is unaffected
            }
        }

        var routesLeft = await RemoveRoutesAsync(failures, s.AddedRoutes).ConfigureAwait(false);
        if (s.ConfWritten && tunnelGone)
            await AttemptAsync(failures, "delete conf", _platform.DeleteConfAsync).ConfigureAwait(false);

        await FinishCleanupAsync(failures, pidLeft, routesLeft).ConfigureAwait(false);
    }

    /// <summary>Caller holds the op lock.</summary>
    private async Task RetryPendingCleanupAsync()
    {
        if (_pendingCleanup is { } pending)
            await CleanRecordedAsync(pending).ConfigureAwait(false);
    }

    /// <summary>
    /// Cleans from a recovery state alone: after a crash, or to retry a failed cleanup. The tunnel
    /// and conf are not recorded because the state's existence implies them; both calls tolerate absence.
    /// </summary>
    private async Task CleanRecordedAsync(RecoveryState state)
    {
        var failures = new List<string>();
        var tunnelGone = await AttemptAsync(failures, "uninstall tunnel",
            () => _platform.UninstallTunnelAsync(CancellationToken.None)).ConfigureAwait(false);

        var pidLeft = state.WstunnelPid;
        if (state.WstunnelPid is int pid
            && await AttemptAsync(failures, "kill wstunnel", () => KillIfWstunnelAsync(pid)).ConfigureAwait(false))
        {
            pidLeft = null;
        }

        var routesLeft = await RemoveRoutesAsync(failures, state.RouteIps ?? []).ConfigureAwait(false);
        if (tunnelGone)
            await AttemptAsync(failures, "delete conf", _platform.DeleteConfAsync).ConfigureAwait(false);

        await FinishCleanupAsync(failures, pidLeft, routesLeft).ConfigureAwait(false);
    }

    /// <summary>Only a clean run drops the state file. Otherwise it shrinks to what still needs undoing.</summary>
    private async Task FinishCleanupAsync(List<string> failures, int? pidLeft, IReadOnlyList<string> routesLeft)
    {
        if (failures.Count == 0
            && await AttemptAsync(failures, "clear recovery state", _platform.ClearRecoveryStateAsync).ConfigureAwait(false))
        {
            _pendingCleanup = null;
            lock (_gate) _cleanupError = null;
            return;
        }

        var left = new RecoveryState(pidLeft, routesLeft);
        _pendingCleanup = left;
        await AttemptAsync(failures, "save recovery state",
            () => _platform.SaveRecoveryStateAsync(left, CancellationToken.None)).ConfigureAwait(false);
        lock (_gate) _cleanupError = "Cleanup incomplete, disconnect again to retry: " + string.Join("; ", failures);
    }

    private Task KillIfWstunnelAsync(int pid)
    {
        var name = _platform.GetProcessName(pid);
        return string.Equals(name, "wstunnel", StringComparison.OrdinalIgnoreCase)
            ? _platform.KillProcessAsync(pid)
            : Task.CompletedTask;
    }

    /// <returns>The IPs whose route may still be present.</returns>
    private async Task<List<string>> RemoveRoutesAsync(List<string> failures, IReadOnlyList<string> ips)
    {
        var left = new List<string>();
        foreach (var ip in ips)
        {
            if (!await AttemptAsync(failures, $"remove route {ip}", () => RemoveRouteAsync(ip)).ConfigureAwait(false))
                left.Add(ip);
        }
        return left;
    }

    /// <summary>Deleting an absent route exits non-zero; that must not stay recorded forever.</summary>
    private async Task RemoveRouteAsync(string ip)
    {
        try
        {
            await _platform.RunProcessAsync(ProxyRoute.BuildRemove([ip])[0], CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            if (await _platform.HasHostRouteAsync(ip, CancellationToken.None).ConfigureAwait(false)) throw;
            _log($"Cleanup: route {ip} was already gone: {e.Message}");
        }
    }

    private async Task<bool> AttemptAsync(List<string> failures, string what, Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(false);
            return true;
        }
        catch (Exception e)
        {
            _log($"Cleanup: {what}: {e.Message}");
            failures.Add($"{what}: {e.Message}");
            return false;
        }
    }

    // ---- helpers ----

    private bool IsActive()
    {
        lock (_gate) return _state.State is TunnelState.Connecting or TunnelState.Connected;
    }

    /// <summary>
    /// Read from disk once, then kept in memory and updated by imports: status is polled, and each
    /// read would reopen the private-key file and could block an import's atomic replace.
    /// </summary>
    private async Task<string?> GetServerHostAsync()
    {
        lock (_gate)
        {
            if (_serverHostKnown) return _serverHost;
        }

        string? host;
        try
        {
            var json = await _platform.LoadBundleAsync(CancellationToken.None).ConfigureAwait(false);
            host = json == null ? null : BundleParser.Parse(json).Server.Host;
        }
        catch (BundleFormatException e)
        {
            _log($"The stored bundle is invalid: {e.Message}");
            host = null; // stays invalid until an import replaces it
        }
        catch (Exception e)
        {
            _log($"Could not read the stored bundle: {e.Message}");
            return null; // possibly transient: the next poll tries again
        }

        lock (_gate)
        {
            // An import that finished during the read is newer than what was read.
            if (!_serverHostKnown)
            {
                _serverHost = host;
                _serverHostKnown = true;
            }
            return _serverHost;
        }
    }

    private void Track(Task task)
    {
        lock (_gate) _background.Add(task);
        task.ContinueWith(
            t =>
            {
                lock (_gate) _background.Remove(t);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
