// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using WgVpn.Core;
using WgVpn.Core.Ipc;

namespace WgVpn.Service;

/// <summary>Serves IPC over the named pipe. A misbehaving client only ever loses its own connection.</summary>
public sealed class PipeServer
{
    /// <summary>A client silent this long loses its connection, so it cannot pin an instance forever.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    private readonly TunnelController _controller;
    private readonly ILogger<PipeServer> _logger;

    public PipeServer(TunnelController controller, ILogger<PipeServer> logger)
    {
        _controller = controller;
        _logger = logger;
    }

    /// <summary>
    /// Accepts until cancelled. Throws if the first instance cannot be created: that means someone
    /// else owns the pipe name, and clients must not be left talking to a squatter unnoticed.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var clients = new HashSet<Task>();
        var listener = CreateInstance(first: true);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var connected = listener;
                listener = null;
                try
                {
                    await connected.WaitForConnectionAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await connected.DisposeAsync().ConfigureAwait(false);
                    break;
                }
                catch (IOException e)
                {
                    _logger.LogWarning("Pipe accept failed: {Error}", e.Message);
                    await connected.DisposeAsync().ConfigureAwait(false);
                    listener = await CreateInstanceWithRetryAsync(ct).ConfigureAwait(false);
                    continue;
                }

                // The next instance exists before this client can finish. If the name ever had no
                // instance, another process could create one and impersonate the service.
                listener = TryCreateInstance();
                clients.RemoveWhere(t => t.IsCompleted);
                clients.Add(ServeClientAsync(connected, ct));
                listener ??= await CreateInstanceWithRetryAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopping.
        }
        finally
        {
            if (listener != null) await listener.DisposeAsync().ConfigureAwait(false);
            await Task.WhenAll(clients).ConfigureAwait(false);
        }
    }

    private async Task ServeClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using (pipe.ConfigureAwait(false))
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idle.CancelAfter(IdleTimeout);
                    var request = await PipeFraming.ReadAsync(pipe, idle.Token).ConfigureAwait(false);
                    if (request == null) return;

                    var reply = await DispatchAsync(request).ConfigureAwait(false);
                    if (reply == null)
                    {
                        _logger.LogWarning("Pipe client sent a message that is not a request; closing.");
                        return;
                    }

                    // A write blocks until the client reads, so a client that never does is bounded too.
                    using var writeLimit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    writeLimit.CancelAfter(IdleTimeout);
                    await PipeFraming.WriteAsync(pipe, reply, writeLimit.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Stopping, or an idle client.
            }
            catch (IpcFormatException e)
            {
                // The message describes the frame, never its content.
                _logger.LogWarning("Pipe client sent a bad frame: {Error}", e.Message);
            }
            catch (IOException)
            {
                // The client went away mid-frame.
            }
            catch (Exception e)
            {
                _logger.LogError("Pipe request failed: {Error}", e.Message);
            }
        }
    }

    private async Task<IpcMessage?> DispatchAsync(IpcMessage request) => request switch
    {
        ConnectRequest => await _controller.ConnectAsync().ConfigureAwait(false),
        DisconnectRequest => await _controller.DisconnectAsync().ConfigureAwait(false),
        GetStatusRequest => await _controller.GetStatusAsync().ConfigureAwait(false),
        ImportBundleRequest import => await _controller.ImportBundleAsync(import.BundleJson).ConfigureAwait(false),
        _ => null,
    };

    private NamedPipeServerStream? TryCreateInstance()
    {
        try
        {
            return CreateInstance(first: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not create a pipe instance: {Error}", e.Message);
            return null;
        }
    }

    private async Task<NamedPipeServerStream> CreateInstanceWithRetryAsync(CancellationToken ct)
    {
        while (true)
        {
            var instance = TryCreateInstance();
            if (instance != null) return instance;
            await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
        }
    }

    private static NamedPipeServerStream CreateInstance(bool first)
    {
        var options = PipeOptions.Asynchronous;
        if (first) options |= PipeOptions.FirstPipeInstance;
        return NamedPipeServerStreamAcl.Create(
            IpcConstants.PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            options,
            inBufferSize: 0,
            outBufferSize: 0,
            CreateSecurity());
    }

    private static PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        // ReadWrite deliberately excludes CreateNewInstance: the GUI's user cannot add server instances.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }
}
