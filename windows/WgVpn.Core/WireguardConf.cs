// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

using System.Text;

namespace WgVpn.Core;

public static class WireguardConf
{
    /// <summary>Renders a wireguard.exe-compatible .conf (LF line endings). Endpoint is the local wstunnel UDP listener.</summary>
    public static string Render(ConnectionBundle b)
    {
        var w = b.Wireguard;
        var sb = new StringBuilder();
        sb.Append("[Interface]\n");
        sb.Append("PrivateKey = ").Append(w.PrivateKey).Append('\n');
        sb.Append("Address = ").Append(w.Address).Append('\n');
        sb.Append("DNS = ").Append(w.Dns).Append('\n');
        sb.Append("MTU = ").Append(w.Mtu).Append('\n');
        sb.Append('\n');
        sb.Append("[Peer]\n");
        sb.Append("PublicKey = ").Append(w.ServerPublicKey).Append('\n');
        sb.Append("Endpoint = 127.0.0.1:").Append(b.Wstunnel.LocalUdpPort).Append('\n');
        sb.Append("AllowedIPs = ").Append(RenderAllowedIps(w.AllowedIps)).Append('\n');
        sb.Append("PersistentKeepalive = ").Append(w.PersistentKeepalive).Append('\n');
        return sb.ToString();
    }

    // WireGuard for Windows turns on its WFP kill-switch when a single peer has a /0 AllowedIP.
    // That blocks wstunnel.exe's LAN TCP to the proxy regardless of the host route, so the tunnel
    // could never carry its own transport. The /1 pair routes identically without the kill-switch.
    private static string RenderAllowedIps(string allowedIps)
    {
        var parts = new List<string>();
        foreach (var raw in allowedIps.Split(','))
        {
            var entry = raw.Trim();
            if (entry.Length == 0) continue;
            if (entry == "0.0.0.0/0")
            {
                parts.Add("0.0.0.0/1");
                parts.Add("128.0.0.0/1");
            }
            else
            {
                parts.Add(entry);
            }
        }
        return string.Join(", ", parts);
    }
}
