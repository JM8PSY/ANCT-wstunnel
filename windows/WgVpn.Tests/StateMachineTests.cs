// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

using WgVpn.Core;

namespace WgVpn.Tests;

public class StateMachineTests
{
    private static ConnectionStateMachine InState(TunnelState s)
    {
        var m = new ConnectionStateMachine();
        switch (s)
        {
            case TunnelState.Connecting: m.Connect(); break;
            case TunnelState.Connected: m.Connect(); m.OnTunnelUp(); break;
            case TunnelState.Error: m.Connect(); m.OnProcessExited(); break;
        }
        Assert.Equal(s, m.State);
        return m;
    }

    [Fact]
    public void Starts_disconnected() => Assert.Equal(TunnelState.Disconnected, new ConnectionStateMachine().State);

    [Fact]
    public void Connect_from_Disconnected_and_Error_transitions()
    {
        foreach (var s in new[] { TunnelState.Disconnected, TunnelState.Error })
        {
            var m = InState(s);
            Assert.True(m.Connect());
            Assert.Equal(TunnelState.Connecting, m.State);
        }
    }

    [Theory]
    [InlineData(TunnelState.Connecting)]
    [InlineData(TunnelState.Connected)]
    public void Connect_is_noop_when_active(TunnelState s)
    {
        var m = InState(s);
        Assert.False(m.Connect());
        Assert.Equal(s, m.State);
    }

    [Fact]
    public void OnTunnelUp_only_from_Connecting()
    {
        var m = InState(TunnelState.Connecting);
        m.OnTunnelUp();
        Assert.Equal(TunnelState.Connected, m.State);
        foreach (var s in new[] { TunnelState.Disconnected, TunnelState.Error })
        {
            var x = InState(s);
            x.OnTunnelUp();
            Assert.Equal(s, x.State);
        }
    }

    [Theory]
    [InlineData(TunnelState.Connecting)]
    [InlineData(TunnelState.Connected)]
    public void OnProcessExited_active_goes_to_Error(TunnelState s)
    {
        var m = InState(s);
        m.OnProcessExited();
        Assert.Equal(TunnelState.Error, m.State);
    }

    [Theory]
    [InlineData(TunnelState.Disconnected)]
    [InlineData(TunnelState.Error)]
    public void OnProcessExited_inactive_is_noop(TunnelState s)
    {
        var m = InState(s);
        m.OnProcessExited();
        Assert.Equal(s, m.State);
    }

    [Theory]
    [InlineData(TunnelState.Disconnected)]
    [InlineData(TunnelState.Connecting)]
    [InlineData(TunnelState.Connected)]
    [InlineData(TunnelState.Error)]
    public void Disconnect_from_any_state(TunnelState s)
    {
        var m = InState(s);
        m.Disconnect();
        Assert.Equal(TunnelState.Disconnected, m.State);
    }

    [Theory]
    [InlineData(TunnelState.Connecting)]
    [InlineData(TunnelState.Connected)]
    public void OnFailed_active_goes_to_Error(TunnelState s)
    {
        var m = InState(s);
        m.OnFailed();
        Assert.Equal(TunnelState.Error, m.State);
    }

    [Theory]
    [InlineData(TunnelState.Disconnected)]
    [InlineData(TunnelState.Error)]
    public void OnFailed_inactive_is_noop(TunnelState s)
    {
        var m = InState(s);
        m.OnFailed();
        Assert.Equal(s, m.State);
    }

    [Fact]
    public void Parallel_Connect_returns_true_exactly_once()
    {
        var m = new ConnectionStateMachine();
        int wins = 0;
        Parallel.For(0, 200, new ParallelOptions { MaxDegreeOfParallelism = 16 }, _ =>
        {
            if (m.Connect()) Interlocked.Increment(ref wins);
        });
        Assert.Equal(1, wins);
        Assert.Equal(TunnelState.Connecting, m.State);
    }
}
