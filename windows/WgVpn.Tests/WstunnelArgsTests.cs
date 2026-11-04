// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

using WgVpn.Core;

namespace WgVpn.Tests;

public class WstunnelArgsTests
{
    private static ConnectionBundle Sample() => BundleParser.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "bundle-v1.json")));

    [Fact]
    public void Build_emits_exact_argv()
    {
        Assert.Equal(new[]
        {
            "client", "-L", "udp://51820:localhost:51820?timeout_sec=0",
            "--http-proxy", "http://websec.asahikawa-nct.ac.jp:8080",
            "wss://example.ddns.net:443",
        }, WstunnelArgs.Build(Sample()));
    }

    [Fact]
    public void Build_has_no_tls_verify_flag()
    {
        Assert.DoesNotContain("--tls-verify-certificate", WstunnelArgs.Build(Sample()));
    }

    [Fact]
    public void Proxy_address_override_replaces_host_keeps_port()
    {
        var a = WstunnelArgs.Build(Sample(), "203.0.113.7");
        Assert.Contains("http://203.0.113.7:8080", a);
        Assert.DoesNotContain(a, x => x.Contains("websec"));
    }
}
