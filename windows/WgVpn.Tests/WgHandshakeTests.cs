// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

using WgVpn.Core;

namespace WgVpn.Tests;

public class WgHandshakeTests
{
    [Theory]
    [InlineData("pub=\t1762230000\n", true)]
    [InlineData("pub=\t1762230000\r\n", true)]
    [InlineData("a=\t0\nb=\t1762230000\n", true)]
    [InlineData("pub=\t0\n", false)]
    [InlineData("a=\t0\nb=\t0\n", false)]
    [InlineData("", false)]
    [InlineData("\n\n", false)]
    [InlineData("pub=\n", false)]
    [InlineData("pub=\tnot-a-number\n", false)]
    [InlineData("pub=\t-5\n", false)]
    [InlineData("Unable to access interface: not found\n", false)]
    public void Parses_latest_handshakes(string output, bool expected)
    {
        Assert.Equal(expected, WgHandshake.HasHandshake(output));
    }
}
