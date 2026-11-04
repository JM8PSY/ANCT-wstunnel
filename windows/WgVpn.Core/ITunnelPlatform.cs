// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

namespace WgVpn.Core;

/// <summary>Outcome of the pin preflight: the proxy IP that connected and the server's leaf certificate.</summary>
public sealed record PreflightResult(string SelectedProxyIp, byte[] LeafCertDer);

/// <summary>What the OS routing table would use to reach a destination.</summary>
public sealed record BestRoute(string NextHop, int IfIndex, bool IsDirect)
{
    /// <summary>On-link destinations already have a LAN prefix more specific than the tunnel's /1 routes.</summary>
    public bool IsOnLink(string destination) => IsDirect || NextHop == "0.0.0.0" || NextHop == destination;
}

/// <summary>What a crashed service left behind. A null PID means wstunnel may have been starting.</summary>
public sealed record RecoveryState(int? WstunnelPid, IReadOnlyList<string> RouteIps);

public interface IWstunnelHandle : IDisposable
{
    int ProcessId { get; }

    /// <summary>Completes with the exit code when the process ends, for whatever reason.</summary>
    Task<int> Exited { get; }

    /// <summary>Last lines of wstunnel output, for LastError on an early exit.</summary>
    string OutputTail { get; }

    Task KillAndWaitAsync();
}

/// <summary>
/// Every side effect of the tunnel workflow. TunnelController enforces timeouts by cancelling
/// the token, so implementations must honour it. Expected failures throw TunnelException with
/// a user-readable message; no message may contain private keys or bundle contents.
/// </summary>
public interface ITunnelPlatform
{
    /// <returns>IPv4 addresses only.</returns>
    Task<IReadOnlyList<string>> ResolveProxyIpv4Async(string host, CancellationToken ct);

    /// <summary>TLS through proxy CONNECT to the server. The first proxy IP that connects is the selected one.</summary>
    Task<PreflightResult> PinPreflightAsync(ConnectionBundle bundle, IReadOnlyList<string> proxyIps, CancellationToken ct);

    Task<IWstunnelHandle> StartWstunnelAsync(string[] args, CancellationToken ct);

    /// <summary>Completes once the port is listening; waits until cancelled otherwise.</summary>
    Task WaitForUdpListenerAsync(int port, CancellationToken ct);

    Task<BestRoute> GetBestRouteAsync(string destination, CancellationToken ct);

    /// <exception cref="TunnelException">The process could not start or exited non-zero.</exception>
    Task RunProcessAsync(ProcessSpec spec, CancellationToken ct);

    Task WriteConfAsync(string content, CancellationToken ct);

    /// <summary>Deletes the conf; absent is not an error.</summary>
    Task DeleteConfAsync();

    /// <summary>Installs the fixed-name <c>wgvpn</c> tunnel from the written conf.</summary>
    Task InstallTunnelAsync(CancellationToken ct);

    /// <summary>Uninstalls the <c>wgvpn</c> tunnel; absent is not an error.</summary>
    Task UninstallTunnelAsync(CancellationToken ct);

    /// <summary>Raw <c>wg show wgvpn latest-handshakes</c> output; empty while the interface does not exist yet.</summary>
    Task<string> GetLatestHandshakesAsync(CancellationToken ct);

    Task SaveRecoveryStateAsync(RecoveryState state, CancellationToken ct);

    /// <returns>Null when no recovery state exists.</returns>
    Task<RecoveryState?> LoadRecoveryStateAsync();

    Task ClearRecoveryStateAsync();

    /// <returns>Null when no process has that PID.</returns>
    string? GetProcessName(int pid);

    Task KillProcessAsync(int pid);

    /// <returns>Null when no bundle has been imported.</returns>
    Task<string?> LoadBundleAsync(CancellationToken ct);

    /// <summary>Stores atomically.</summary>
    Task SaveBundleAsync(string bundleJson, CancellationToken ct);
}
