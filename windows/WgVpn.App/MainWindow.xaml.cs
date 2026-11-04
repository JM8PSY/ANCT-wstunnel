// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

using System.ComponentModel;
using System.Windows;
using WgVpn.Core;

namespace WgVpn.App;

/// <summary>Thin binding layer: shows a <see cref="StatusView"/> and forwards user actions to <see cref="App"/>.</summary>
public partial class MainWindow : Window
{
    private const int MaxLogLines = 500;
    private readonly LinkedList<string> _log = new();

    public MainWindow()
    {
        InitializeComponent();
        // Logoff/shutdown must not be blocked by hide-to-tray.
        Microsoft.Win32.SystemEvents.SessionEnding += (_, _) => Host.IsExiting = true;
        ShowStatus(StatusPresenter.ServiceUnavailable());
    }

    private static App Host => (App)System.Windows.Application.Current;

    public void ShowStatus(StatusView v)
    {
        StatusText.Text = v.StatusText;
        ServerText.Text = v.ServerText;
        PowerToggle.IsEnabled = v.ToggleEnabled;
        PowerToggle.IsChecked = v.ToggleChecked;
        DisconnectButton.Visibility = v.DisconnectOffered && !v.ToggleChecked ? Visibility.Visible : Visibility.Collapsed;
    }

    public void AppendLog(string line)
    {
        _log.AddLast($"{DateTime.Now:HH:mm:ss} {line}");
        while (_log.Count > MaxLogLines) _log.RemoveFirst();
        LogBox.Text = string.Join(Environment.NewLine, _log);
        LogBox.ScrollToEnd();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!Host.IsExiting)
        {
            e.Cancel = true; // closing hides to tray
            Hide();
        }
        base.OnClosing(e);
    }

    private void PowerToggle_Click(object sender, RoutedEventArgs e) => _ = Host.ToggleAsync();
    private void DisconnectButton_Click(object sender, RoutedEventArgs e) => _ = Host.DisconnectAsync();
    private void ImportButton_Click(object sender, RoutedEventArgs e) => _ = Host.ImportAsync(this);
}
