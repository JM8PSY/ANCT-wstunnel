// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using WgVpn.Core;

namespace WgVpn.Service;

/// <summary>
/// The real side effects behind <see cref="ITunnelPlatform"/>. No orchestration belongs here.
/// Compile-only until the Task 9 E2E: exercising it needs admin rights and wstunnel.exe.
/// </summary>
public sealed class WindowsTunnelPlatform : ITunnelPlatform
{
    private const string TunnelName = "wgvpn";
    private const string TunnelServiceName = "WireGuardTunnel$" + TunnelName;
    private const string WireguardMissing = "WireGuard for Windows is not installed.";

    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ProxyConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan UdpPollInterval = TimeSpan.FromMilliseconds(200);

    private readonly ILogger<WindowsTunnelPlatform> _logger;
    private readonly string _dataDir;
    private readonly string _confPath;
    private readonly string _statePath;
    private readonly string _bundlePath;
    private readonly string _wireguardExe;
    private readonly string _wgExe;

    public WindowsTunnelPlatform(ILogger<WindowsTunnelPlatform> logger)
    {
        _logger = logger;
        _dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WgVpn");
        _confPath = Path.Combine(_dataDir, TunnelName + ".conf");
        _statePath = Path.Combine(_dataDir, "state.json");
        _bundlePath = Path.Combine(_dataDir, "bundle.json");
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        _wireguardExe = Path.Combine(programFiles, "WireGuard", "wireguard.exe");
        _wgExe = Path.Combine(programFiles, "WireGuard", "wg.exe");
    }

