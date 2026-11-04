// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

using WgVpn.Core.Ipc;

namespace WgVpn.Core;

/// <summary>Which tray icon / colour to show.</summary>
public enum TrayState { Idle, Connecting, Connected, Error, ServiceUnavailable }

/// <summary>Everything the GUI displays for one status poll.</summary>
public sealed record StatusView(
    string StatusText,
    bool ToggleEnabled,
    bool ToggleChecked,
    string ServerText,
    string? ErrorText,
    TrayState Tray,
    bool ConnectOffered,
    bool DisconnectOffered);

/// <summary>Pure mapping from a service reply (or its absence) to display data.</summary>
public static class StatusPresenter
{
    public const string ServiceDownText = "バックグラウンドサービスが起動していません";

    public static StatusView ServiceUnavailable() =>
        new(ServiceDownText, ToggleEnabled: false, ToggleChecked: false, "接続先: -", null,
            TrayState.ServiceUnavailable, ConnectOffered: false, DisconnectOffered: false);

    public static StatusView FromReply(StatusReply reply)
    {
        var server = "接続先: " + (string.IsNullOrWhiteSpace(reply.ServerHost) ? "-" : reply.ServerHost);
        var error = string.IsNullOrWhiteSpace(reply.LastError) ? null : reply.LastError;
        return reply.State switch
        {
            TunnelState.Connecting => new("接続中", true, true, server, error, TrayState.Connecting, false, true),
            TunnelState.Connected => new("接続済み", true, true, server, error, TrayState.Connected, false, true),
            // ToggleChecked false: one click on the toggle retries Connect; Disconnect stays offered.
            TunnelState.Error => new("エラー", true, false, server, error, TrayState.Error, true, true),
            // Disconnected with an error means a failed cleanup: the user must be able to retry Disconnect.
            _ => new("未接続", true, false, server, error,
                error == null ? TrayState.Idle : TrayState.Error, true, error != null),
        };
    }
}
