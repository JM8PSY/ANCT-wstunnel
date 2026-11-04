// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

using System.Text;
using WgVpn.Core;
using WgVpn.Core.Ipc;

namespace WgVpn.Tests;

public class PipeFramingTests
{
    private static MemoryStream Frame(byte[] payload, int? declaredLength = null)
    {
        var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes(declaredLength ?? payload.Length));
        ms.Write(payload);
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void Pipe_name_is_fixed() => Assert.Equal("WgVpn.Service", IpcConstants.PipeName);

    [Fact]
    public async Task Round_trip_on_stream()
    {
        var ms = new MemoryStream();
        await PipeFraming.WriteAsync(ms, new StatusReply(TunnelState.Connected, "h.example", null));
        await PipeFraming.WriteAsync(ms, new DisconnectRequest());
        ms.Position = 0;
        Assert.Equal(new StatusReply(TunnelState.Connected, "h.example", null), await PipeFraming.ReadAsync(ms));
        Assert.Equal(new DisconnectRequest(), await PipeFraming.ReadAsync(ms));
    }

    [Fact]
    public async Task Prefix_is_little_endian_length_of_utf8_json()
    {
        var ms = new MemoryStream();
        var msg = new StatusReply(TunnelState.Error, null, "日本語");
        await PipeFraming.WriteAsync(ms, msg);
        var bytes = ms.ToArray();
        var expected = Encoding.UTF8.GetByteCount(IpcSerializer.Serialize(msg));
        Assert.Equal(new[] { (byte)expected, (byte)(expected >> 8), (byte)(expected >> 16), (byte)(expected >> 24) }, bytes[..4]);
        Assert.Equal(expected, bytes.Length - 4);
    }

    [Fact]
    public async Task Clean_eof_before_a_frame_returns_null()
    {
        Assert.Null(await PipeFraming.ReadAsync(new MemoryStream()));
    }

    [Fact]
    public async Task Oversized_prefix_is_rejected_without_reading_body()
    {
        var ms = Frame([], PipeFraming.MaxFrameBytes + 1);
        await Assert.ThrowsAsync<IpcFormatException>(() => PipeFraming.ReadAsync(ms));
    }

    [Fact]
    public async Task Negative_prefix_is_rejected()
    {
        var ms = Frame([], -1);
        await Assert.ThrowsAsync<IpcFormatException>(() => PipeFraming.ReadAsync(ms));
    }

    [Fact]
    public async Task Frame_of_exactly_the_limit_reaches_the_parser()
    {
        // Valid JSON padded with whitespace to exactly the limit: accepted.
        var payload = Encoding.UTF8.GetBytes("{\"type\":\"getStatus\"}".PadRight(PipeFraming.MaxFrameBytes));
        Assert.Equal(new GetStatusRequest(), await PipeFraming.ReadAsync(Frame(payload)));
    }

    [Fact]
    public async Task Truncated_prefix_is_rejected()
    {
        var ms = new MemoryStream(new byte[] { 1, 0 });
        await Assert.ThrowsAsync<IpcFormatException>(() => PipeFraming.ReadAsync(ms));
    }

    [Fact]
    public async Task Truncated_body_is_rejected()
    {
        var ms = Frame(Encoding.UTF8.GetBytes("{\"ty"), 100);
        await Assert.ThrowsAsync<IpcFormatException>(() => PipeFraming.ReadAsync(ms));
    }

    [Fact]
    public async Task Invalid_utf8_is_rejected()
    {
        var ms = Frame(new byte[] { 0xC3, 0x28 });
        await Assert.ThrowsAsync<IpcFormatException>(() => PipeFraming.ReadAsync(ms));
    }

    [Fact]
    public async Task Bad_json_is_rejected()
    {
        var ms = Frame(Encoding.UTF8.GetBytes("nope"));
        await Assert.ThrowsAsync<IpcFormatException>(() => PipeFraming.ReadAsync(ms));
    }

    [Fact]
    public async Task Write_rejects_oversized_message_and_writes_nothing()
    {
        var ms = new MemoryStream();
        var big = new ImportBundleRequest(new string('x', PipeFraming.MaxFrameBytes));
        await Assert.ThrowsAsync<IpcFormatException>(() => PipeFraming.WriteAsync(ms, big));
        Assert.Equal(0, ms.Length);
    }
}
