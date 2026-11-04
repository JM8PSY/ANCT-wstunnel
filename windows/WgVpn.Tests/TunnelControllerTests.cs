// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

using WgVpn.Core;
using static WgVpn.Tests.FakeTunnelPlatform;

namespace WgVpn.Tests;

public class TunnelControllerTests
{
    private const string Mask = "mask 255.255.255.255";
    private static readonly string Add1 = $"route add {Ip1} {Mask} 10.0.0.1 if 7";
    private static readonly string Add2 = $"route add {Ip2} {Mask} 10.0.0.1 if 7";
    private static readonly string Del1 = $"route delete {Ip1} {Mask}";
    private static readonly string Del2 = $"route delete {Ip2} {Mask}";

    private readonly FakeTunnelPlatform _p = new();
    private readonly List<string> _log = [];

    private TunnelController NewController(TunnelTimeouts? timeouts = null) =>
        new(_p, timeouts ?? new TunnelTimeouts(
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(1)),
            m => { lock (_log) _log.Add(m); });

    private async Task<TunnelController> Connected()
    {
        var c = NewController();
        await c.ConnectAsync();
        await c.IdleAsync();
        Assert.Equal(TunnelState.Connected, (await c.GetStatusAsync()).State);
        return c;
    }

    // ---- success path ----

    [Fact]
    public async Task Success_path_runs_every_step_in_order()
    {
        await Connected();
        Assert.Equal(new[]
        {
            "resolve", "preflight",
            "save-recovery", "start-wstunnel", "save-recovery", "wait-udp",
            "best-route " + Ip1, "save-recovery", Add1,
            "best-route " + Ip2, "save-recovery", Add2,
            "write-conf", "uninstall", "install", "handshake",
        }, _p.Calls);
    }

    [Fact]
    public async Task Connect_replies_immediately_while_work_runs_in_background()
    {
        _p.Hold("resolve");
        var c = NewController();
        var reply = await c.ConnectAsync();
        Assert.Equal(TunnelState.Connecting, reply.State);
        Assert.Equal("vpn.example", reply.ServerHost);
        _p.Release("resolve");
        await c.IdleAsync();
        Assert.Equal(TunnelState.Connected, (await c.GetStatusAsync()).State);
    }

    [Fact]
    public async Task Wstunnel_uses_the_selected_proxy_ip_and_conf_is_rendered_from_the_bundle()
    {
        await Connected();
        var bundle = BundleParser.Parse(ValidBundleJson);
        Assert.Equal(WstunnelArgs.Build(bundle, Ip1), _p.WstunnelArgv);
        Assert.Equal(WireguardConf.Render(bundle), _p.WrittenConf);
    }

    [Fact]
    public async Task Recovery_state_names_each_side_effect_before_it_happens()
    {
        await Connected();
        var pid = _p.Handles[0].ProcessId;
        Assert.Equal(4, _p.SavedStates.Count);
        Assert.Null(_p.SavedStates[0].WstunnelPid);
        Assert.Empty(_p.SavedStates[0].RouteIps);
        Assert.Equal(pid, _p.SavedStates[1].WstunnelPid);
        Assert.Empty(_p.SavedStates[1].RouteIps);
        Assert.Equal(new[] { Ip1 }, _p.SavedStates[2].RouteIps);
        Assert.Equal(new[] { Ip1, Ip2 }, _p.SavedStates[3].RouteIps);
        Assert.Equal(pid, _p.SavedStates[3].WstunnelPid);
    }

    [Fact]
    public async Task Handshake_is_polled_until_seen()
    {
        _p.Handshakes = "pub\t0\n";
        _p.Hold("handshake");
        var c = NewController();
        await c.ConnectAsync();
        await _p.Reached("handshake");
        Assert.Equal(TunnelState.Connecting, (await c.GetStatusAsync()).State);
        _p.Release("handshake");
        await WaitForCount("handshake", 3);
        Assert.Equal(TunnelState.Connecting, (await c.GetStatusAsync()).State);
        _p.Handshakes = "pub\t5\n";
        await c.IdleAsync();
        Assert.Equal(TunnelState.Connected, (await c.GetStatusAsync()).State);
    }

