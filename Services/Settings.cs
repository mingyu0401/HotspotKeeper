using System.IO;
using System.Text.Json;

namespace HotspotKeeper.Services;

public class Settings
{
    /// <summary>auto | dark | light</summary>
    public string Theme { get; set; } = "auto";
    public string ClashPath { get; set; } = "";
    /// <summary>打开软件后自动启动守护（开 WiFi + 热点 + 每分钟检测）</summary>
    public bool AutoRunDaemon { get; set; } = true;

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HotspotKeeper");
    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
