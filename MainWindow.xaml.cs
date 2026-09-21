using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using HotspotKeeper.Services;
using Microsoft.Win32;

namespace HotspotKeeper;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _daemonRunning;
    private bool _busy;
    private bool _suppressComboEvent = true;

    public MainWindow()
    {
        InitializeComponent();
        TxtClashPath.Text = App.Config.ClashPath;
        CmbTheme.SelectedIndex = App.Config.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        _suppressComboEvent = false;
        ChkAutoDaemon.IsChecked = App.Config.AutoRunDaemon;

        _timer.Tick += async (_, _) => await DaemonTickAsync();

        Loaded += MainWindow_Loaded;
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

        SetStatus("正在打开 WiFi…");
        var wifi = await Ps.RunAsync("wifi-on");
        if (!wifi.Ok) SetStatus($"打开 WiFi 失败：{wifi.Error}");

        SetStatus("正在启动 Clash for Windows…");
        var clash = await Ps.RunAsync("clash-start",
            string.IsNullOrWhiteSpace(TxtClashPath.Text) ? null : TxtClashPath.Text.Trim());
        SetStatus(clash.Ok
            ? "已切换：有线关闭、WiFi 打开、Clash 已启动。"
            : $"切换完成但有错误：{clash.Error}");
    }

    private async Task RestoreWiredAsync()
    {
        SetStatus("正在关闭 Clash for Windows…");
        await Ps.RunAsync("clash-stop");

        SetStatus("正在启用有线网卡…");
        var eth = await Ps.RunAsync("eth-on");
        SetStatus(eth.Ok ? "已恢复：Clash 已关闭，有线网已启用。" : $"启用有线失败：{eth.Error}");
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
            "将执行：关闭有线网卡 → 打开 WiFi → 启动 Clash for Windows。\n\n" +
            "此操作会短暂断网，且未经测试。确定继续吗？",
            "切换到 WiFi + Clash",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        await SwitchToWiredClashAsync();
    }

    private async void BtnRestore_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(this,
            "将执行：关闭 Clash for Windows → 启用有线网卡。\n\n确定继续吗？",
            "恢复有线网络",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        await RestoreWiredAsync();
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
