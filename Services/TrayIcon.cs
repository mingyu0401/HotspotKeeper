using System.Drawing;
using System.Reflection;
using WF = System.Windows.Forms;

namespace HotspotKeeper.Services;

/// <summary>
/// Notification-area icon: closing the window only hides the app;
/// real exit happens through the tray context menu.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WF.NotifyIcon _icon;

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        Icon icon;
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var resName = Array.Find(asm.GetManifestResourceNames(),
                n => n.EndsWith("App.ico", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("App.ico not embedded.");
            using var stream = asm.GetManifestResourceStream(resName)!;
            icon = new Icon(stream);
        }
        catch
        {
            icon = SystemIcons.Application;
        }

        var menu = new WF.ContextMenuStrip();
        menu.Items.Add("打开主窗口", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("退出 HotspotKeeper", null, (_, _) => ExitRequested?.Invoke());

        _icon = new WF.NotifyIcon
        {
            Icon = icon,
            Text = "HotspotKeeper · 热点守护",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    public void ShowBalloon(string title, string text) =>
        _icon.ShowBalloonTip(2500, title, text, WF.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
