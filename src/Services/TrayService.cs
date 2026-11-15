using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using QuickSnip.Native;
using Forms = System.Windows.Forms;

namespace QuickSnip.Services;

/// <summary>
/// 托盘图标。图标在运行时画出来，省掉一个 .ico 资源文件。
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private Icon? _icon;
    private bool _disposed;

    public TrayService()
    {
        _icon = CreateIcon();

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = "QuickSnip 截图（Ctrl+Alt+A）",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _notifyIcon.DoubleClick += (_, _) => CaptureRequested?.Invoke();
    }

    public event Action? CaptureRequested;
    public event Action? SettingsRequested;
    public event Action? ScreenInfoRequested;
    public event Action? OpenSaveFolderRequested;
    public event Action? ExitRequested;

    public void SetTooltip(string hotKeyDisplay)
    {
        // NotifyIcon.Text 上限 63 字符。
        string text = $"QuickSnip 截图（{hotKeyDisplay}）";
        _notifyIcon.Text = text.Length > 63 ? text.Substring(0, 63) : text;
    }

    private Forms.ContextMenuStrip BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip();

        var capture = new Forms.ToolStripMenuItem("截图");
        capture.Click += (_, _) => CaptureRequested?.Invoke();

        var settings = new Forms.ToolStripMenuItem("热键设置…");
        settings.Click += (_, _) => SettingsRequested?.Invoke();

        var screenInfo = new Forms.ToolStripMenuItem("屏幕信息…");
        screenInfo.Click += (_, _) => ScreenInfoRequested?.Invoke();

        var openFolder = new Forms.ToolStripMenuItem("打开截图文件夹");
        openFolder.Click += (_, _) => OpenSaveFolderRequested?.Invoke();

        var exit = new Forms.ToolStripMenuItem("退出");
        exit.Click += (_, _) => ExitRequested?.Invoke();

        menu.Items.Add(capture);
        menu.Items.Add(openFolder);
        menu.Items.Add(settings);
        menu.Items.Add(screenInfo);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exit);
        return menu;
    }

    public void ShowBalloon(string title, string text, bool isError = false)
    {
        if (_disposed) return;
        try
        {
            _notifyIcon.ShowBalloonTip(
                2500,
                title,
                text,
                isError ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
        }
        catch
        {
            // 通知中心被系统策略关掉时会抛，忽略。
        }
    }

    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var background = new SolidBrush(Color.FromArgb(255, 240, 78, 62));
            g.FillEllipse(background, 1f, 1f, 30f, 30f);

            using var pen = new Pen(Color.White, 2.2f) { LineJoin = LineJoin.Round };
            // 取景框
            g.DrawRectangle(pen, 8f, 11f, 16f, 12f);
            // 快门凸起
            g.DrawLines(pen, new[]
            {
                new PointF(12f, 11f), new PointF(13.5f, 7.5f),
                new PointF(18.5f, 7.5f), new PointF(20f, 11f),
            });
        }

        IntPtr hIcon = bitmap.GetHicon();
        try
        {
            using Icon temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon?.Dispose();
        _icon = null;
    }
}
