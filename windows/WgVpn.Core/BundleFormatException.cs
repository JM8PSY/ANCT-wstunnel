// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

namespace WgVpn.Core;

/// <summary>
/// Thrown when a bundle is malformed or invalid. Messages name the offending
/// field but never include field values (the bundle contains a private key).
/// </summary>
public sealed class BundleFormatException : Exception
{
    public BundleFormatException(string message) : base(message) { }
}
