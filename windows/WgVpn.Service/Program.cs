// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

using WgVpn.Core;
using WgVpn.Service;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "WgVpnService");

// Teardown runs external tools, each bounded at 60 s; the default 30 s stop window would cut it short.
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromMinutes(3));

builder.Services.AddSingleton<WindowsTunnelPlatform>();
builder.Services.AddSingleton(provider =>
{
    var logger = provider.GetRequiredService<ILogger<TunnelController>>();
    return new TunnelController(
        provider.GetRequiredService<WindowsTunnelPlatform>(),
        TunnelTimeouts.Default,
        message => logger.LogWarning("{Message}", message));
});
builder.Services.AddSingleton<PipeServer>();
builder.Services.AddHostedService<TunnelHostedService>();

var host = builder.Build();
host.Run();
