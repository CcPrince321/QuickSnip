using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace QuickSnip.Services;

/// <summary>
/// 截图落盘。PNG 编码，文件名带时间戳，避免同名覆盖。
/// </summary>
public static class SaveService
{
    /// <summary>默认目录：图片\QuickSnip。</summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "QuickSnip");

    public static string ResolveDirectory(AppSettings settings)
    {
        string dir = string.IsNullOrWhiteSpace(settings.SaveDirectory)
            ? DefaultDirectory
            : settings.SaveDirectory;
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>保存 PNG，返回完整路径。同一秒内多次截图会追加序号，不覆盖旧文件。</summary>
    public static string SavePng(BitmapSource image, string dir)
    {
        Directory.CreateDirectory(dir);

        string stem = $"QuickSnip_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
        string path = Path.Combine(dir, stem + ".png");
        for (int i = 2; File.Exists(path); i++)
        {
            path = Path.Combine(dir, $"{stem}_{i}.png");
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        encoder.Save(stream);
        return path;
    }
}
