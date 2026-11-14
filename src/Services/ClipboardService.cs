using System;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;

namespace QuickSnip.Services;

public static class ClipboardService
{
    /// <summary>
    /// 写剪贴板。剪贴板被其他程序临时占用时 SetImage 会抛 COMException，
    /// 按需求重试 3 次、间隔 50ms；仍失败就把原因返回给调用方做可见提示。
    /// 必须在 UI（STA）线程调用。
    /// </summary>
    public static bool TrySetImage(BitmapSource image, out string? error)
    {
        const int attempts = 3;
        const int delayMs = 50;
        error = null;

        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                Clipboard.SetImage(image);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                if (attempt < attempts)
                {
                    // 注意：不要在这里 Clipboard.Clear()，那会把用户原来的剪贴板内容也一起丢掉。
                    Thread.Sleep(delayMs);
                }
            }
        }

        return false;
    }
}
