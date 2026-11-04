// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

namespace WgVpn.Core;

/// <summary>Builds route.exe commands pinning proxy IPs to the physical LAN path. Pure; non-persistent (no -p).</summary>
public static class ProxyRoute
{
    private const string Mask = "255.255.255.255";

    public static IReadOnlyList<ProcessSpec> BuildAdd(IReadOnlyList<string> proxyIps, string gateway, string ifIndex) =>
        proxyIps.Select(ip => new ProcessSpec("route", ["add", ip, "mask", Mask, gateway, "if", ifIndex])).ToList();

    public static IReadOnlyList<ProcessSpec> BuildRemove(IReadOnlyList<string> proxyIps) =>
        proxyIps.Select(ip => new ProcessSpec("route", ["delete", ip, "mask", Mask])).ToList();
}
