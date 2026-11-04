// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

using System.IO;
using System.IO.Pipes;
using WgVpn.Core.Ipc;

namespace WgVpn.App;

/// <summary>Outcome of one request. Never an exception.</summary>
public abstract record PipeResult
{
    public sealed record Reply(IpcMessage Message) : PipeResult;
    /// <summary>The service could not be reached or broke the protocol.</summary>
    public sealed record ServiceUnavailable(string Reason) : PipeResult;
    /// <summary>The request itself was refused locally (e.g. too large); the service was not at fault.</summary>
    public sealed record Rejected(string Reason) : PipeResult;
}

/// <summary>One short-lived pipe connection per request, so a hung request never blocks polling.</summary>
public sealed class PipeClient
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    public async Task<PipeResult> SendAsync(IpcMessage request, TimeSpan requestTimeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(requestTimeout);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", IpcConstants.PipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token))
            {
                connectCts.CancelAfter(ConnectTimeout);
                await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);
            }
            try
            {
                await PipeFraming.WriteAsync(pipe, request, cts.Token).ConfigureAwait(false);
            }
            catch (IpcFormatException e) when (e.Message.Contains("frame limit"))
            {
                return new PipeResult.Rejected("リクエストが大きすぎます");
            }
            var reply = await PipeFraming.ReadAsync(pipe, cts.Token).ConfigureAwait(false);
            return reply == null
                ? new PipeResult.ServiceUnavailable("service closed the connection")
                : new PipeResult.Reply(reply);
        }
        catch (OperationCanceledException)
        {
            return ct.IsCancellationRequested
                ? new PipeResult.ServiceUnavailable("cancelled")
                : new PipeResult.ServiceUnavailable("timeout");
        }
        catch (Exception e)
        {
            // Contract: never throw to the UI, whatever the framing/JSON/pipe layers raise.
            return new PipeResult.ServiceUnavailable(e.GetType().Name);
        }
    }
}
