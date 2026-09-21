using System.Windows;
using HotspotKeeper.Services;
using Microsoft.Win32;
using Application = System.Windows.Application;

namespace HotspotKeeper;

public partial class App : Application
{
    public static Settings Config { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        Config = Settings.Load();
        ThemeManager.Apply(Config.Theme);

        // Follow the system theme live when in "auto" mode.
        SystemEvents.UserPreferenceChanged += (_, _) =>
        {
            if (ThemeManager.Mode == "auto")
            {
                try { ThemeManager.Apply("auto"); } catch { }
            }
        };

        base.OnStartup(e);
    }
}
