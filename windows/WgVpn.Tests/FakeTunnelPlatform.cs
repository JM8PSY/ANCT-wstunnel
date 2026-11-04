// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

using WgVpn.Core;

namespace WgVpn.Tests;

/// <summary>Records every platform call by name; steps can be failed or held at a gate.</summary>
internal sealed class FakeTunnelPlatform : ITunnelPlatform
{
    public const string Ip1 = "192.0.2.1";
    public const string Ip2 = "192.0.2.2";
    public const string PrivateKey = "PRIVATE-KEY-MUST-NOT-LEAK";

    public const string ValidBundleJson =
        "{\"version\":1,\"server\":{\"host\":\"vpn.example\",\"port\":443,\"tlsSha256\":\"" + CertSha256 + "\"}," +
        "\"proxy\":{\"host\":\"proxy.example\",\"port\":8080}," +
        "\"wstunnel\":{\"localUdpPort\":51820,\"remoteTarget\":\"localhost:51820\"}," +
        "\"wireguard\":{\"privateKey\":\"" + PrivateKey + "\",\"serverPublicKey\":\"pub\",\"address\":\"10.88.0.2/32\"," +
        "\"allowedIps\":\"0.0.0.0/0\",\"dns\":\"1.1.1.1\",\"mtu\":1280,\"persistentKeepalive\":25}}";

    // SHA-256 of the three bytes 01 02 03.
    public const string CertSha256 = "039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81";

    private readonly object _gate = new();
    private readonly List<string> _calls = [];
    private readonly Dictionary<string, int> _failures = [];
    private readonly Dictionary<string, TaskCompletionSource> _holds = [];
    private readonly Dictionary<string, TaskCompletionSource> _stubbornHolds = [];
    private readonly Dictionary<string, TaskCompletionSource> _reached = [];
    private readonly HashSet<string> _usedHolds = [];
    private readonly HashSet<string> _toolLimits = [];
    private int _nextPid = 1000;

    /// <summary>The real routing table: what route.exe has committed.</summary>
    public HashSet<string> HostRoutes = [];

    /// <summary>When a route add is interrupted, whether route.exe had already committed it.</summary>
    public bool RouteAddCommitsInFlight = true;

    public int BundleLoads;
    public bool ProcessNameThrows;

    public string? BundleJson = ValidBundleJson;
    public IReadOnlyList<string> Ips = [Ip1, Ip2];
    public byte[] LeafCert = [1, 2, 3];
    public Func<string, BestRoute> Route = _ => new BestRoute("10.0.0.1", 7, false);
    public string Handshakes = "pub\t1762230000\n";
    public RecoveryState? Recovery;
    public bool RecoveryCorrupt;
    public Dictionary<int, string> ProcessNames = [];
    public string? WrittenConf;
    public string[]? WstunnelArgv;
    public List<FakeHandle> Handles = [];
    public List<RecoveryState> SavedStates = [];
    public int MaxLiveWstunnels;

    public IReadOnlyList<string> Calls { get { lock (_gate) return _calls.ToList(); } }

    /// <summary>Calls recorded after the first occurrence of <paramref name="step"/>.</summary>
    public IReadOnlyList<string> CallsAfter(string step)
    {
        var calls = Calls;
        return calls.Skip(calls.ToList().IndexOf(step) + 1).ToList();
    }

    public void Fail(string step, int times = 1) { lock (_gate) _failures[step] = times; }

    /// <summary>The tool does not finish in time, so the platform cannot say whether it took effect.</summary>
    public void FailWithToolLimit(string step)
    {
        lock (_gate)
        {
            _failures[step] = 1;
            _toolLimits.Add(step);
        }
    }

    /// <summary>Holds the next call of the step until released; the hold observes cancellation.</summary>
    public void Hold(string step)
    {
        lock (_gate)
        {
            _holds[step] = NewTcs();
            _usedHolds.Remove(step);
        }
    }

    public void Release(string step) { lock (_gate) _holds[step].TrySetResult(); }

    /// <summary>Holds the step and ignores cancellation, like a call that cannot be interrupted.</summary>
    public void HoldStubbornly(string step) { lock (_gate) _stubbornHolds[step] = NewTcs(); }

    public void ReleaseStubborn(string step) { lock (_gate) _stubbornHolds[step].TrySetResult(); }

