// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

using System.Drawing;
using System.Runtime.InteropServices;
using WgVpn.Core;

namespace WgVpn.App;

/// <summary>Per-state tray icons drawn in code; owns and disposes the icons.</summary>
public sealed class TrayIcons : IDisposable
{
    private readonly Dictionary<TrayState, Icon> _icons = new();

    public TrayIcons()
    {
        foreach (var state in Enum.GetValues<TrayState>())
            _icons[state] = Draw(ColorOf(state));
    }

    public Icon Get(TrayState state) => _icons[state];

    private static Color ColorOf(TrayState s) => s switch
    {
        TrayState.Idle => Color.Gray,
        TrayState.Connecting => Color.Orange,
        TrayState.Connected => Color.LimeGreen,
        TrayState.Error => Color.Red,
        _ => Color.FromArgb(90, 20, 20),
    };

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private static Icon Draw(Color color)
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        using (var fill = new SolidBrush(color))
        using (var pen = new Pen(Color.FromArgb(60, 60, 60)))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.FillEllipse(fill, 1, 1, 13, 13);
            g.DrawEllipse(pen, 1, 1, 13, 13);
        }
        var handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone(); // own copy, so the raw handle can be destroyed
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        foreach (var i in _icons.Values) i.Dispose();
        _icons.Clear();
    }
}
