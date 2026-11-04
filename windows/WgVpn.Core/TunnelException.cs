// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

namespace WgVpn.Core;

/// <summary>
/// An expected connection failure whose message is written for the user and becomes
/// StatusReply.LastError. Messages must never include private keys or bundle contents.
/// </summary>
public class TunnelException : Exception
{
    public TunnelException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// A tool ran to completion and reported failure through a non-zero exit, so it did not do its
/// work. Other failures (a timeout, a kill, a cancellation) leave the outcome unknown.
/// </summary>
public sealed class ProcessFailedException : TunnelException
{
    public ProcessFailedException(string message) : base(message) { }
}