    /// <summary>Completes when the step is first called. The cap only bounds a failing test.</summary>
    public Task Reached(string step)
    {
        lock (_gate)
        {
            if (_calls.Contains(step)) return Task.CompletedTask;
            if (!_reached.TryGetValue(step, out var tcs)) _reached[step] = tcs = NewTcs();
            return tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static TaskCompletionSource NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task Hit(string step, CancellationToken ct = default)
    {
        Task? hold = null, stubborn = null;
        var fail = false;
        lock (_gate)
        {
            _calls.Add(step);
            if (_reached.TryGetValue(step, out var r)) r.TrySetResult();
            if (_holds.TryGetValue(step, out var h) && _usedHolds.Add(step)) hold = h.Task;
            if (_stubbornHolds.TryGetValue(step, out var s)) stubborn = s.Task;
            if (_failures.TryGetValue(step, out var n) && n > 0)
            {
                _failures[step] = n - 1;
                fail = true;
            }
        }
        if (stubborn != null) await stubborn;
        if (hold != null) await hold.WaitAsync(ct);
        if (fail) throw new TunnelException($"{step} failed");
        if (stubborn == null) ct.ThrowIfCancellationRequested(); // a stubborn call returns its result regardless
    }

    public async Task<IReadOnlyList<string>> ResolveProxyIpv4Async(string host, CancellationToken ct)
    {
        await Hit("resolve", ct);
        return Ips;
    }

    public async Task<PreflightResult> PinPreflightAsync(ConnectionBundle bundle, IReadOnlyList<string> proxyIps, CancellationToken ct)
    {
        await Hit("preflight", ct);
        return new PreflightResult(proxyIps[0], LeafCert);
    }

    public async Task<IWstunnelHandle> StartWstunnelAsync(string[] args, CancellationToken ct)
    {
        await Hit("start-wstunnel", ct);
        var handle = new FakeHandle(this, ++_nextPid);
        lock (_gate)
        {
            WstunnelArgv = args;
            Handles.Add(handle);
            var live = Handles.Count(x => !x.Killed && !x.Exited.IsCompleted);
            MaxLiveWstunnels = Math.Max(MaxLiveWstunnels, live);
        }
        return handle;
    }

    public Task WaitForUdpListenerAsync(int port, CancellationToken ct) => Hit("wait-udp", ct);

    public async Task<BestRoute> GetBestRouteAsync(string destination, CancellationToken ct)
    {
        await Hit("best-route " + destination, ct);
        return Route(destination);
    }

    public async Task<bool> HasHostRouteAsync(string destination, CancellationToken ct)
    {
        await Hit("has-route " + destination, ct);
        lock (_gate) return HostRoutes.Contains(destination);
    }

    /// <summary>Models route.exe: a failed step exits non-zero, deleting an absent route exits non-zero.</summary>
    public async Task RunProcessAsync(ProcessSpec spec, CancellationToken ct)
    {
        var step = spec.FileName + " " + string.Join(' ', spec.Args);
        var ip = spec.Args[1];
        var isAdd = spec.Args[0] == "add";
        try
        {
            await Hit(step, ct);
        }
        catch (TunnelException e)
        {
            lock (_gate)
            {
                if (!_toolLimits.Remove(step)) throw new ProcessFailedException(e.Message);
                if (isAdd && RouteAddCommitsInFlight) HostRoutes.Add(ip);
            }
            throw;
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                if (isAdd && RouteAddCommitsInFlight) HostRoutes.Add(ip);
            }
            throw;
        }

        lock (_gate)
        {
            if (isAdd) HostRoutes.Add(ip);
            else if (!HostRoutes.Remove(ip)) throw new ProcessFailedException($"route delete {ip}: element not found");
        }
    }

    public async Task WriteConfAsync(string content, CancellationToken ct)
    {
        await Hit("write-conf", ct);
        WrittenConf = content;
    }

    public Task DeleteConfAsync() => Hit("delete-conf");
    public Task InstallTunnelAsync(CancellationToken ct) => Hit("install", ct);
    public Task UninstallTunnelAsync(CancellationToken ct) => Hit("uninstall", ct);

    public async Task<string> GetLatestHandshakesAsync(CancellationToken ct)
    {
        await Hit("handshake", ct);
        return Handshakes;
    }

    public async Task SaveRecoveryStateAsync(RecoveryState state, CancellationToken ct)
    {
        await Hit("save-recovery", ct);
        lock (_gate) SavedStates.Add(state);
        Recovery = state;
    }

    public async Task<RecoveryState?> LoadRecoveryStateAsync()
    {
        await Hit("load-recovery");
        if (RecoveryCorrupt) throw new InvalidDataException("state.json is corrupt");
        return Recovery;
    }

    public async Task ClearRecoveryStateAsync()
    {
        await Hit("clear-recovery");
        Recovery = null;
    }

    public string? GetProcessName(int pid)
    {
        if (ProcessNameThrows) throw new InvalidOperationException("process has exited");
        return ProcessNames.GetValueOrDefault(pid);
    }
    public Task KillProcessAsync(int pid) => Hit("kill-pid " + pid);

    public async Task<string?> LoadBundleAsync(CancellationToken ct)
    {
        await Task.CompletedTask;
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref BundleLoads);
        return BundleJson;
    }

    public async Task SaveBundleAsync(string bundleJson, CancellationToken ct)
    {
        await Hit("save-bundle", ct);
        BundleJson = bundleJson;
    }

    internal sealed class FakeHandle(FakeTunnelPlatform owner, int pid) : IWstunnelHandle
    {
        // Deliberately without RunContinuationsAsynchronously: the exit handler's synchronous
        // part has run by the time Exit() returns, which keeps exit tests deterministic.
        private readonly TaskCompletionSource<int> _exited = new();

        public int ProcessId { get; } = pid;
        public Task<int> Exited => _exited.Task;
        public string OutputTail { get; set; } = "";
        public bool Killed { get; private set; }

        /// <summary>When true, Kill leaves the exit pending so a test can deliver a late, stale exit.</summary>
        public bool DeferExitOnKill { get; set; }

        public void Exit(int code) => _exited.TrySetResult(code);

        public async Task KillAndWaitAsync()
        {
            Killed = true;
            await owner.Hit("kill-wstunnel");
            if (!DeferExitOnKill) Exit(-1);
        }

        public void Dispose() { }
    }
}
