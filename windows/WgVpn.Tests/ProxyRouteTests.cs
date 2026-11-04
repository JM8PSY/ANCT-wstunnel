// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

using WgVpn.Core;

namespace WgVpn.Tests;

public class ProxyRouteTests
{
    private static readonly string[] Ips = ["203.0.113.9", "203.0.113.10"];

    [Fact]
    public void BuildAdd_one_host_route_per_ip()
    {
        var specs = ProxyRoute.BuildAdd(Ips, "192.168.1.1", "12");
        Assert.Equal(2, specs.Count);
        Assert.All(specs, s => Assert.Equal("route", s.FileName));
        Assert.Equal(new[] { "add", "203.0.113.9", "mask", "255.255.255.255", "192.168.1.1", "if", "12" }, specs[0].Args);
        Assert.Equal(new[] { "add", "203.0.113.10", "mask", "255.255.255.255", "192.168.1.1", "if", "12" }, specs[1].Args);
    }

    [Fact]
    public void BuildRemove_matching_delete_per_ip()
    {
        var specs = ProxyRoute.BuildRemove(Ips);
        Assert.Equal(2, specs.Count);
        Assert.All(specs, s => Assert.Equal("route", s.FileName));
        Assert.Equal(new[] { "delete", "203.0.113.9", "mask", "255.255.255.255" }, specs[0].Args);
        Assert.Equal(new[] { "delete", "203.0.113.10", "mask", "255.255.255.255" }, specs[1].Args);
    }

    [Fact]
    public void Empty_input_yields_empty_output()
    {
        Assert.Empty(ProxyRoute.BuildAdd([], "192.168.1.1", "12"));
        Assert.Empty(ProxyRoute.BuildRemove([]));
    }
}
