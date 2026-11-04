// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

namespace WgVpn.Core;

/// <summary>Builds the wstunnel v11 client argv. v11.1.0 has no cert-pin flag, so no TLS flag is emitted.</summary>
public static class WstunnelArgs
{
    public const string ClientCommand = "client";
    public const string LocalForwardFlag = "-L";
    public const string HttpProxyFlag = "--http-proxy";

    /// <param name="proxyAddress">Optional IP replacing the proxy host (port unchanged), to avoid re-resolving DNS through the tunnel.</param>
    public static string[] Build(ConnectionBundle b, string? proxyAddress = null)
    {
        var proxyHost = proxyAddress ?? b.Proxy.Host;
        return new[]
        {
            ClientCommand,
            LocalForwardFlag, $"udp://{b.Wstunnel.LocalUdpPort}:{b.Wstunnel.RemoteTarget}?timeout_sec=0",
            HttpProxyFlag, $"http://{proxyHost}:{b.Proxy.Port}",
            $"wss://{b.Server.Host}:{b.Server.Port}",
        };
    }
}
