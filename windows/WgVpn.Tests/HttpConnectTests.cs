// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

using System.Text;
using WgVpn.Core;

namespace WgVpn.Tests;

public class HttpConnectTests
{
    /// <summary>Scripted server replies on read; records what is written.</summary>
    private sealed class Duplex(byte[] toRead) : MemoryStream(toRead)
    {
        public MemoryStream Written { get; } = new();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            Written.Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Written.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
    }

    private static Duplex Reply(string s) => new(Encoding.ASCII.GetBytes(s));

    [Fact]
    public async Task Sends_only_request_line_and_Host_header()
    {
        var s = Reply("HTTP/1.1 200 Connection established\r\n\r\n");
        await HttpConnect.EstablishAsync(s, "vpn.example", 443, default);
        Assert.Equal(
            "CONNECT vpn.example:443 HTTP/1.1\r\nHost: vpn.example:443\r\n\r\n",
            Encoding.ASCII.GetString(s.Written.ToArray()));
    }

    [Theory]
    [InlineData("HTTP/1.1 200 OK\r\n\r\n")]
    [InlineData("HTTP/1.1 200\r\n\r\n")]
    [InlineData("HTTP/1.0 200 Connection established\r\nVia: proxy\r\n\r\n")]
    public async Task Accepts_200(string reply)
    {
        await HttpConnect.EstablishAsync(Reply(reply), "h", 443, default);
    }

    [Fact]
    public async Task Does_not_consume_bytes_after_the_header()
    {
        var s = Reply("HTTP/1.1 200 OK\r\n\r\nTLS");
        await HttpConnect.EstablishAsync(s, "h", 443, default);
        Assert.Equal('T', s.ReadByte());
    }

    [Theory]
    [InlineData("HTTP/1.1 403 Forbidden\r\n\r\n")]
    [InlineData("HTTP/1.1 407 Proxy Authentication Required\r\n\r\n")]
    [InlineData("HTTP/1.1 2000 OK\r\n\r\n")]
    [InlineData("garbage\r\n\r\n")]
    [InlineData("HTTP/1.1 200 OK\r\n")]
    [InlineData("")]
    public async Task Rejects_anything_but_a_complete_200(string reply)
    {
        await Assert.ThrowsAsync<TunnelException>(() => HttpConnect.EstablishAsync(Reply(reply), "h", 443, default));
    }

    [Fact]
    public async Task Rejects_endless_header()
    {
        var s = Reply("HTTP/1.1 200 OK\r\n" + new string('a', HttpConnect.MaxResponseHeaderBytes + 1));
        await Assert.ThrowsAsync<TunnelException>(() => HttpConnect.EstablishAsync(s, "h", 443, default));
    }
}
