// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

using System.Security.Cryptography;

namespace WgVpn.Core;

public static class TlsPin
{
    /// <summary>True if SHA-256 of the DER cert equals the expected lowercase-hex (no colons) digest; case-insensitive.</summary>
    public static bool Matches(byte[] certDer, string expectedSha256Hex)
    {
        if (certDer is null || expectedSha256Hex is null || expectedSha256Hex.Length != 64) return false;
        var actual = Convert.ToHexString(SHA256.HashData(certDer));
        return string.Equals(actual, expectedSha256Hex, StringComparison.OrdinalIgnoreCase);
    }
}
