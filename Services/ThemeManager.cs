using System.Windows;
using Microsoft.Win32;

namespace HotspotKeeper.Services;

public static class ThemeManager
{
    public static string Mode { get; private set; } = "auto";

    public static void Apply(string mode)
    {
        Mode = mode;
        var dark = mode switch
        {
            "dark" => true,
            "light" => false,
            _ => IsSystemDark()
        };
        var app = Application.Current;
        if (app == null) return;
        var dicts = app.Resources.MergedDictionaries;
        dicts.Clear();
        dicts.Add(new ResourceDictionary
        {
            Source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative)
        });
    }

    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }
}
