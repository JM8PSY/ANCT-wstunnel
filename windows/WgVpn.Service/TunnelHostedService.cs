// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

using WgVpn.Core;

namespace WgVpn.Service;

/// <summary>Service lifetime: prepare storage, recover from a crash, serve the pipe; tear down on stop.</summary>
public sealed class TunnelHostedService : BackgroundService
{
    private readonly WindowsTunnelPlatform _platform;
    private readonly TunnelController _controller;
    private readonly PipeServer _pipe;

    public TunnelHostedService(WindowsTunnelPlatform platform, TunnelController controller, PipeServer pipe)
    {
        _platform = platform;
        _controller = controller;
        _pipe = pipe;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // Let the host finish starting; the service manager is waiting on it.
        _platform.PrepareDataDirectory();
        await _controller.RecoverAsync().ConfigureAwait(false);
        await _pipe.RunAsync(stoppingToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false); // stops the pipe, so no new requests
        await _controller.ShutdownAsync().ConfigureAwait(false);
    }
}
