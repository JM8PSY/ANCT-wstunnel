// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

using System.IO;
using System.Windows;
using System.Windows.Threading;
using WgVpn.Core;
using WgVpn.Core.Ipc;
using Forms = System.Windows.Forms;

namespace WgVpn.App;

public partial class App : System.Windows.Application
{
    private const long MaxBundleFileBytes = 256 * 1024; // well under PipeFraming.MaxFrameBytes (1 MiB)
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DisconnectTimeout = TimeSpan.FromMinutes(3); // teardown can be slow
    private static readonly TimeSpan ImportTimeout = TimeSpan.FromSeconds(15);

    private readonly PipeClient _client = new();
    private Mutex? _singleInstance;
    private MainWindow? _window;
    private Forms.NotifyIcon? _tray;
    private TrayIcons? _icons;
    private Forms.ToolStripMenuItem? _connectItem, _disconnectItem;
    private DispatcherTimer? _timer;
    private bool _polling;
    private bool _commandBusy;
    private StatusView _view = StatusPresenter.ServiceUnavailable();
    private (string Status, string? Error)? _lastLogged;

    public bool IsExiting { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Local\ namespace = per user session.
        _singleInstance = new Mutex(true, @"Local\ANCT.WgVpn.Tray", out var created);
        if (!created)
        {
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        _window = new MainWindow();
        _icons = new TrayIcons();
        BuildTray();
        // The logon autostart passes --tray: start in the tray only. A manual or post-install launch shows the window.
        if (!e.Args.Contains("--tray")) _window.Show();

        _timer = new DispatcherTimer { Interval = PollInterval };
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
        _ = PollAsync();
    }

    private void BuildTray()
    {
        var menu = new Forms.ContextMenuStrip();
        _connectItem = new Forms.ToolStripMenuItem("接続", null, (_, _) => _ = ConnectAsync());
        _disconnectItem = new Forms.ToolStripMenuItem("切断", null, (_, _) => _ = DisconnectAsync());
        menu.Items.Add(_connectItem);
        menu.Items.Add(_disconnectItem);
        menu.Items.Add(new Forms.ToolStripMenuItem("開く", null, (_, _) => ShowWindow()));
        menu.Items.Add(new Forms.ToolStripMenuItem("終了", null, (_, _) => ExitGui()));
        _tray = new Forms.NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = _icons!.Get(TrayState.ServiceUnavailable),
            Text = "ANCT WireGuard VPN",
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowWindow();
        ApplyView(_view);
    }

    private void ShowWindow()
    {
        if (_window == null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <summary>Exits the GUI only; the service and tunnel keep running.</summary>
    private void ExitGui()
    {
        IsExiting = true;
        _timer?.Stop();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
        _window?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _icons?.Dispose();
        if (_singleInstance != null)
        {
            _singleInstance.ReleaseMutex();
            _singleInstance.Dispose();
        }
        base.OnExit(e);
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "...";

    // NotifyIcon.Text is limited to 63 characters.
    private static string TrayText(string s) => "ANCT VPN: " + (s.Length > 40 ? s[..40] : s);

    private async Task PollAsync()
    {
        if (_polling || IsExiting) return; // never overlap requests
        _polling = true;
        try
        {
            var result = await _client.SendAsync(new GetStatusRequest(), StatusTimeout);
            if (IsExiting) return;
            Apply(result is PipeResult.Reply { Message: StatusReply r }
                ? StatusPresenter.FromReply(r)
                : StatusPresenter.ServiceUnavailable());
        }
        catch (Exception)
        {
            if (!IsExiting) Apply(StatusPresenter.ServiceUnavailable());
        }
        finally
        {
            _polling = false;
        }
    }

    private void Apply(StatusView v)
    {
        var key = (v.StatusText, v.ErrorText);
        if (_lastLogged != key)
        {
            _lastLogged = key;
            _window?.AppendLog(v.ErrorText == null ? v.StatusText : $"{v.StatusText}: {Truncate(v.ErrorText)}");
        }
        ApplyView(v);
    }

    private void ApplyView(StatusView v)
    {
        _view = v;
        _window?.ShowStatus(v);
        if (_tray == null) return;
        _tray.Icon = _icons!.Get(v.Tray);
        _tray.Text = TrayText(v.StatusText);
        if (_connectItem != null) _connectItem.Enabled = v.ConnectOffered && !_commandBusy;
        if (_disconnectItem != null) _disconnectItem.Enabled = v.DisconnectOffered;
    }

    public Task ToggleAsync() => _view.ToggleChecked ? DisconnectAsync() : ConnectAsync();

    public Task ConnectAsync() => CommandAsync(new ConnectRequest(), ConnectTimeout, "接続を要求しました");
    public Task DisconnectAsync() => CommandAsync(new DisconnectRequest(), DisconnectTimeout, "切断を要求しました");

    private async Task CommandAsync(IpcMessage request, TimeSpan timeout, string startedText)
    {
        // Connect replies at once; Disconnect replies only after teardown, hence the long timeout.
        // Polling continues meanwhile on its own connections; only one command runs at a time.
        if (_commandBusy) return;
        _commandBusy = true;
        ApplyView(_view);
        _window?.AppendLog(startedText);
        try
        {
            var result = await _client.SendAsync(request, timeout);
            if (result is PipeResult.ServiceUnavailable u)
                _window?.AppendLog("サービスに接続できません (" + u.Reason + ")");
            else if (result is PipeResult.Reply { Message: StatusReply r })
                Apply(StatusPresenter.FromReply(r));
            else if (result is PipeResult.Rejected j)
                _window?.AppendLog("要求は拒否されました: " + j.Reason);
            else
                _window?.AppendLog("予期しない応答を受信しました");
        }
        catch (Exception e)
        {
            _window?.AppendLog("エラー: " + e.GetType().Name);
        }
        finally
        {
            _commandBusy = false;
            ApplyView(_view);
        }
        _ = PollAsync();
    }

    public async Task ImportAsync(Window owner)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "設定ファイルを選択",
            Filter = "設定ファイル (*.json)|*.json",
        };
        if (dlg.ShowDialog(owner) != true) return;

        string text;
        try
        {
            if (new FileInfo(dlg.FileName).Length > MaxBundleFileBytes)
            {
                _window?.AppendLog("読み込み失敗: ファイルが大きすぎます (上限 256 KiB)");
                return;
            }
            text = await File.ReadAllTextAsync(dlg.FileName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or System.Security.SecurityException or NotSupportedException
                                      or ArgumentException)
        {
            _window?.AppendLog("読み込み失敗: ファイルを開けません");
            return;
        }

        var result = await _client.SendAsync(new ImportBundleRequest(text), ImportTimeout);
        text = string.Empty; // do not hold the bundle longer than the request
        switch (result)
        {
            case PipeResult.Reply { Message: ImportBundleReply { Ok: true } }:
                _window?.AppendLog("設定ファイルを読み込みました");
                break;
            case PipeResult.Reply { Message: ImportBundleReply r }:
                _window?.AppendLog("読み込み失敗: " + Truncate(r.Error ?? "不明なエラー"));
                break;
            case PipeResult.Rejected j:
                _window?.AppendLog("読み込み失敗: " + j.Reason);
                break;
            default:
                _window?.AppendLog("読み込み失敗: サービスに接続できません");
                break;
        }
    }
}