    private async Task WaitForCount(string step, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_p.Calls.Count(x => x == step) < count)
        {
            Assert.True(DateTime.UtcNow < deadline, $"{step} was not called {count} times");
            await Task.Yield();
        }
    }

    // ---- duplicate guard ----

    [Fact]
    public async Task Duplicate_Connect_while_Connecting_starts_one_wstunnel()
    {
        _p.Hold("wait-udp");
        var c = NewController();
        await c.ConnectAsync();
        await _p.Reached("wait-udp");
        var second = await c.ConnectAsync();
        Assert.Equal(TunnelState.Connecting, second.State);
        _p.Release("wait-udp");
        await c.IdleAsync();
        Assert.Single(_p.Handles);
        Assert.Equal(TunnelState.Connected, (await c.GetStatusAsync()).State);
    }

    [Fact]
    public async Task Duplicate_Connect_while_Connected_starts_nothing()
    {
        var c = await Connected();
        var before = _p.Calls.Count;
        var reply = await c.ConnectAsync();
        await c.IdleAsync();
        Assert.Equal(TunnelState.Connected, reply.State);
        Assert.Equal(before, _p.Calls.Count);
    }

    // ---- failures ----

    public static IEnumerable<object[]> FailureCases()
    {
        string[] routes = [Del1, Del2];
        object[] Case(string step, string[] teardown) => [step, teardown];
        yield return Case("resolve", []);
        yield return Case("preflight", []);
        yield return Case("save-recovery", ["clear-recovery"]);
        yield return Case("start-wstunnel", ["clear-recovery"]);
        yield return Case("wait-udp", ["kill-wstunnel", "clear-recovery"]);
        yield return Case("best-route " + Ip1, ["kill-wstunnel", "clear-recovery"]);
        yield return Case(Add1, ["kill-wstunnel", "clear-recovery"]);
        yield return Case("write-conf", ["kill-wstunnel", .. routes, "delete-conf", "clear-recovery"]);
        // The leftover uninstall may be interrupted part-way, so teardown repeats it.
        yield return Case("uninstall", ["uninstall", "kill-wstunnel", .. routes, "delete-conf", "clear-recovery"]);
        yield return Case("install", ["uninstall", "kill-wstunnel", .. routes, "delete-conf", "clear-recovery"]);
        yield return Case("handshake", ["uninstall", "kill-wstunnel", .. routes, "delete-conf", "clear-recovery"]);
    }

    [Theory]
    [MemberData(nameof(FailureCases))]
    public async Task Failure_at_a_step_gives_Error_and_tears_down_only_what_started(string step, string[] expectedTeardown)
    {
        _p.Fail(step);
        var c = NewController();
        await c.ConnectAsync();
        await c.IdleAsync();
        var status = await c.GetStatusAsync();
        Assert.Equal(TunnelState.Error, status.State);
        Assert.Contains($"{step} failed", status.LastError);
        Assert.Equal(expectedTeardown, _p.CallsAfter(step));
    }

    [Fact]
    public async Task Failure_at_the_second_route_removes_only_the_first()
    {
        _p.Fail(Add2);
        var c = NewController();
        await c.ConnectAsync();
        await c.IdleAsync();
        Assert.Equal(new[] { "kill-wstunnel", Del1, "clear-recovery" }, _p.CallsAfter(Add2));
    }

    [Fact]
    public async Task No_bundle_gives_Error_and_starts_nothing()
    {
        _p.BundleJson = null;
        var c = NewController();
        await c.ConnectAsync();
        await c.IdleAsync();
        var status = await c.GetStatusAsync();
        Assert.Equal(TunnelState.Error, status.State);
        Assert.Equal("no bundle imported", status.LastError);
        Assert.Empty(_p.Calls);
    }

    [Fact]
    public async Task Stored_bundle_that_no_longer_parses_gives_Error_and_starts_nothing()
    {
        _p.BundleJson = "{\"version\":2}";
        var c = NewController();
        await c.ConnectAsync();
        await c.IdleAsync();
        Assert.Equal(TunnelState.Error, (await c.GetStatusAsync()).State);
        Assert.Empty(_p.Calls);
    }

    [Fact]
    public async Task No_proxy_address_gives_Error()
    {
        _p.Ips = [];
        var c = NewController();
        await c.ConnectAsync();
        await c.IdleAsync();
        var status = await c.GetStatusAsync();
        Assert.Equal(TunnelState.Error, status.State);
        Assert.Contains("proxy.example", status.LastError);
        Assert.Equal(new[] { "resolve" }, _p.Calls);
    }

    [Fact]
    public async Task Pin_mismatch_starts_nothing()
    {
        _p.LeafCert = [9, 9, 9];
        var c = NewController();
        await c.ConnectAsync();
        await c.IdleAsync();
        var status = await c.GetStatusAsync();
        Assert.Equal(TunnelState.Error, status.State);
        Assert.Contains("fingerprint", status.LastError);
        Assert.Equal(new[] { "resolve", "preflight" }, _p.Calls);
    }

    [Fact]
    public async Task No_handshake_gives_Error_and_full_teardown()
    {
        _p.Handshakes = "pub\t0\n";
        var c = NewController(new TunnelTimeouts(
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(1)));
        await c.ConnectAsync();
        await c.IdleAsync();
        var status = await c.GetStatusAsync();
        Assert.Equal(TunnelState.Error, status.State);
        Assert.Equal("WireGuard handshake timed out", status.LastError);
        Assert.Equal(new[] { "uninstall", "kill-wstunnel", Del1, Del2, "delete-conf", "clear-recovery" }, _p.Calls.TakeLast(6));
    }

    [Fact]
    public async Task Preflight_timeout_gives_Error()
    {
        _p.Hold("preflight");
        var c = NewController(new TunnelTimeouts(
            TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(1)));
        await c.ConnectAsync();
        await c.IdleAsync();
        var status = await c.GetStatusAsync();
        Assert.Equal(TunnelState.Error, status.State);
        Assert.Contains("timed out", status.LastError);
        Assert.Equal(new[] { "resolve", "preflight" }, _p.Calls);
    }

    [Fact]
    public async Task Udp_listener_timeout_gives_Error_and_kills_wstunnel()
    {
        _p.Hold("wait-udp");
        var c = NewController(new TunnelTimeouts(
            TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(1)));
        await c.ConnectAsync();
        await c.IdleAsync();
        var status = await c.GetStatusAsync();
        Assert.Equal(TunnelState.Error, status.State);
        Assert.Contains("timed out", status.LastError);
        Assert.Equal(new[] { "kill-wstunnel", "clear-recovery" }, _p.CallsAfter("wait-udp"));
    }

    [Fact]
    public async Task LastError_is_cleared_on_a_real_start()
    {
        _p.Fail("resolve");
        var c = NewController();
        await c.ConnectAsync();
        await c.IdleAsync();
        Assert.NotNull((await c.GetStatusAsync()).LastError);
        _p.Hold("resolve");
        var reply = await c.ConnectAsync();
        Assert.Null(reply.LastError);
        _p.Release("resolve");
        await c.IdleAsync();
        Assert.Equal(TunnelState.Connected, (await c.GetStatusAsync()).State);
    }

    [Fact]
    public async Task Unexpected_exception_is_a_failure_too()
    {
        _p.Route = _ => throw new InvalidOperationException("boom");
        var c = NewController();
        await c.ConnectAsync();
        await c.IdleAsync();
        var status = await c.GetStatusAsync();
        Assert.Equal(TunnelState.Error, status.State);
        Assert.Contains("boom", status.LastError);
    }

    [Fact]
    public async Task Private_key_never_reaches_status_or_log()
    {
        _p.Fail("install");
        var c = NewController();
        await c.ConnectAsync();
        await c.IdleAsync();
        Assert.Contains(PrivateKey, _p.WrittenConf);
        Assert.DoesNotContain(PrivateKey, (await c.GetStatusAsync()).ToString());
        lock (_log) Assert.DoesNotContain(_log, m => m.Contains(PrivateKey));
    }

    // ---- teardown ----

    public static IEnumerable<object[]> TeardownFailureCases()
    {
        const string has1 = "has-route " + Ip1;
        object[] Case(string failing, string[] expected) => [failing, expected];
        // The conf is kept while the tunnel may still be installed.
        yield return Case("uninstall", ["uninstall", "kill-wstunnel", Del1, Del2, "save-recovery"]);
        yield return Case("kill-wstunnel", ["uninstall", "kill-wstunnel", Del1, Del2, "delete-conf", "save-recovery"]);
        yield return Case(Del1, ["uninstall", "kill-wstunnel", Del1, has1, Del2, "delete-conf", "save-recovery"]);
        yield return Case("delete-conf", ["uninstall", "kill-wstunnel", Del1, Del2, "delete-conf", "save-recovery"]);
        yield return Case("clear-recovery", ["uninstall", "kill-wstunnel", Del1, Del2, "delete-conf", "clear-recovery", "save-recovery"]);
    }

    [Theory]
    [MemberData(nameof(TeardownFailureCases))]
    public async Task Teardown_continues_after_a_step_throws_and_keeps_the_state(string failing, string[] expected)
    {
        var c = await Connected();
        var before = _p.Calls.Count;
        _p.Fail(failing);
        var reply = await c.DisconnectAsync();
        Assert.Equal(TunnelState.Disconnected, reply.State);
        Assert.Contains($"{failing} failed", reply.LastError);
        Assert.Equal(expected, _p.Calls.Skip(before));
        Assert.NotNull(_p.Recovery);
        lock (_log) Assert.Contains(_log, m => m.Contains($"{failing} failed"));
    }

    [Fact]
    public async Task Failed_teardown_records_only_what_still_needs_undoing()
    {
        var c = await Connected();
        var pid = _p.Handles[0].ProcessId;
        _p.Fail("kill-wstunnel");
        _p.Fail(Del2);
        await c.DisconnectAsync();
        Assert.Equal(pid, _p.Recovery!.WstunnelPid);
        Assert.Equal(new[] { Ip2 }, _p.Recovery.RouteIps);
    }

    [Fact]
    public async Task Failed_uninstall_keeps_the_conf_and_records_nothing_else_as_left()
    {
        var c = await Connected();
        _p.Fail("uninstall");
        await c.DisconnectAsync();
        Assert.DoesNotContain("delete-conf", _p.Calls);
        Assert.Null(_p.Recovery!.WstunnelPid);
        Assert.Empty(_p.Recovery.RouteIps);
    }

    [Fact]
    public async Task Second_Disconnect_retries_a_failed_teardown_and_clears_the_error()
    {
        var c = await Connected();
        _p.Fail("uninstall");
        await c.DisconnectAsync();
        var before = _p.Calls.Count;

        var reply = await c.DisconnectAsync();

        Assert.Equal(new[] { "uninstall", "delete-conf", "clear-recovery" }, _p.Calls.Skip(before));
        Assert.Null(reply.LastError);
        Assert.Null(_p.Recovery);
        var after = _p.Calls.Count;
        await c.DisconnectAsync();
        Assert.Equal(after, _p.Calls.Count);
    }

    [Fact]
    public async Task Disconnect_clears_a_stale_connect_error()
    {
        _p.Fail("preflight");
        var c = NewController();
        await c.ConnectAsync();
        await c.IdleAsync();
        var failed = await c.GetStatusAsync();
        Assert.Equal(TunnelState.Error, failed.State);
        Assert.NotNull(failed.LastError);

        var reply = await c.DisconnectAsync();

        Assert.Equal(TunnelState.Disconnected, reply.State);
        Assert.Null(reply.LastError);
    }

    [Fact]
    public async Task Disconnect_keeps_the_cleanup_error()
    {
        var c = await Connected();
        _p.Fail("uninstall");
        var reply = await c.DisconnectAsync();
        Assert.Contains("Cleanup incomplete", reply.LastError);
    }

    [Fact]
    public async Task Retry_that_fails_again_keeps_state_and_error()
    {
        var c = await Connected();
        _p.Fail("uninstall", times: 2);
        await c.DisconnectAsync();
        var reply = await c.DisconnectAsync();
        Assert.Contains("uninstall failed", reply.LastError);
        Assert.NotNull(_p.Recovery);
        Assert.Null((await c.DisconnectAsync()).LastError);
        Assert.Null(_p.Recovery);
    }

    [Fact]
    public async Task Connect_retries_a_failed_teardown_before_its_own_steps()
    {
        var c = await Connected();
        _p.Fail("uninstall");
        await c.DisconnectAsync();
        var before = _p.Calls.Count;

        await c.ConnectAsync();
        await c.IdleAsync();

        Assert.Equal(new[] { "uninstall", "delete-conf", "clear-recovery", "resolve" }, _p.Calls.Skip(before).Take(4));
        var status = await c.GetStatusAsync();
        Assert.Equal(TunnelState.Connected, status.State);
        Assert.Null(status.LastError);
    }

    [Fact]
    public async Task Connect_starts_nothing_when_the_retry_fails_again()
    {
        var c = await Connected();
        _p.Fail("uninstall", times: 2);
        await c.DisconnectAsync();
        var before = _p.Calls.Count;

        await c.ConnectAsync();
        await c.IdleAsync();

        var status = await c.GetStatusAsync();
        Assert.Equal(TunnelState.Error, status.State);
        Assert.Contains("uninstall failed", status.LastError);
        Assert.DoesNotContain("resolve", _p.Calls.Skip(before));
        Assert.NotNull(_p.Recovery);
        Assert.Single(_p.Handles);
    }

    [Fact]
    public async Task Service_start_retries_from_the_kept_state()
    {
        var c = await Connected();
        var pid = _p.Handles[0].ProcessId;
        _p.Fail("uninstall");
        _p.Fail("kill-wstunnel");
        _p.Fail(Del1);
        await c.DisconnectAsync();
        _p.ProcessNames[pid] = "wstunnel";
        var before = _p.Calls.Count;

        var restarted = NewController();
        await restarted.RecoverAsync();

        Assert.Equal(
            new[] { "load-recovery", "uninstall", "kill-pid " + pid, Del1, "delete-conf", "clear-recovery" },
            _p.Calls.Skip(before));
        Assert.Null(_p.Recovery);
        Assert.Null((await restarted.GetStatusAsync()).LastError);
    }

    [Fact]
    public async Task Leftover_host_route_is_removed_before_a_later_Connect_adds_it()
    {
        var c = await Connected();
        _p.Fail(Del1);
        await c.DisconnectAsync();
        Assert.Contains(Ip1, _p.HostRoutes);
        var before = _p.Calls.Count;

        await c.ConnectAsync();
        await c.IdleAsync();

        var calls = _p.Calls.Skip(before).ToList();
        Assert.True(calls.IndexOf(Del1) < calls.IndexOf(Add1));
        Assert.Equal(TunnelState.Connected, (await c.GetStatusAsync()).State);
    }

    [Fact]
    public async Task Deleting_an_absent_route_is_not_a_failure()
    {
        _p.Recovery = new RecoveryState(null, [Ip1]);
        var c = NewController();
        await c.RecoverAsync();
        Assert.Contains("has-route " + Ip1, _p.Calls);
        Assert.Null(_p.Recovery);
        Assert.Null((await c.GetStatusAsync()).LastError);
    }

    [Fact]
    public async Task Route_delete_that_cannot_be_verified_stays_recorded()
    {
        _p.Recovery = new RecoveryState(null, [Ip1]);
        _p.Fail("has-route " + Ip1);
        var c = NewController();
        await c.RecoverAsync();
        Assert.Equal(new[] { Ip1 }, _p.Recovery!.RouteIps);
        Assert.Contains("has-route", (await c.GetStatusAsync()).LastError);
    }

    [Fact]
    public async Task Failed_recovery_is_reported_and_retried_by_the_next_Connect()
    {
        _p.Recovery = new RecoveryState(null, []);
        _p.Fail("uninstall");
        var c = NewController();
        await c.RecoverAsync();
        Assert.Contains("uninstall failed", (await c.GetStatusAsync()).LastError);
        Assert.NotNull(_p.Recovery);

        await c.ConnectAsync();
        await c.IdleAsync();
        var status = await c.GetStatusAsync();
        Assert.Equal(TunnelState.Connected, status.State);
        Assert.Null(status.LastError);
    }

    [Fact]
    public async Task Failed_teardown_after_a_wstunnel_exit_reports_both()
    {
        var c = await Connected();
        _p.Fail("uninstall");
        _p.Handles[0].Exit(42);
        await c.IdleAsync();
        var status = await c.GetStatusAsync();
        Assert.Equal(TunnelState.Error, status.State);
        Assert.Contains("42", status.LastError);
        Assert.Contains("uninstall failed", status.LastError);
        Assert.NotNull(_p.Recovery);
    }

    // ---- route add interrupted in flight ----

    [Fact]
    public async Task Disconnect_during_a_route_add_removes_the_committed_route()
    {
        _p.Hold(Add1);
        var c = NewController();
        await c.ConnectAsync();
        await _p.Reached(Add1);
        var reply = await c.DisconnectAsync();
        Assert.Contains(Del1, _p.Calls);
        Assert.Empty(_p.HostRoutes);
        Assert.Null(reply.LastError);
        Assert.Null(_p.Recovery);
    }

    [Fact]
    public async Task Wstunnel_exit_during_a_route_add_removes_the_committed_route()
    {
        _p.Hold(Add1);
        var c = NewController();
        await c.ConnectAsync();
        await _p.Reached(Add1);
        _p.Handles[0].Exit(5);
        await c.IdleAsync();
        Assert.Empty(_p.HostRoutes);
        Assert.Null(_p.Recovery);
    }

    [Fact]
    public async Task Tool_limit_on_a_route_add_that_committed_removes_the_route()
    {
        _p.FailWithToolLimit(Add1);
        var c = NewController();
        await c.ConnectAsync();
        await c.IdleAsync();
        Assert.Equal(TunnelState.Error, (await c.GetStatusAsync()).State);
        Assert.Contains(Del1, _p.Calls);
        Assert.Empty(_p.HostRoutes);
        Assert.Null(_p.Recovery);
    }

    [Fact]
    public async Task Interrupted_route_add_that_never_committed_cleans_up_without_a_cleanup_error()
    {
        _p.RouteAddCommitsInFlight = false;
        _p.Hold(Add1);
        var c = NewController();
        await c.ConnectAsync();
        await _p.Reached(Add1);
        var reply = await c.DisconnectAsync();
        Assert.Contains(Del1, _p.Calls);
        Assert.Contains("has-route " + Ip1, _p.Calls);
        Assert.Null(reply.LastError);
        Assert.Null(_p.Recovery);
    }

    [Fact]
    public async Task Non_zero_route_add_is_known_not_added_and_is_not_deleted()
    {
        _p.Fail(Add1);
        var c = NewController();
        await c.ConnectAsync();
        await c.IdleAsync();
        Assert.DoesNotContain(Del1, _p.Calls);
        Assert.Null(_p.Recovery);
    }

    // ---- a workflow retired before it holds anything ----

    [Fact]
    public async Task Racing_Disconnect_never_leaks_what_a_workflow_starts()
    {
        var c = NewController();
        for (var i = 0; i < 300; i++)
        {
            var connect = Task.Run(c.ConnectAsync);
            var disconnect = Task.Run(c.DisconnectAsync);
            await Task.WhenAll(connect, disconnect);
        }
        await c.DisconnectAsync();
        await c.IdleAsync();
        Assert.All(_p.Handles, h => Assert.True(h.Killed));
        Assert.Empty(_p.HostRoutes);
        Assert.Null(_p.Recovery);
        Assert.Equal(TunnelState.Disconnected, (await c.GetStatusAsync()).State);
    }

    [Fact]
    public async Task Disconnect_tears_down_in_reverse_and_is_idempotent()
    {
        var c = await Connected();
        var before = _p.Calls.Count;
        await c.DisconnectAsync();
        await c.DisconnectAsync();
        Assert.Equal(
            new[] { "uninstall", "kill-wstunnel", Del1, Del2, "delete-conf", "clear-recovery" },
            _p.Calls.Skip(before));
        Assert.Equal(TunnelState.Disconnected, (await c.GetStatusAsync()).State);
    }

    [Fact]
    public async Task Disconnect_with_nothing_running_touches_nothing()
    {
        var c = NewController();
        var reply = await c.DisconnectAsync();
        Assert.Equal(TunnelState.Disconnected, reply.State);
        Assert.Empty(_p.Calls);
    }

    [Fact]
    public async Task Disconnect_from_Error_gives_Disconnected_without_a_second_teardown()
    {
        _p.Fail("install");
        var c = NewController();
        await c.ConnectAsync();
        await c.IdleAsync();
        var before = _p.Calls.Count;
        var reply = await c.DisconnectAsync();
        Assert.Equal(TunnelState.Disconnected, reply.State);
        Assert.Equal(before, _p.Calls.Count);
    }

    [Fact]
    public async Task Shutdown_tears_down()
    {
        var c = await Connected();
        await c.ShutdownAsync();
        Assert.Equal(TunnelState.Disconnected, (await c.GetStatusAsync()).State);
        Assert.Equal("clear-recovery", _p.Calls[^1]);
        Assert.True(_p.Handles[0].Killed);
    }

    // ---- wstunnel exit ----

    [Fact]
    public async Task Wstunnel_exit_mid_session_gives_Error_and_teardown()
    {
        var c = await Connected();
        var before = _p.Calls.Count;
        _p.Handles[0].Exit(42);
        await c.IdleAsync();
        var status = await c.GetStatusAsync();
        Assert.Equal(TunnelState.Error, status.State);
        Assert.Contains("42", status.LastError);
        Assert.Equal(
            new[] { "uninstall", "kill-wstunnel", Del1, Del2, "delete-conf", "clear-recovery" },
            _p.Calls.Skip(before));
    }

    [Fact]
    public async Task Wstunnel_exit_while_connecting_fails_with_its_output_and_cancels_the_workflow()
    {
        _p.Hold("wait-udp");
        var c = NewController();
        await c.ConnectAsync();
        await _p.Reached("wait-udp");
        _p.Handles[0].OutputTail = "bind failed";
        _p.Handles[0].Exit(3);
        await c.IdleAsync();
        var status = await c.GetStatusAsync();
        Assert.Equal(TunnelState.Error, status.State);
        Assert.Contains("3", status.LastError);
        Assert.Contains("bind failed", status.LastError);
        Assert.Equal(new[] { "kill-wstunnel", "clear-recovery" }, _p.CallsAfter("wait-udp"));
    }

    [Fact]
    public async Task Stale_exit_of_a_previous_process_does_not_fail_a_new_connection()
    {
        var c = await Connected();
        var old = _p.Handles[0];
        old.DeferExitOnKill = true;
        await c.DisconnectAsync();
        await c.ConnectAsync();
        await c.IdleAsync();
        var calls = _p.Calls.Count;

        old.Exit(1);
        await c.IdleAsync();

        var status = await c.GetStatusAsync();
        Assert.Equal(TunnelState.Connected, status.State);
        Assert.Null(status.LastError);
        Assert.Equal(calls, _p.Calls.Count);
        Assert.False(_p.Handles[1].Killed);
    }

    [Fact]
    public async Task Exit_during_intended_teardown_is_ignored()
    {
        var c = await Connected();
        var reply = await c.DisconnectAsync();
        await c.IdleAsync();
        Assert.Equal(TunnelState.Disconnected, reply.State);
        Assert.Null((await c.GetStatusAsync()).LastError);
    }

    [Fact]
    public async Task Exit_caused_by_failure_teardown_keeps_the_original_error()
    {
        _p.Fail("install"); // the fake's kill completes Exited during teardown
        var c = NewController();
        await c.ConnectAsync();
        await c.IdleAsync();
        Assert.Contains("install failed", (await c.GetStatusAsync()).LastError);
    }

    // ---- disconnect during connecting ----

    public static TheoryData<string> HoldableSteps() => new()
    {
        "resolve", "preflight", "save-recovery", "start-wstunnel", "wait-udp", "best-route " + Ip1,
        Add1, "write-conf", "uninstall", "install", "handshake",
    };

    [Theory]
    [MemberData(nameof(HoldableSteps))]
    public async Task Disconnect_during_Connecting_cancels_and_does_not_set_Error(string step)
    {
        _p.Hold(step);
        var c = NewController();
        await c.ConnectAsync();
        await _p.Reached(step);
        var reply = await c.DisconnectAsync();
        await c.IdleAsync();
        Assert.Equal(TunnelState.Disconnected, reply.State);
        Assert.Null(reply.LastError);
        Assert.Equal(TunnelState.Disconnected, (await c.GetStatusAsync()).State);
        Assert.All(_p.Handles, h => Assert.True(h.Killed));
    }

    [Fact]
    public async Task Handshake_arriving_after_Disconnect_does_not_resurrect_Connected()
    {
        _p.HoldStubbornly("handshake");
        var c = NewController();
        await c.ConnectAsync();
        await _p.Reached("handshake");
        var disconnect = c.DisconnectAsync();
        _p.ReleaseStubborn("handshake");
        var reply = await disconnect;
        await c.IdleAsync();
        Assert.Equal(TunnelState.Disconnected, reply.State);
        Assert.Equal(TunnelState.Disconnected, (await c.GetStatusAsync()).State);
        Assert.Equal("clear-recovery", _p.Calls[^1]);
    }

    [Fact]
    public async Task Late_handshake_of_a_cancelled_workflow_does_not_promote_a_newer_connection()
    {
        _p.HoldStubbornly("handshake");
        var c = NewController();
        await c.ConnectAsync();
        await _p.Reached("handshake");
        var disconnect = c.DisconnectAsync();
        _p.Hold("resolve");
        await c.ConnectAsync();
        _p.ReleaseStubborn("handshake");
        await WaitForCount("resolve", 2);
        Assert.Equal(TunnelState.Connecting, (await c.GetStatusAsync()).State);
        _p.Release("resolve");
        await disconnect;
        await c.IdleAsync();
        Assert.Equal(TunnelState.Connected, (await c.GetStatusAsync()).State);
    }

    [Fact]
    public async Task Cancelled_workflow_failing_late_does_not_fail_a_newer_connection()
    {
        _p.HoldStubbornly("install");
        _p.Fail("install");
        var c = NewController();
        await c.ConnectAsync();
        await _p.Reached("install");
        var disconnect = c.DisconnectAsync();
        await c.ConnectAsync();
        _p.ReleaseStubborn("install");
        await disconnect;
        await c.IdleAsync();
        var status = await c.GetStatusAsync();
        Assert.Equal(TunnelState.Connected, status.State);
        Assert.Null(status.LastError);
        Assert.Equal(2, _p.Handles.Count);
        Assert.True(_p.Handles[0].Killed);
        Assert.False(_p.Handles[1].Killed);
    }

    // ---- no overlap ----

    [Fact]
    public async Task Connect_right_after_Disconnect_waits_for_teardown()
    {
        var c = await Connected();
        var started = _p.Handles.Count;
        _p.Hold("delete-conf");
        var disconnect = c.DisconnectAsync();
        await _p.Reached("delete-conf");

        var reply = await c.ConnectAsync();
        Assert.Equal(TunnelState.Connecting, reply.State);
        await Task.Delay(20);
        Assert.Equal(started, _p.Handles.Count);
        Assert.Equal("delete-conf", _p.Calls[^1]);

        _p.Release("delete-conf");
        await disconnect;
        await c.IdleAsync();
        Assert.Equal(TunnelState.Connected, (await c.GetStatusAsync()).State);
        Assert.Equal(started + 1, _p.Handles.Count);
        Assert.Equal(1, _p.MaxLiveWstunnels);
    }

    [Fact]
    public async Task Rapid_connect_disconnect_cycles_never_overlap_and_end_clean()
    {
        var c = NewController();
        var work = new List<Task>();
        for (var i = 0; i < 25; i++)
        {
            work.Add(c.ConnectAsync());
            work.Add(c.DisconnectAsync());
        }
        await Task.WhenAll(work);
        await c.DisconnectAsync();
        await c.IdleAsync();
        Assert.Equal(TunnelState.Disconnected, (await c.GetStatusAsync()).State);
        Assert.True(_p.MaxLiveWstunnels <= 1, $"{_p.MaxLiveWstunnels} wstunnels were live at once");
        Assert.All(_p.Handles, h => Assert.True(h.Killed));
        Assert.Null(_p.Recovery);
    }

    [Fact]
    public async Task Connect_during_teardown_after_a_wstunnel_exit_waits_for_it()
    {
        var c = await Connected();
        _p.Hold("delete-conf");
        _p.Handles[0].Exit(9);
        await _p.Reached("delete-conf");
        await c.ConnectAsync();
        Assert.Single(_p.Handles);
        _p.Release("delete-conf");
        await c.IdleAsync();
        Assert.Equal(TunnelState.Connected, (await c.GetStatusAsync()).State);
        Assert.Equal(2, _p.Handles.Count);
        Assert.Equal(1, _p.MaxLiveWstunnels);
    }

    // ---- routes ----

    [Theory]
    [InlineData("192.0.2.1", 7, false)] // next hop equals the destination
    [InlineData("0.0.0.0", 7, false)]   // no next hop
    [InlineData("10.0.0.1", 7, true)]   // forward type DIRECT
    public async Task On_link_ip_is_not_routed_and_removal_uses_exactly_the_added_ips(string nextHop, int ifIndex, bool direct)
    {
        _p.Route = ip => ip == Ip1 ? new BestRoute(nextHop, ifIndex, direct) : new BestRoute("10.0.0.1", 7, false);
        var c = await Connected();
        Assert.DoesNotContain(Add1, _p.Calls);
        Assert.Contains(Add2, _p.Calls);
        await c.DisconnectAsync();
        Assert.DoesNotContain(Del1, _p.Calls);
        Assert.Contains(Del2, _p.Calls);
        Assert.Equal(new[] { Ip2 }, _p.SavedStates[^1].RouteIps);
    }

    [Fact]
    public async Task Route_uses_the_gateway_and_interface_of_the_best_route()
    {
        _p.Route = _ => new BestRoute("172.16.0.1", 12, false);
        await Connected();
        Assert.Contains($"route add {Ip1} {Mask} 172.16.0.1 if 12", _p.Calls);
    }

    // ---- crash recovery ----

    [Fact]
    public async Task Recovery_cleans_up_what_the_state_file_records()
    {
        _p.Recovery = new RecoveryState(555, [Ip1, Ip2]);
        _p.HostRoutes.UnionWith([Ip1, Ip2]);
        _p.ProcessNames[555] = "wstunnel";
        await NewController().RecoverAsync();
        Assert.Equal(new[]
        {
            "load-recovery", "uninstall", "kill-pid 555", Del1, Del2, "delete-conf", "clear-recovery",
        }, _p.Calls);
    }

    [Fact]
    public async Task Recovery_without_a_state_file_touches_nothing()
    {
        await NewController().RecoverAsync();
        Assert.Equal(new[] { "load-recovery", "tunnel-exists" }, _p.Calls);
    }

    [Fact]
    public async Task Recovery_without_a_state_file_removes_a_leftover_tunnel()
    {
        _p.TunnelExists = true;
        var c = NewController();
        await c.RecoverAsync();
        Assert.Equal(new[] { "load-recovery", "tunnel-exists", "uninstall", "delete-conf", "clear-recovery" }, _p.Calls);
        Assert.Null((await c.GetStatusAsync()).LastError);
    }

    [Fact]
    public async Task Failed_leftover_tunnel_removal_is_reported_and_stays_recoverable()
    {
        _p.TunnelExists = true;
        _p.Fail("uninstall");
        var c = NewController();
        await c.RecoverAsync();
        Assert.Contains("uninstall failed", (await c.GetStatusAsync()).LastError);
        Assert.NotNull(_p.Recovery);
    }

    [Theory]
    [InlineData("notepad")]
    [InlineData(null)]
    public async Task Recovery_does_not_kill_a_pid_that_is_not_wstunnel(string? processName)
    {
        _p.Recovery = new RecoveryState(555, []);
        if (processName != null) _p.ProcessNames[555] = processName;
        await NewController().RecoverAsync();
        Assert.DoesNotContain("kill-pid 555", _p.Calls);
        Assert.Contains("clear-recovery", _p.Calls);
    }

    [Fact]
    public async Task Recovery_with_no_recorded_pid_skips_the_kill()
    {
        _p.Recovery = new RecoveryState(null, []);
        await NewController().RecoverAsync();
        Assert.Equal(new[] { "load-recovery", "uninstall", "delete-conf", "clear-recovery" }, _p.Calls);
    }

    [Fact]
    public async Task Recovery_continues_after_a_step_throws()
    {
        _p.Recovery = new RecoveryState(555, [Ip1]);
        _p.HostRoutes.Add(Ip1);
        _p.ProcessNames[555] = "wstunnel";
        _p.Fail("uninstall");
        await NewController().RecoverAsync();
        Assert.Equal(new[] { "load-recovery", "uninstall", "kill-pid 555", Del1, "save-recovery" }, _p.Calls);
    }

    [Fact]
    public async Task Recovery_survives_a_process_exiting_during_the_name_lookup()
    {
        _p.Recovery = new RecoveryState(555, [Ip1]);
        _p.HostRoutes.Add(Ip1);
        _p.ProcessNameThrows = true;
        var c = NewController();
        await c.RecoverAsync();
        Assert.Contains(Del1, _p.Calls);
        Assert.Equal(555, _p.Recovery!.WstunnelPid);

        _p.ProcessNameThrows = false;
        await c.DisconnectAsync();
        Assert.Null(_p.Recovery);
        Assert.DoesNotContain("kill-pid 555", _p.Calls);
    }

    [Fact]
    public async Task Recovery_with_an_unreadable_state_file_still_cleans_what_it_can()
    {
        _p.RecoveryCorrupt = true;
        await NewController().RecoverAsync();
        Assert.Equal(new[] { "load-recovery", "uninstall", "delete-conf", "clear-recovery" }, _p.Calls);
    }

    [Fact]
    public async Task Connect_issued_during_recovery_waits_for_it()
    {
        _p.Recovery = new RecoveryState(null, []);
        _p.Hold("delete-conf");
        var c = NewController();
        var recover = c.RecoverAsync();
        await _p.Reached("delete-conf");
        await c.ConnectAsync();
        await Task.Delay(20);
        Assert.DoesNotContain("resolve", _p.Calls);
        _p.Release("delete-conf");
        await recover;
        await c.IdleAsync();
        Assert.Equal(TunnelState.Connected, (await c.GetStatusAsync()).State);
    }

    // ---- import and status ----

    [Fact]
    public async Task Import_valid_bundle_is_stored()
    {
        _p.BundleJson = null;
        var reply = await NewController().ImportBundleAsync(ValidBundleJson);
        Assert.True(reply.Ok);
        Assert.Null(reply.Error);
        Assert.Equal(ValidBundleJson, _p.BundleJson);
    }

    [Fact]
    public async Task Import_invalid_bundle_is_not_stored_and_does_not_echo_it()
    {
        var reply = await NewController().ImportBundleAsync("{\"version\":2,\"secret\":\"" + PrivateKey + "\"}");
        Assert.False(reply.Ok);
        Assert.DoesNotContain(PrivateKey, reply.Error);
        Assert.DoesNotContain("save-bundle", _p.Calls);
        Assert.Equal(ValidBundleJson, _p.BundleJson);
    }

    [Fact]
    public async Task Import_rejected_while_Connected()
    {
        var c = await Connected();
        var reply = await c.ImportBundleAsync(ValidBundleJson);
        Assert.False(reply.Ok);
        Assert.Equal("disconnect first", reply.Error);
        Assert.DoesNotContain("save-bundle", _p.Calls);
    }

    [Fact]
    public async Task Import_rejected_while_Connecting()
    {
        _p.Hold("resolve");
        var c = NewController();
        await c.ConnectAsync();
        var reply = await c.ImportBundleAsync(ValidBundleJson);
        Assert.False(reply.Ok);
        Assert.Equal("disconnect first", reply.Error);
        _p.Release("resolve");
        await c.IdleAsync();
    }

    [Fact]
    public async Task Import_allowed_in_Error()
    {
        _p.Fail("resolve");
        var c = NewController();
        await c.ConnectAsync();
        await c.IdleAsync();
        Assert.True((await c.ImportBundleAsync(ValidBundleJson)).Ok);
        Assert.Contains("save-bundle", _p.Calls);
    }

    [Fact]
    public async Task Import_store_failure_is_reported()
    {
        _p.Fail("save-bundle");
        var reply = await NewController().ImportBundleAsync(ValidBundleJson);
        Assert.False(reply.Ok);
        Assert.Contains("save-bundle failed", reply.Error);
    }

    [Fact]
    public async Task Status_ServerHost_comes_from_the_stored_bundle()
    {
        Assert.Equal("vpn.example", (await NewController().GetStatusAsync()).ServerHost);
        _p.BundleJson = null;
        Assert.Null((await NewController().GetStatusAsync()).ServerHost);
        _p.BundleJson = "not json";
        Assert.Null((await NewController().GetStatusAsync()).ServerHost);
    }

    [Fact]
    public async Task Status_polling_reads_the_bundle_file_once()
    {
        var c = NewController();
        for (var i = 0; i < 5; i++) await c.GetStatusAsync();
        Assert.Equal(1, _p.BundleLoads);
    }

    [Fact]
    public async Task Import_updates_the_cached_ServerHost()
    {
        _p.BundleJson = null;
        var c = NewController();
        Assert.Null((await c.GetStatusAsync()).ServerHost);
        await c.ImportBundleAsync(ValidBundleJson);
        var loads = _p.BundleLoads;
        Assert.Equal("vpn.example", (await c.GetStatusAsync()).ServerHost);
        Assert.Equal(loads, _p.BundleLoads);
    }

    [Fact]
    public async Task Rejected_import_leaves_the_cached_ServerHost()
    {
        var c = NewController();
        await c.GetStatusAsync();
        await c.ImportBundleAsync("{\"version\":2}");
        Assert.Equal("vpn.example", (await c.GetStatusAsync()).ServerHost);
    }
}
