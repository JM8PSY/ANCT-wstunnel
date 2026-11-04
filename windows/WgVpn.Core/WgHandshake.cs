// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

using System.Globalization;

namespace WgVpn.Core;

public static class WgHandshake
{
    /// <summary>
    /// Parses <c>wg show wgvpn latest-handshakes</c> output (<c>pubkey\tunix-seconds</c> per line);
    /// true when any peer has a timestamp above zero. Anything unparseable counts as no handshake.
    /// </summary>
    public static bool HasHandshake(string wgShowOutput)
    {
        foreach (var line in wgShowOutput.Split('\n'))
        {
            var fields = line.TrimEnd('\r').Split('\t');
            if (fields.Length == 2
                && long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                && seconds > 0)
            {
                return true;
            }
        }
        return false;
    }
}
