// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

using WgVpn.Core;

namespace WgVpn.Tests;

public class WireguardConfTests
{
    private static ConnectionBundle Load() =>
        BundleParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "bundle-v1.json")));

    private static ConnectionBundle WithAllowed(string allowed)
    {
        var b = Load();
        return b with { Wireguard = b.Wireguard with { AllowedIps = allowed } };
    }

    private static string[] Lines(string s) => s.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    [Fact]
    public void Render_ContainsExpectedValues()
    {
        var b = Load();
        var conf = WireguardConf.Render(b);
        Assert.Contains("Endpoint = 127.0.0.1:51820", conf);
        Assert.Contains("MTU = 1280", conf);
        Assert.Contains("Address = 10.88.0.2/32", conf);
        Assert.Contains("DNS = 1.1.1.1", conf);
        Assert.Contains("PersistentKeepalive = 25", conf);
        Assert.Contains("PublicKey = " + b.Wireguard.ServerPublicKey, conf);
        Assert.Contains("PrivateKey = " + b.Wireguard.PrivateKey, conf);
    }

    [Fact]
    public void Render_HasSectionsInOrder()
    {
        var lines = Lines(WireguardConf.Render(Load()));
        var i = Array.IndexOf(lines, "[Interface]");
        var p = Array.IndexOf(lines, "[Peer]");
        Assert.True(i >= 0 && p > i);
        Assert.DoesNotContain("\r", WireguardConf.Render(Load()));
    }

    [Fact]
    public void Render_DefaultRouteSplitIntoTwoHalves()
    {
        var conf = WireguardConf.Render(Load());
        Assert.Contains("AllowedIPs = 0.0.0.0/1, 128.0.0.0/1", conf);
        Assert.DoesNotContain("0.0.0.0/0", conf);
    }

    [Fact]
    public void Render_NonDefaultAllowedIpsPassThrough()
    {
        Assert.Contains("AllowedIPs = 10.0.0.0/8", WireguardConf.Render(WithAllowed("10.0.0.0/8")));
    }

    [Fact]
    public void Render_Ipv6DefaultRouteSplitIntoTwoHalves()
    {
        var conf = WireguardConf.Render(WithAllowed("::/0"));
        Assert.Contains("AllowedIPs = ::/1, 8000::/1", conf);
        Assert.DoesNotContain("::/0", conf);
    }

    [Fact]
    public void Render_MixedListSplitsDefaultAndPreservesOrder()
    {
        var conf = WireguardConf.Render(WithAllowed("10.0.0.0/8,0.0.0.0/0 , ::/0"));
        Assert.Contains("AllowedIPs = 10.0.0.0/8, 0.0.0.0/1, 128.0.0.0/1, ::/1, 8000::/1", conf);
    }
}
