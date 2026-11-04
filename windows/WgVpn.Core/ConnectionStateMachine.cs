// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

namespace WgVpn.Core;

/// <summary>Thread-safe tunnel connection state (pipe handler and process monitor call concurrently).</summary>
public sealed class ConnectionStateMachine
{
    private readonly object _gate = new();
    private TunnelState _state = TunnelState.Disconnected;

    public TunnelState State { get { lock (_gate) return _state; } }

    /// <summary>True only if this call moved to Connecting (caller must start the work).</summary>
    public bool Connect()
    {
        lock (_gate)
        {
            if (_state is TunnelState.Connecting or TunnelState.Connected) return false;
            _state = TunnelState.Connecting;
            return true;
        }
    }

    public void OnTunnelUp()
    {
        lock (_gate) { if (_state == TunnelState.Connecting) _state = TunnelState.Connected; }
    }

    public void Disconnect()
    {
        lock (_gate) _state = TunnelState.Disconnected;
    }

    public void OnProcessExited() => ToErrorIfActive();

    /// <summary>A failure other than the wstunnel process dying. No-op unless Connecting or Connected.</summary>
    public void OnFailed() => ToErrorIfActive();

    private void ToErrorIfActive()
    {
        lock (_gate)
        {
            if (_state is TunnelState.Connecting or TunnelState.Connected) _state = TunnelState.Error;
        }
    }
}
