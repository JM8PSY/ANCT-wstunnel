// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

using System.Text.Json;
using WgVpn.Core;
using WgVpn.Core.Ipc;

namespace WgVpn.Tests;

public class IpcMessagesTests
{
    public static IEnumerable<object[]> All() => new[]
    {
        new object[] { new ConnectRequest(), "connect" },
        new object[] { new DisconnectRequest(), "disconnect" },
        new object[] { new GetStatusRequest(), "getStatus" },
        new object[] { new ImportBundleRequest("{\"version\":1}"), "importBundle" },
        new object[] { new ImportBundleReply(false, "bad"), "importBundleReply" },
        new object[] { new StatusReply(TunnelState.Connected, "h.example", null), "status" },
    };

    [Theory]
    [MemberData(nameof(All))]
    public void RoundTrip_preserves_type_and_value(IpcMessage msg, string disc)
    {
        var json = IpcSerializer.Serialize(msg);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(disc, doc.RootElement.GetProperty("type").GetString());
        var back = IpcSerializer.Deserialize(json);
        Assert.Equal(msg.GetType(), back.GetType());
        Assert.Equal(msg, back);
    }

    [Fact]
    public void TunnelState_serialized_as_string()
    {
        var json = IpcSerializer.Serialize(new StatusReply(TunnelState.Error, null, "x"));
        Assert.Contains("\"Error\"", json);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("{\"type\":\"bogus\"}")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"type\":\"status\",\"state\":\"Nope\"}")]
    public void Deserialize_bad_input_throws_IpcFormatException(string input)
    {
        Assert.Throws<IpcFormatException>(() => IpcSerializer.Deserialize(input));
    }

    [Theory]
    [InlineData("{\"type\":\"importBundle\"}")]
    [InlineData("{\"type\":\"importBundle\",\"bundleJson\":null}")]
    public void ImportBundleRequest_without_BundleJson_is_rejected(string input)
    {
        Assert.Throws<IpcFormatException>(() => IpcSerializer.Deserialize(input));
    }

    [Fact]
    public void ImportBundleRequest_ToString_redacts_BundleJson()
    {
        var text = new ImportBundleRequest("{\"privateKey\":\"SECRET\"}").ToString();
        Assert.DoesNotContain("SECRET", text);
        Assert.DoesNotContain("privateKey", text);
    }
}
