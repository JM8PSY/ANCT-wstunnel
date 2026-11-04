// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

namespace WgVpn.Core;

/// <summary>An external command as argv (never a joined string).</summary>
public sealed record ProcessSpec(string FileName, string[] Args);
