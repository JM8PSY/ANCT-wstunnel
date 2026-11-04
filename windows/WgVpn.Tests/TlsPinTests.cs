// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

using WgVpn.Core;

namespace WgVpn.Tests;

public class TlsPinTests
{
    private static readonly byte[] Abc = "abc"u8.ToArray();
    private const string AbcHex = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    [Fact] public void Matches_known_vector() => Assert.True(TlsPin.Matches(Abc, AbcHex));
    [Fact] public void Matches_is_case_insensitive() => Assert.True(TlsPin.Matches(Abc, AbcHex.ToUpperInvariant()));
    [Fact] public void Rejects_wrong_hash() => Assert.False(TlsPin.Matches(Abc, new string('0', 64)));
    [Fact] public void Rejects_wrong_length() => Assert.False(TlsPin.Matches(Abc, AbcHex[..62]));
    [Fact] public void Rejects_empty() => Assert.False(TlsPin.Matches(Abc, ""));
    [Fact] public void Rejects_colon_format() =>
        Assert.False(TlsPin.Matches(Abc, string.Join(":", Enumerable.Range(0, 32).Select(i => AbcHex.Substring(i * 2, 2)))));
}
