// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

using WgVpn.Core;
using WgVpn.Core.Ipc;

namespace WgVpn.Tests;

public class StatusPresenterTests
{
    [Fact]
    public void Idle_offers_connect_only()
    {
        var v = StatusPresenter.FromReply(new StatusReply(TunnelState.Disconnected, null, null));
        Assert.Equal("未接続", v.StatusText);
        Assert.True(v.ToggleEnabled);
        Assert.False(v.ToggleChecked);
        Assert.False(v.DisconnectOffered);
        Assert.True(v.ConnectOffered);
        Assert.Equal(TrayState.Idle, v.Tray);
        Assert.Null(v.ErrorText);
        Assert.Equal("接続先: -", v.ServerText);
    }

    [Fact]
    public void Connecting_is_checked_and_disconnectable()
    {
        var v = StatusPresenter.FromReply(new StatusReply(TunnelState.Connecting, "vpn.example.org", null));
        Assert.Equal("接続中", v.StatusText);
        Assert.True(v.ToggleChecked);
        Assert.True(v.DisconnectOffered);
        Assert.False(v.ConnectOffered);
        Assert.Equal(TrayState.Connecting, v.Tray);
        Assert.Equal("接続先: vpn.example.org", v.ServerText);
    }

    [Fact]
    public void Connected()
    {
        var v = StatusPresenter.FromReply(new StatusReply(TunnelState.Connected, "h", null));
        Assert.Equal("接続済み", v.StatusText);
        Assert.True(v.ToggleChecked);
        Assert.True(v.DisconnectOffered);
        Assert.Equal(TrayState.Connected, v.Tray);
    }

    [Fact]
    public void Error_shows_text_and_offers_disconnect()
    {
        var v = StatusPresenter.FromReply(new StatusReply(TunnelState.Error, "h", "boom"));
        Assert.Equal("エラー", v.StatusText);
        Assert.Equal("boom", v.ErrorText);
        Assert.True(v.DisconnectOffered);
        Assert.Equal(TrayState.Error, v.Tray);
    }

    [Fact]
    public void Disconnected_with_cleanup_error_still_offers_disconnect()
    {
        var v = StatusPresenter.FromReply(new StatusReply(TunnelState.Disconnected, null,
            "Cleanup incomplete, disconnect again to retry: x"));
        Assert.Equal("未接続", v.StatusText);
        Assert.True(v.DisconnectOffered);
        Assert.NotNull(v.ErrorText);
        Assert.True(v.ToggleEnabled);
        Assert.Equal(TrayState.Error, v.Tray);
    }

    [Fact]
    public void Unreachable_disables_everything()
    {
        var v = StatusPresenter.ServiceUnavailable();
        Assert.Equal("バックグラウンドサービスが起動していません", v.StatusText);
        Assert.False(v.ToggleEnabled);
        Assert.False(v.ToggleChecked);
        Assert.False(v.DisconnectOffered);
        Assert.False(v.ConnectOffered);
        Assert.Equal(TrayState.ServiceUnavailable, v.Tray);
    }
}
