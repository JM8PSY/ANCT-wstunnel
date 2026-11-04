// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

using System.Text.Json.Nodes;
using WgVpn.Core;

namespace WgVpn.Tests;

public class BundleParserTests
{
    private static string Fixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "bundle-v1.json"));

    private static string Mutate(Action<JsonObject> edit)
    {
        var node = JsonNode.Parse(Fixture())!.AsObject();
        edit(node);
        return node.ToJsonString();
    }

    private static BundleFormatException Fails(string json) =>
        Assert.Throws<BundleFormatException>(() => BundleParser.Parse(json));

    [Fact]
    public void Parses_fixture()
    {
        var b = BundleParser.Parse(Fixture());
        Assert.Equal("example.ddns.net", b.Server.Host);
        Assert.Equal(443, b.Server.Port);
        Assert.Equal(64, b.Server.TlsSha256.Length);
        Assert.Equal(1280, b.Wireguard.Mtu);
        Assert.Equal(25, b.Wireguard.PersistentKeepalive);
        Assert.Equal(8080, b.Proxy.Port);
        Assert.Equal("websec.asahikawa-nct.ac.jp", b.Proxy.Host);
        Assert.Equal(51820, b.Wstunnel.LocalUdpPort);
        Assert.Equal("localhost:51820", b.Wstunnel.RemoteTarget);
        Assert.Equal("10.88.0.2/32", b.Wireguard.Address);
    }

    [Fact]
    public void Unknown_version_names_version()
    {
        var ex = Fails(Mutate(o => o["version"] = 2));
        Assert.Contains("2", ex.Message);
        Assert.Contains("version", ex.Message);
    }

    [Fact]
    public void Missing_version_throws() => Fails(Mutate(o => o.Remove("version")));

    [Fact]
    public void Missing_private_key_throws_naming_field()
    {
        var ex = Fails(Mutate(o => o["wireguard"]!.AsObject().Remove("privateKey")));
        Assert.Contains("privateKey", ex.Message);
    }

    [Fact]
    public void Missing_section_throws() => Fails(Mutate(o => o.Remove("proxy")));

    [Fact]
    public void Empty_string_throws() =>
        Fails(Mutate(o => o["server"]!["host"] = ""));

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("null")]
    [InlineData("\"x\"")]
    [InlineData("{\"version\":")]
    public void Malformed_or_non_object_json_throws(string json) => Fails(json);

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\u0000b")]
    [InlineData("a\tb")]
    public void Control_characters_rejected_without_leaking_value(string bad)
    {
        var ex = Fails(Mutate(o => o["wireguard"]!["privateKey"] = bad));
        Assert.Contains("privateKey", ex.Message);
    }

    [Fact]
    public void Injection_in_dns_rejected() =>
        Fails(Mutate(o => o["wireguard"]!["dns"] = "1.1.1.1\nPostUp = calc.exe"));

    [Fact]
    public void Error_message_never_contains_secret_value()
    {
        const string secret = "SUPERSECRETKEYVALUE\n";
        var ex = Fails(Mutate(o => o["wireguard"]!["privateKey"] = secret));
        Assert.DoesNotContain("SUPERSECRET", ex.Message);
    }

    [Theory]
    [InlineData("server", "port", 0)]
    [InlineData("server", "port", 65536)]
    [InlineData("server", "port", -1)]
    [InlineData("proxy", "port", 0)]
    [InlineData("proxy", "port", 70000)]
    [InlineData("wstunnel", "localUdpPort", 0)]
    [InlineData("wstunnel", "localUdpPort", 65536)]
    [InlineData("wireguard", "mtu", 575)]
    [InlineData("wireguard", "mtu", 65536)]
    [InlineData("wireguard", "persistentKeepalive", -1)]
    [InlineData("wireguard", "persistentKeepalive", 65536)]
    public void Out_of_range_numbers_throw(string section, string field, int value)
    {
        var ex = Fails(Mutate(o => o[section]![field] = value));
        Assert.Contains(field, ex.Message);
    }

    [Theory]
    [InlineData("server", "port", 1)]
    [InlineData("server", "port", 65535)]
    [InlineData("wireguard", "mtu", 576)]
    [InlineData("wireguard", "mtu", 65535)]
    [InlineData("wireguard", "persistentKeepalive", 0)]
    [InlineData("wireguard", "persistentKeepalive", 65535)]
    public void Boundary_numbers_accepted(string section, string field, int value) =>
        BundleParser.Parse(Mutate(o => o[section]![field] = value));

    [Fact]
    public void Non_integer_number_throws() =>
        Fails(Mutate(o => o["server"]!["port"] = 443.5));

    [Fact]
    public void Wrong_type_throws()
    {
        Fails(Mutate(o => o["server"]!["port"] = "443"));
        Fails(Mutate(o => o["server"]!["host"] = 5));
        Fails(Mutate(o => o["server"] = "x"));
        Fails(Mutate(o => o["version"] = "1"));
    }
}