    /// <summary>
    /// Creates the data directory, or takes it over if it exists: anyone may create folders under
    /// ProgramData, so a pre-existing one is untrusted. Owner and DACL are forced in both cases.
    /// </summary>
    public void PrepareDataDirectory()
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var security = new DirectorySecurity();
        security.SetOwner(admins);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { system, admins })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        }

        var dir = new DirectoryInfo(_dataDir);
        if (dir.Exists) dir.SetAccessControl(security);
        else dir.Create(security);
    }

    // ---- network ----

    public async Task<IReadOnlyList<string>> ResolveProxyIpv4Async(string host, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct).ConfigureAwait(false);
        return addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).Distinct().ToList();
    }

    public async Task<PreflightResult> PinPreflightAsync(ConnectionBundle bundle, IReadOnlyList<string> proxyIps, CancellationToken ct)
    {
        var (tcp, selected) = await ConnectToFirstProxyAsync(proxyIps, bundle.Proxy.Port, ct).ConfigureAwait(false);
        using var _ = tcp;

        var network = tcp.GetStream();
        await HttpConnect.EstablishAsync(network, bundle.Server.Host, bundle.Server.Port, ct).ConfigureAwait(false);

        byte[]? leaf = null;
        await using var tls = new SslStream(network, leaveInnerStreamOpen: true);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = bundle.Server.Host,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = (_, cert, _, _) =>
            {
                // Self-signed by design. Accept here; the caller compares this against the pin.
                leaf = cert?.GetRawCertData();
                return true;
            },
        }, ct).ConfigureAwait(false);

        return new PreflightResult(selected, leaf ?? throw new TunnelException("The server presented no TLS certificate."));
    }

    private static async Task<(TcpClient Client, string Ip)> ConnectToFirstProxyAsync(IReadOnlyList<string> proxyIps, int port, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var ip in proxyIps)
        {
            // A fresh client per attempt: a failed or cancelled connect leaves the socket unusable.
            var client = new TcpClient(AddressFamily.InterNetwork);
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(ProxyConnectTimeout);
            try
            {
                await client.ConnectAsync(IPAddress.Parse(ip), port, attempt.Token).ConfigureAwait(false);
                return (client, ip);
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException)
            {
                client.Dispose();
                ct.ThrowIfCancellationRequested();
                last = e;
            }
        }
        throw new TunnelException($"Could not connect to the proxy on port {port}.", last);
    }

    public async Task WaitForUdpListenerAsync(int port, CancellationToken ct)
    {
        var properties = IPGlobalProperties.GetIPGlobalProperties();
        while (!properties.GetActiveUdpListeners().Any(e => e.Port == port))
            await Task.Delay(UdpPollInterval, ct).ConfigureAwait(false);
    }

    public Task<BestRoute> GetBestRouteAsync(string destination, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var dest = BitConverter.ToUInt32(IPAddress.Parse(destination).GetAddressBytes());
        var error = NativeMethods.GetBestRoute(dest, 0, out var row);
        if (error != 0)
            throw new TunnelException($"No route to the proxy address {destination}: {new Win32Exception(error).Message}");

        var nextHop = new IPAddress(BitConverter.GetBytes(row.dwForwardNextHop)).ToString();
        return Task.FromResult(new BestRoute(nextHop, (int)row.dwForwardIfIndex, row.dwForwardType == NativeMethods.ForwardTypeDirect));
    }

    private static class NativeMethods
    {
        public const uint ForwardTypeDirect = 3;

        [StructLayout(LayoutKind.Sequential)]
        public struct MibIpForwardRow
        {
            public uint dwForwardDest;
            public uint dwForwardMask;
            public uint dwForwardPolicy;
            public uint dwForwardNextHop;
            public uint dwForwardIfIndex;
            public uint dwForwardType;
            public uint dwForwardProto;
            public uint dwForwardAge;
            public uint dwForwardNextHopAS;
            public uint dwForwardMetric1;
            public uint dwForwardMetric2;
            public uint dwForwardMetric3;
            public uint dwForwardMetric4;
            public uint dwForwardMetric5;
        }

        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        public static extern int GetBestRoute(uint dwDestAddr, uint dwSourceAddr, out MibIpForwardRow pBestRoute);
    }

    // ---- wstunnel ----

    public Task<IWstunnelHandle> StartWstunnelAsync(string[] args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var psi = NewStartInfo(Path.Combine(AppContext.BaseDirectory, "wstunnel.exe"), args);
        psi.Environment["NO_COLOR"] = "1"; // keeps escape codes out of LastError

        var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Win32Exception e)
        {
            process.Dispose();
            throw new TunnelException($"Could not start wstunnel.exe: {e.Message}", e);
        }
        return Task.FromResult<IWstunnelHandle>(new WstunnelHandle(process, _logger));
    }

    private sealed class WstunnelHandle : IWstunnelHandle
    {
        private const int TailLines = 20;
        private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(10);

        private readonly Process _process;
        private readonly ILogger _logger;
        private readonly Queue<string> _tail = new();

        public WstunnelHandle(Process process, ILogger logger)
        {
            _process = process;
            _logger = logger;
            ProcessId = process.Id;
            process.OutputDataReceived += (_, e) => OnLine(e.Data);
            process.ErrorDataReceived += (_, e) => OnLine(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            Exited = WaitForExitCodeAsync();
        }

        public int ProcessId { get; }
        public Task<int> Exited { get; }

        public string OutputTail
        {
            get { lock (_tail) return string.Join('\n', _tail); }
        }

        private async Task<int> WaitForExitCodeAsync()
        {
            // Also waits for the output to drain, so the tail is complete when this completes.
            await _process.WaitForExitAsync().ConfigureAwait(false);
            return _process.ExitCode;
        }

        private void OnLine(string? line)
        {
            if (line == null) return;
            _logger.LogInformation("wstunnel: {Line}", line);
            lock (_tail)
            {
                _tail.Enqueue(line);
                while (_tail.Count > TailLines) _tail.Dequeue();
            }
        }

        public async Task KillAndWaitAsync()
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
            await Exited.WaitAsync(KillWait).ConfigureAwait(false);
        }

        public void Dispose()
        {
            // Exited reads ExitCode from the process, so it must finish first. A process that
            // survived the kill keeps its handle; it is only one handle and the failure was logged.
            if (Exited.IsCompleted) _process.Dispose();
        }
    }

    // ---- external commands ----

    public async Task RunProcessAsync(ProcessSpec spec, CancellationToken ct)
    {
        // Absolute path: a LocalSystem service must not find its tools through a search path.
        if (spec.FileName != "route") throw new TunnelException($"Unexpected command: {spec.FileName}");
        var result = await RunCapturedAsync(Path.Combine(Environment.SystemDirectory, "route.exe"), spec.Args, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new TunnelException($"route {spec.Args[0]} {spec.Args[1]} failed with exit code {result.ExitCode}: {result.Output.Trim()}");
    }

    public async Task InstallTunnelAsync(CancellationToken ct)
    {
        EnsureWireguardInstalled();
        var result = await RunCapturedAsync(_wireguardExe, ["/installtunnelservice", _confPath], ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new TunnelException($"Installing the WireGuard tunnel failed with exit code {result.ExitCode}: {result.Output.Trim()}");
    }

    public async Task UninstallTunnelAsync(CancellationToken ct)
    {
        // wireguard.exe treats an unknown tunnel as a fatal error, so ask the service manager first.
        if (!TunnelServiceExists()) return;
        EnsureWireguardInstalled();
        var result = await RunCapturedAsync(_wireguardExe, ["/uninstalltunnelservice", TunnelName], ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new TunnelException($"Uninstalling the WireGuard tunnel failed with exit code {result.ExitCode}: {result.Output.Trim()}");
    }

    public async Task<string> GetLatestHandshakesAsync(CancellationToken ct)
    {
        EnsureWireguardInstalled();
        var result = await RunCapturedAsync(_wgExe, ["show", TunnelName, "latest-handshakes"], ct).ConfigureAwait(false);
        return result.ExitCode == 0 ? result.Output : string.Empty; // non-zero: the interface is not up yet
    }

    private static bool TunnelServiceExists()
    {
        var services = ServiceController.GetServices();
        try
        {
            return services.Any(s => string.Equals(s.ServiceName, TunnelServiceName, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            foreach (var s in services) s.Dispose();
        }
    }

    private void EnsureWireguardInstalled()
    {
        if (!File.Exists(_wireguardExe) || !File.Exists(_wgExe)) throw new TunnelException(WireguardMissing);
    }

    private readonly record struct ProcessResult(int ExitCode, string Output);

    /// <summary>
    /// Runs a short-lived tool, bounded by a timeout so that a tool blocked in session 0 (for
    /// example on an error dialog nobody can see) cannot wedge the op lock forever.
    /// </summary>
    private static async Task<ProcessResult> RunCapturedAsync(string fileName, IEnumerable<string> args, CancellationToken ct)
    {
        using var process = new Process { StartInfo = NewStartInfo(fileName, args) };
        try
        {
            process.Start();
        }
        catch (Win32Exception e)
        {
            throw new TunnelException($"Could not start {Path.GetFileName(fileName)}: {e.Message}", e);
        }

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(ProcessTimeout);
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(bounded.Token);
            var stderr = process.StandardError.ReadToEndAsync(bounded.Token);
            await process.WaitForExitAsync(bounded.Token).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            var errors = await stderr.ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, output.Length > 0 ? output : errors);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
            ct.ThrowIfCancellationRequested();
            throw new TunnelException($"{Path.GetFileName(fileName)} did not finish in time.");
        }
    }

    private static ProcessStartInfo NewStartInfo(string fileName, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.SystemDirectory,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        return psi;
    }

    // ---- files ----

    public async Task WriteConfAsync(string content, CancellationToken ct) =>
        await WriteAtomicAsync(_confPath, content, ct).ConfigureAwait(false);

    public Task DeleteConfAsync()
    {
        File.Delete(_confPath);
        return Task.CompletedTask;
    }

    public async Task SaveRecoveryStateAsync(RecoveryState state, CancellationToken ct) =>
        await WriteAtomicAsync(_statePath, JsonSerializer.Serialize(state), ct).ConfigureAwait(false);

    public async Task<RecoveryState?> LoadRecoveryStateAsync()
    {
        if (!File.Exists(_statePath)) return null;
        var json = await File.ReadAllTextAsync(_statePath).ConfigureAwait(false);
        return JsonSerializer.Deserialize<RecoveryState>(json)
            ?? throw new InvalidDataException("The recovery state file is empty.");
    }

    public Task ClearRecoveryStateAsync()
    {
        File.Delete(_statePath);
        return Task.CompletedTask;
    }

    public async Task<string?> LoadBundleAsync(CancellationToken ct)
    {
        try
        {
            return await File.ReadAllTextAsync(_bundlePath, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    public async Task SaveBundleAsync(string bundleJson, CancellationToken ct) =>
        await WriteAtomicAsync(_bundlePath, bundleJson, ct).ConfigureAwait(false);

    /// <summary>Temp file in the protected directory, so it inherits the DACL before any content lands.</summary>
    private async Task WriteAtomicAsync(string path, string content, CancellationToken ct)
    {
        var temp = Path.Combine(_dataDir, Path.GetRandomFileName());
        try
        {
            await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false), ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    // ---- processes ----

    public string? GetProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public Task KillProcessAsync(int pid)
    {
        using var process = Process.GetProcessById(pid);
        process.Kill(entireProcessTree: true);
        return Task.CompletedTask;
    }
}
