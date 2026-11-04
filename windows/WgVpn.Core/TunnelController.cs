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

    private readonly SemaphoreSlim _opLock = new(1, 1);

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
        public bool TunnelInstallAttempted;
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

    /// <summary>Works in any state. Completes after teardown.</summary>
    public async Task<StatusReply> DisconnectAsync()
    {
        Session? ended;
        lock (_gate)
        {
            _state.Disconnect();
            ended = RetireCurrent();
        }
        ended?.Cancel();
        await DrainRetiredUnderOpLockAsync().ConfigureAwait(false);
        return await GetStatusAsync().ConfigureAwait(false);
    }

    public async Task<StatusReply> GetStatusAsync()
    {
        var serverHost = await TryReadServerHostAsync().ConfigureAwait(false);
        lock (_gate) return new StatusReply(_state.State, serverHost, _lastError);
    }

    public async Task<ImportBundleReply> ImportBundleAsync(string bundleJson)
    {
        if (IsActive()) return new ImportBundleReply(false, DisconnectFirst);
        try
        {
            BundleParser.Parse(bundleJson);
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

    /// <summary>Cleans up after a crashed service. Call once at service start.</summary>
    public async Task RecoverAsync()
    {
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
            if (state == null && !unreadable) return;

            _log("Recovery: cleaning up after an unclean stop.");
            await AttemptAsync("uninstall tunnel", () => _platform.UninstallTunnelAsync(CancellationToken.None)).ConfigureAwait(false);
            if (state?.WstunnelPid is int pid)
            {
                var name = _platform.GetProcessName(pid);
                if (string.Equals(name, "wstunnel", StringComparison.OrdinalIgnoreCase))
                    await AttemptAsync("kill wstunnel", () => _platform.KillProcessAsync(pid)).ConfigureAwait(false);
            }
            await RemoveRoutesAsync(state?.RouteIps ?? []).ConfigureAwait(false);
            await AttemptAsync("delete conf", _platform.DeleteConfAsync).ConfigureAwait(false);
            await AttemptAsync("clear recovery state", _platform.ClearRecoveryStateAsync).ConfigureAwait(false);
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
        await _platform.UninstallTunnelAsync(ct).ConfigureAwait(false); // leftover from an earlier run
        s.TunnelInstallAttempted = true;
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
            await _platform.RunProcessAsync(add[0], ct).ConfigureAwait(false);
            s.AddedRoutes.Add(ip);
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

    /// <summary>Reverses setup, undoing only what this session started. Every step is attempted.</summary>
    private async Task TeardownAsync(Session s)
    {
        if (s.TunnelInstallAttempted)
            await AttemptAsync("uninstall tunnel", () => _platform.UninstallTunnelAsync(CancellationToken.None)).ConfigureAwait(false);
        if (s.Wstunnel is { } handle)
        {
            await AttemptAsync("kill wstunnel", handle.KillAndWaitAsync).ConfigureAwait(false);
            await AttemptAsync("release wstunnel handle", () => { handle.Dispose(); return Task.CompletedTask; }).ConfigureAwait(false);
        }
        await RemoveRoutesAsync(s.AddedRoutes).ConfigureAwait(false);
        if (s.ConfWritten)
            await AttemptAsync("delete conf", _platform.DeleteConfAsync).ConfigureAwait(false);
        if (s.RecoveryStarted)
            await AttemptAsync("clear recovery state", _platform.ClearRecoveryStateAsync).ConfigureAwait(false);
    }

    private async Task RemoveRoutesAsync(IReadOnlyList<string> ips)
    {
        foreach (var spec in ProxyRoute.BuildRemove(ips))
            await AttemptAsync("remove route", () => _platform.RunProcessAsync(spec, CancellationToken.None)).ConfigureAwait(false);
    }

    private async Task AttemptAsync(string what, Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _log($"Teardown: {what}: {e.Message}");
        }
    }

    // ---- helpers ----

    private bool IsActive()
    {
        lock (_gate) return _state.State is TunnelState.Connecting or TunnelState.Connected;
    }

    private async Task<string?> TryReadServerHostAsync()
    {
        try
        {
            var json = await _platform.LoadBundleAsync(CancellationToken.None).ConfigureAwait(false);
            return json == null ? null : BundleParser.Parse(json).Server.Host;
        }
        catch (Exception e)
        {
            _log($"Could not read the stored bundle: {e.Message}");
            return null;
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
