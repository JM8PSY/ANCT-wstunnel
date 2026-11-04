// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

using System.Globalization;
using System.Text.Json;

namespace WgVpn.Core;

public static class BundleParser
{
    public static ConnectionBundle Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json ?? string.Empty);
        }
        catch (JsonException)
        {
            throw new BundleFormatException("Bundle is not valid JSON.");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new BundleFormatException("Bundle must be a JSON object.");

            if (!root.TryGetProperty("version", out var v))
                throw new BundleFormatException("Missing required field: version.");
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version))
                throw new BundleFormatException("Field version must be an integer.");
            if (version != 1)
                throw new BundleFormatException(
                    $"Unsupported bundle version {version.ToString(CultureInfo.InvariantCulture)} (only version 1 is supported).");

            var server = Section(root, "server");
            var proxy = Section(root, "proxy");
            var wst = Section(root, "wstunnel");
            var wg = Section(root, "wireguard");

            return new ConnectionBundle(
                new ServerInfo(
                    Str(server, "server", "host"),
                    Int(server, "server", "port", 1, 65535),
                    Str(server, "server", "tlsSha256")),
                new ProxyInfo(
                    Str(proxy, "proxy", "host"),
                    Int(proxy, "proxy", "port", 1, 65535)),
                new WstunnelInfo(
                    Int(wst, "wstunnel", "localUdpPort", 1, 65535),
                    Str(wst, "wstunnel", "remoteTarget")),
                new WireguardInfo(
                    Str(wg, "wireguard", "privateKey"),
                    Str(wg, "wireguard", "serverPublicKey"),
                    Str(wg, "wireguard", "address"),
                    Str(wg, "wireguard", "allowedIps"),
                    Str(wg, "wireguard", "dns"),
                    Int(wg, "wireguard", "mtu", 576, 65535),
                    Int(wg, "wireguard", "persistentKeepalive", 0, 65535)));
        }
    }

    private static JsonElement Section(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var e))
            throw new BundleFormatException($"Missing required section: {name}.");
        if (e.ValueKind != JsonValueKind.Object)
            throw new BundleFormatException($"Section {name} must be an object.");
        return e;
    }

    private static string Str(JsonElement section, string sectionName, string field)
    {
        var path = $"{sectionName}.{field}";
        if (!section.TryGetProperty(field, out var e))
            throw new BundleFormatException($"Missing required field: {path}.");
        if (e.ValueKind != JsonValueKind.String)
            throw new BundleFormatException($"Field {path} must be a string.");
        var s = e.GetString();
        if (string.IsNullOrWhiteSpace(s))
            throw new BundleFormatException($"Field {path} must not be empty.");
        foreach (var c in s)
        {
            if (char.IsControl(c) || char.GetUnicodeCategory(c) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                throw new BundleFormatException($"Field {path} contains control characters.");
        }
        return s;
    }

    private static int Int(JsonElement section, string sectionName, string field, int min, int max)
    {
        var path = $"{sectionName}.{field}";
        if (!section.TryGetProperty(field, out var e))
            throw new BundleFormatException($"Missing required field: {path}.");
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt32(out var n))
            throw new BundleFormatException($"Field {path} must be an integer.");
        if (n < min || n > max)
            throw new BundleFormatException($"Field {path} is out of range ({min}-{max}).");
        return n;
    }
}
