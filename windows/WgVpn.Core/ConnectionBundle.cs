// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

namespace WgVpn.Core;

public sealed record ServerInfo(string Host, int Port, string TlsSha256);
public sealed record ProxyInfo(string Host, int Port);
public sealed record WstunnelInfo(int LocalUdpPort, string RemoteTarget);
public sealed record WireguardInfo(
    string PrivateKey,
    string ServerPublicKey,
    string Address,
    string AllowedIps,
    string Dns,
    int Mtu,
    int PersistentKeepalive);

public sealed record ConnectionBundle(
    ServerInfo Server,
    ProxyInfo Proxy,
    WstunnelInfo Wstunnel,
    WireguardInfo Wireguard);
