using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using HotspotKeeper.Services;
using Microsoft.Win32;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;

namespace HotspotKeeper;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _daemonRunning;
    private bool _busy;
    private bool _suppressComboEvent = true;
    private TrayIcon? _tray;
    private bool _reallyExit;
    private bool _trayHintShown;

    public MainWindow()
    {
        InitializeComponent();
        TxtClashPath.Text = App.Config.ClashPath;
        CmbTheme.SelectedIndex = App.Config.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        _suppressComboEvent = false;
        ChkAutoDaemon.IsChecked = App.Config.AutoRunDaemon;

        _timer.Tick += async (_, _) => await DaemonTickAsync();

        _tray = new TrayIcon();
        _tray.OpenRequested += ShowFromTray;
        _tray.ExitRequested += () =>
        {
            _reallyExit = true;
            Application.Current.Shutdown();
        };

        Loaded += MainWindow_Loaded;
    }

    // 关闭窗口 = 隐藏到托盘，守护继续运行；只有托盘右键「退出」才真正关闭
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_reallyExit)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray?.ShowBalloon("HotspotKeeper 仍在运行",
                    "已最小化到系统托盘，热点守护继续运行。\n双击托盘图标恢复窗口，右键托盘图标可退出。");
            }
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _tray?.Dispose();
        _tray = null;
        base.OnClosed(e);
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // 同步开机自启勾选状态（不触发写回）
        try
        {
            ChkAutoStart.IsChecked = await AutoStart.IsEnabledAsync();
        }
        catch { }

        // 需求：打开软件后自动打开 WiFi 与个人热点并开始守护
        if (App.Config.AutoRunDaemon)
            await StartDaemonAsync();
        else
            await RefreshStateAsync(checkOnlyOnce: false);
    }

    // ---------- 守护逻辑 ----------

    private async Task StartDaemonAsync()
    {
        if (_daemonRunning) return;
        _daemonRunning = true;
        UpdateDaemonUi();
        SetStatus("守护已启动：正在打开 WiFi 与个人热点…");
        var wifi = await Ps.RunAsync("wifi-on");
        if (!wifi.Ok) SetStatus($"打开 WiFi 失败：{wifi.Error}");
        var hs = await Ps.RunAsync("start");
        SetStatus(hs.Ok ? "守护运行中，每 60 秒检测一次。" : $"开启热点失败：{hs.Error}");
        _timer.Start();
    }

    private void StopDaemon()
    {
        if (!_daemonRunning) return;
        _daemonRunning = false;
        _timer.Stop();
        UpdateDaemonUi();
        SetStatus("守护已停止。");
    }

    private async Task DaemonTickAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var r = await Ps.RunAsync("state");
            TxtLastCheck.Text = DateTime.Now.ToString("HH:mm:ss");

            if (!r.Ok)
            {
                SetStatus($"检测失败：{r.Error}");
                return;
            }

            var state = r.Get("state") ?? "unknown";
            var clientsRaw = r.Get("clients") ?? "-1";
            int.TryParse(clientsRaw, out var clients);

            TxtState.Text = state switch { "enabled" => "已开启", "disabled" => "已关闭", _ => "未知" };
            TxtClients.Text = clients >= 0 ? clients.ToString() : "?";

            if (state != "enabled")
            {
                SetStatus("检测到热点未开启，正在开启…");
                var start = await Ps.RunAsync("start");
                if (!start.Ok) SetStatus($"开启热点失败：{start.Error}");
            }
            else if (clients == 0)
            {
                SetStatus("检测到连接设备数为 0，正在重启热点…");
                await Ps.RunAsync("stop");
                await Task.Delay(3000);
                var start = await Ps.RunAsync("start");
                if (!start.Ok) SetStatus($"重启热点失败：{start.Error}");
            }
            else if (clients > 0)
            {
                SetStatus($"检查完成：{clients} 台设备已连接。");
            }
            else
            {
                SetStatus("热点已开启，但设备数未知（跳过重启）。");
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task RefreshStateAsync(bool checkOnlyOnce)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var r = await Ps.RunAsync("state");
            if (r.Ok)
            {
                var state = r.Get("state") ?? "unknown";
                var clientsRaw = r.Get("clients") ?? "-1";
                int.TryParse(clientsRaw, out var clients);
                TxtState.Text = state switch { "enabled" => "已开启", "disabled" => "已关闭", _ => "未知" };
                TxtClients.Text = clients >= 0 ? clients.ToString() : "?";
                TxtLastCheck.Text = DateTime.Now.ToString("HH:mm:ss");
                SetStatus("状态已刷新。");
            }
            else
            {
                SetStatus($"刷新失败：{r.Error}");
            }
        }
        finally
        {
            _busy = false;
        }
    }

    // ---------- 一键切换 ----------

    private async Task SwitchToWiredClashAsync()
    {
        StopDaemon();
        SetStatus("正在关闭有线网卡…");
        var eth = await Ps.RunAsync("eth-off");
        if (!eth.Ok) SetStatus($"关闭有线失败：{eth.Error}");

        SetStatus("等待 5 秒后打开 WiFi…");
        await Task.Delay(5000);

        SetStatus("正在打开 WiFi…");
        var wifi = await Ps.RunAsync("wifi-on");
        if (!wifi.Ok) SetStatus($"打开 WiFi 失败：{wifi.Error}");

        // 启动 Clash 前：3 秒后才可确认的提醒弹窗
        if (!ShowDelayedConfirmDialog("切换到 WiFi + Clash",
                "请先启动 Clash",
                "确认后将选择启动方式：已启动 / 自动启动。"))
        {
            SetStatus("已取消：未启动 Clash。");
            return;
        }

        // 三选一：取消 / 已启动（用户已手动打开 Clash）/ 自动启动
        var choice = ShowChoiceDialog("启动 Clash",
            "请先启动 Clash。\n\n可选择自行手动启动，或由本程序自动启动。",
            "取消", "已启动", "自动启动");
        if (choice < 0)
        {
            SetStatus("已取消：未启动 Clash。");
            return;
        }

        if (choice == 1)
        {
            SetStatus("已切换：有线关闭、WiFi 打开，Clash 由你手动启动。");
            return;
        }

        SetStatus("正在启动 Clash for Windows…");
        var clash = await Ps.RunAsync("clash-start",
            string.IsNullOrWhiteSpace(TxtClashPath.Text) ? null : TxtClashPath.Text.Trim());
        if (!clash.Ok)
        {
            SetStatus($"启动 Clash 失败：{clash.Error}");
            return;
        }

        // 自动启动 Clash 后，提醒手动开启代理开关
        var proxy = ShowChoiceDialog("代理开关",
            "请在 Clash 中开启系统代理开关。\n\n开启后点击「已开启」完成切换。",
            "取消", "已开启");

        SetStatus(proxy == 1
            ? "已切换：有线关闭、WiFi 打开、Clash 代理已开启。"
            : "已切换，但你未确认代理已开启，请手动检查 Clash 代理开关。");
    }

    private async Task RestoreWiredAsync()
    {
        SetStatus("正在关闭 Clash for Windows…");
        await Ps.RunAsync("clash-stop");

        SetStatus("正在启用有线网卡…");
        var eth = await Ps.RunAsync("eth-on");
        SetStatus(eth.Ok ? "已恢复：Clash 已关闭，有线网已启用。" : $"启用有线失败：{eth.Error}");

        await StartDaemonAsync();
    }

    // ---------- UI 事件 ----------

    private async void BtnStartDaemon_Click(object sender, RoutedEventArgs e)
    {
        await StartDaemonAsync();
    }

    private void BtnStopDaemon_Click(object sender, RoutedEventArgs e)
    {
        StopDaemon();
    }

    private async void BtnCheckNow_Click(object sender, RoutedEventArgs e)
    {
        await DaemonTickAsync();
    }

    private async void BtnToWiredClash_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(this,
            "将执行：关闭有线网卡 → 等待 5 秒 → 打开 WiFi → 启动 Clash for Windows。\n\n" +
            "此操作会短暂断网，且未经测试。确定继续吗？",
            "切换到 WiFi + Clash",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        await SwitchToWiredClashAsync();
    }

    private async void BtnRestore_Click(object sender, RoutedEventArgs e)
    {
        if (!ShowDelayedConfirmDialog("恢复有线网络",
                "请先手动关闭 clash 的代理!!!",
                "将执行：关闭 Clash for Windows → 启用有线网卡 → 自动开启热点守护。")) return;
        await RestoreWiredAsync();
    }

    // 3 秒后才允许点击「确认」的提醒弹窗，返回 false 表示取消
    private bool ShowDelayedConfirmDialog(string title, string warnText, string descText)
    {
        bool result = false;
        var dlg = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.SingleBorderWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = (Brush)FindResource("WindowBg"),
        };

        var warn = new TextBlock
        {
            Text = warnText,
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("DangerFg"),
            Margin = new Thickness(0, 0, 0, 10),
        };
        var desc = new TextBlock
        {
            Text = descText,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextSecondary"),
            Margin = new Thickness(0, 0, 0, 18),
        };

        var okBtn = new Button { Content = "确认（3 秒）", MinWidth = 100, Height = 30, IsEnabled = false };
        var cancelBtn = new Button { Content = "取消", MinWidth = 100, Height = 30, IsCancel = true };
        okBtn.Click += (_, _) => { result = true; dlg.Close(); };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };
        buttons.Children.Add(cancelBtn);
        var okMargin = okBtn.Margin;
        okMargin.Left = 10;
        okBtn.Margin = okMargin;
        buttons.Children.Add(okBtn);

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(warn);
        panel.Children.Add(desc);
        panel.Children.Add(buttons);
        dlg.Content = panel;

        var remaining = 3;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            remaining--;
            if (remaining > 0)
            {
                okBtn.Content = $"确认（{remaining} 秒）";
                return;
            }
            timer.Stop();
            okBtn.Content = "确认继续";
            okBtn.IsEnabled = true;
        };
        dlg.Closed += (_, _) => timer.Stop();
        timer.Start();

        dlg.ShowDialog();
        return result;
    }

    // 自定义多按钮询问弹窗，返回按钮下标；直接关闭窗口返回 -1
    private int ShowChoiceDialog(string title, string message, params string[] buttons)
    {
        int choice = -1;
        var dlg = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.SingleBorderWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = (Brush)FindResource("WindowBg"),
        };

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextPrimary"),
            Margin = new Thickness(0, 0, 0, 18),
        });

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };
        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i;
            var btn = new Button
            {
                Content = buttons[i],
                MinWidth = 100,
                Height = 30,
                Margin = new Thickness(i == 0 ? 0 : 10, 0, 0, 0),
                IsCancel = i == 0,
            };
            btn.Click += (_, _) => { choice = index; dlg.Close(); };
            row.Children.Add(btn);
        }
        panel.Children.Add(row);
        dlg.Content = panel;

        dlg.ShowDialog();
        return choice;
    }

    private async void ChkAutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var enable = ChkAutoStart.IsChecked == true;
        var (ok, error) = await AutoStart.SetEnabledAsync(enable);
        if (!ok)
        {
            SetStatus($"设置开机自启失败：{error}");
            ChkAutoStart.IsChecked = !enable;
        }
        else
        {
            SetStatus(enable ? "已开启开机自启。" : "已关闭开机自启。");
        }
    }

    private void ChkAutoDaemon_Changed(object sender, RoutedEventArgs e)
    {
        App.Config.AutoRunDaemon = ChkAutoDaemon.IsChecked == true;
        App.Config.Save();
    }

    private void CmbTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvent) return;
        var mode = (CmbTheme.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        App.Config.Theme = mode;
        App.Config.Save();
        ThemeManager.Apply(mode);
    }

    private void TxtClashPath_LostFocus(object sender, RoutedEventArgs e)
    {
        App.Config.ClashPath = TxtClashPath.Text.Trim();
        App.Config.Save();
    }

    private void BtnBrowseClash_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择 Clash for Windows.exe",
            Filter = "可执行文件|*.exe|所有文件|*.*",
        };
        if (dlg.ShowDialog(this) == true)
        {
            TxtClashPath.Text = dlg.FileName;
            App.Config.ClashPath = dlg.FileName;
            App.Config.Save();
        }
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch { }
        e.Handled = true;
    }

    // ---------- 小工具 ----------

    private void SetStatus(string text) => TxtStatus.Text = text;

    private void UpdateDaemonUi()
    {
        BtnStartDaemon.IsEnabled = !_daemonRunning;
        BtnStopDaemon.IsEnabled = _daemonRunning;
        TxtDaemon.Text = _daemonRunning ? "运行中" : "未运行";
        TxtDaemon.Foreground = (Brush)FindResource(_daemonRunning ? "OkFg" : "TextSecondary");
    }
}
