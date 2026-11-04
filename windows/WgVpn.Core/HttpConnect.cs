// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

using System.Globalization;
using System.Text;

namespace WgVpn.Core;

/// <summary>HTTP CONNECT client handshake used by the pin preflight. Plain host+port and Host header only.</summary>
public static class HttpConnect
{
    public const int MaxResponseHeaderBytes = 16 * 1024;

    /// <summary>
    /// Sends CONNECT (as HTTP/1.0, like wstunnel, so the preflight rehearses its real request) and requires a complete 200 response. Reads one byte at a time so that
    /// no byte of the TLS stream following the header is consumed.
    /// </summary>
    /// <exception cref="TunnelException">The proxy refused, or did not answer with valid HTTP.</exception>
    public static async Task EstablishAsync(Stream stream, string host, int port, CancellationToken ct)
    {
        var authority = host + ":" + port.ToString(CultureInfo.InvariantCulture);
        var request = Encoding.ASCII.GetBytes($"CONNECT {authority} HTTP/1.0\r\nHost: {authority}\r\n\r\n");
        await stream.WriteAsync(request, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        var statusLine = await ReadStatusLineAndSkipHeadersAsync(stream, ct).ConfigureAwait(false);
        if (!IsStatus200(statusLine))
            throw new TunnelException($"Proxy refused CONNECT: {statusLine}");
    }

    private static bool IsStatus200(string statusLine)
    {
        var parts = statusLine.Split(' ', 3);
        return parts.Length >= 2 && parts[0].StartsWith("HTTP/1.", StringComparison.Ordinal) && parts[1] == "200";
    }

    private static async Task<string> ReadStatusLineAndSkipHeadersAsync(Stream stream, CancellationToken ct)
    {
        var header = new List<byte>(256);
        var one = new byte[1];
        while (true)
        {
            var n = await stream.ReadAsync(one, ct).ConfigureAwait(false);
            if (n == 0)
                throw new TunnelException("Proxy closed the connection during CONNECT.");
            if (header.Count >= MaxResponseHeaderBytes)
                throw new TunnelException("Proxy CONNECT response header is too large.");
            header.Add(one[0]);

            var c = header.Count;
            if (c >= 4 && header[c - 4] == '\r' && header[c - 3] == '\n' && header[c - 2] == '\r' && header[c - 1] == '\n')
            {
                var text = Encoding.ASCII.GetString(header.ToArray());
                return text[..text.IndexOf('\r', StringComparison.Ordinal)];
            }
        }
    }
}
