using System;
using System.IO;
using System.Text.Json;

namespace QuickSnip.Services;

public sealed class AppSettings
{
    /// <summary>形如 "Ctrl+Alt+A"。</summary>
    public string HotKey { get; set; } = "Ctrl+Alt+A";

    /// <summary>双击 = 全屏。关掉之后单击取消立即生效（没有 500ms 等待）。</summary>
    public bool DoubleClickFullScreen { get; set; } = true;

    /// <summary>全屏选区的范围：false = 鼠标所在显示器，true = 整个虚拟桌面。</summary>
    public bool FullScreenUseWholeVirtualDesktop { get; set; }

    /// <summary>截完是否弹托盘气泡。</summary>
    public bool ShowBalloonAfterCapture { get; set; } = true;

    /// <summary>启动时弹一次气泡，用来确认「程序真的起来了」。</summary>
    public bool ShowBalloonOnStartup { get; set; } = true;

    /// <summary>截完自动保存 PNG。</summary>
    public bool AutoSavePng { get; set; } = true;

    /// <summary>PNG 保存目录；空 = 图片\QuickSnip。</summary>
    public string SaveDirectory { get; set; } = "";

    public HotKeySpec GetHotKeySpec() => HotKeySpec.Parse(HotKey);
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickSnip");

    public static string FilePath => Path.Combine(Directory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            string json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        }
        catch
        {
            // 配置坏了就用默认值，不要让软件起不来。
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
            // 存不下就算了，本次会话仍然按内存里的值跑。
        }
    }
}
