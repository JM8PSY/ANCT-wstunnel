// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

namespace WgVpn.Core;

/// <summary>
/// An expected connection failure whose message is written for the user and becomes
/// StatusReply.LastError. Messages must never include private keys or bundle contents.
/// </summary>
public sealed class TunnelException : Exception
{
    public TunnelException(string message, Exception? inner = null) : base(message, inner) { }
}
