// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

using System.Buffers.Binary;
using System.Text;

namespace WgVpn.Core.Ipc;

public static class IpcConstants
{
    public const string PipeName = "WgVpn.Service";
}

/// <summary>Frames IPC messages on a stream: 4-byte little-endian length, then UTF-8 JSON. Shared by service and GUI.</summary>
public static class PipeFraming
{
    public const int MaxFrameBytes = 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <returns>The next message, or null if the peer closed cleanly between frames.</returns>
    /// <exception cref="IpcFormatException">Oversized, truncated or undecodable frame.</exception>
    public static async Task<IpcMessage?> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        var prefix = new byte[4];
        var got = await ReadFullyAsync(stream, prefix, ct).ConfigureAwait(false);
        if (got == 0) return null;
        if (got < prefix.Length) throw new IpcFormatException("Truncated frame length");

        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length < 0 || length > MaxFrameBytes) throw new IpcFormatException("Frame length out of range");

        var body = new byte[length];
        if (await ReadFullyAsync(stream, body, ct).ConfigureAwait(false) < length)
            throw new IpcFormatException("Truncated frame body");

        string json;
        try
        {
            json = StrictUtf8.GetString(body);
        }
        catch (ArgumentException e)
        {
            throw new IpcFormatException("Frame is not valid UTF-8", e);
        }
        return IpcSerializer.Deserialize(json);
    }

    /// <exception cref="IpcFormatException">The message exceeds the frame limit; nothing is written.</exception>
    public static async Task WriteAsync(Stream stream, IpcMessage message, CancellationToken ct = default)
    {
        var json = StrictUtf8.GetBytes(IpcSerializer.Serialize(message));
        if (json.Length > MaxFrameBytes) throw new IpcFormatException("Message exceeds the frame limit");

        // One buffer, one write: the frame never interleaves with another writer's partial frame.
        var frame = new byte[4 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, json.Length);
        json.CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<int> ReadFullyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
