using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuickSnip.Native;
using QuickSnip.Services;

namespace QuickSnip.Overlay;

/// <summary>
/// 一次截图会话。它是唯一持有「选区」的地方，且选区永远存在**虚拟桌面物理像素坐标系**里。
/// 各遮罩窗口只把这块矩形换算成自己的 DIP 显示，从不各自维护坐标。
///
/// 模型：先抓帧，再上遮罩；拖拽期间只改一个 PixelRect，从不重新截屏。
/// </summary>
public sealed class OverlaySession : IDisposable
{
    /// <summary>小于这个尺寸（物理像素）的选区视为误触。</summary>
    private const int MinSelectionPx = 2;

    private readonly BitmapSource _frame;
    private readonly PixelRect _virtualBounds;
    private readonly List<MonitorInfo> _monitors;
    private readonly AppSettings _settings;
    private readonly Action<BitmapSource?> _onFinished;
    private readonly List<OverlayWindow> _windows = new();
    private readonly DispatcherTimer _clickCancelTimer;

    private int _anchorX;
    private int _anchorY;
    private int _lastClickX;
    private int _lastClickY;
    private bool _hasAnchor;
    private PixelRect? _selection;
    private bool _finished;
    private bool _disposed;

    private OverlaySession(
        BitmapSource frame,
        PixelRect virtualBounds,
        List<MonitorInfo> monitors,
        AppSettings settings,
        Action<BitmapSource?> onFinished)
    {
        _frame = frame;
        _virtualBounds = virtualBounds;
        _monitors = monitors;
        _settings = settings;
        _onFinished = onFinished;

        // 单击取消要被「双击全屏」让路，所以延迟一个系统双击时间去取消。
        _clickCancelTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(50, (int)NativeMethods.GetDoubleClickTime())),
        };
        _clickCancelTimer.Tick += (_, _) =>
        {
            _clickCancelTimer.Stop();
            Cancel();
        };
    }

    /// <summary>
    /// 抓帧 + 上遮罩。抓帧失败会抛异常，由调用方给用户可见提示。
    /// onFinished(null) 表示取消；否则参数是裁剪好的、DPI=96 的最终图像。
    /// </summary>
    public static OverlaySession Begin(AppSettings settings, Action<BitmapSource?> onFinished)
    {
        PixelRect virtualBounds = ScreenLayout.GetVirtualBounds();
        if (virtualBounds.IsEmpty)
            throw new InvalidOperationException("拿不到虚拟桌面尺寸（GetSystemMetrics 返回 0）。");

        List<MonitorInfo> monitors = ScreenLayout.Enumerate();
        if (monitors.Count == 0)
            throw new InvalidOperationException("没有检测到任何显示器。");

        // 先拍，后挡。此刻屏幕上没有任何属于我们的窗口，所以不需要 hide + sleep。
        BitmapSource frame = CaptureService.CaptureVirtualDesktop(virtualBounds);
        Log.Write($"frame captured: {frame.PixelWidth}x{frame.PixelHeight} dpi={frame.DpiX:0.##} " +
                  $"vsp={virtualBounds} monitors={monitors.Count}");

        var session = new OverlaySession(frame, virtualBounds, monitors, settings, onFinished);
        session.ShowOverlays();
        return session;
    }

    private void ShowOverlays()
    {
        NativeMethods.POINT cursor = NativeMethods.GetCursorPosition();
        MonitorInfo targetMonitor = _monitors.FirstOrDefault(m => m.Bounds.Contains(cursor.X, cursor.Y)) ?? _monitors[0];

        foreach (MonitorInfo monitor in _monitors)
        {
            var window = new OverlayWindow(this, monitor, _virtualBounds, _frame, ReferenceEquals(monitor, targetMonitor));
            _windows.Add(window);
            window.Show();
        }

        // 键盘（Esc / Ctrl+A）只会到前台窗口，所以把鼠标所在那块屏的遮罩提上来。
        OverlayWindow target = _windows.FirstOrDefault(w => ReferenceEquals(w.Monitor, targetMonitor)) ?? _windows[0];
        NativeMethods.ForceForeground(target.Handle);

        Log.Write($"overlays shown: count={_windows.Count} cursor=({cursor.X},{cursor.Y}) " +
                  $"target=0x{target.Handle.ToInt64():X} foreground=0x{NativeMethods.GetForegroundWindow().ToInt64():X}");
    }

    // ---------------- 鼠标 ----------------

    public void OnMouseDown(OverlayWindow source)
    {
        if (_finished) return;

        NativeMethods.POINT p = NativeMethods.GetCursorPosition();

        // 上一次是「单击但还没决定取消」的状态，且这一下几乎在同一点：
        // 判定为双击 → 全屏。这样即使窗口类没有 CS_DBLCLKS、收不到 WM_LBUTTONDBLCLK，
        // 双击也照样能用。
        if (_clickCancelTimer.IsEnabled)
        {
            _clickCancelTimer.Stop();

            int tolX = Math.Max(4, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXDOUBLECLK) / 2);
            int tolY = Math.Max(4, NativeMethods.GetSystemMetrics(NativeMethods.SM_CYDOUBLECLK) / 2);
            if (Math.Abs(p.X - _lastClickX) <= tolX && Math.Abs(p.Y - _lastClickY) <= tolY)
            {
                Commit(FullScreenRect());
                return;
            }
        }

        _anchorX = p.X;
        _anchorY = p.Y;
        _hasAnchor = true;
        _selection = null;

        // 用 Win32 SetCapture，而不是 WPF 的 CaptureMouse：
        // 鼠标可能从这块屏拖到另一块屏（不同 HWND、不同 DPI），
        // 只有 Win32 捕获才能保证消息始终回到按下的那个窗口。
        NativeMethods.SetCapture(source.Handle);
        UpdateAllWindows();
    }

    public void OnMouseMove()
    {
        if (_finished || !_hasAnchor) return;

        NativeMethods.POINT p = NativeMethods.GetCursorPosition();
        _selection = Clamp(PixelRect.FromPoints(_anchorX, _anchorY, p.X, p.Y));
        UpdateAllWindows();
    }

    public void OnMouseUp()
    {
        if (_finished || !_hasAnchor) return;

        NativeMethods.ReleaseCapture();
        _hasAnchor = false;

        PixelRect? selection = _selection;
        if (selection == null || selection.Value.Width < MinSelectionPx || selection.Value.Height < MinSelectionPx)
        {
            // 单击 = 取消。但要给双击（全屏）留时间窗，否则第二下按不到。
            NativeMethods.POINT p = NativeMethods.GetCursorPosition();
            _lastClickX = p.X;
            _lastClickY = p.Y;

            _selection = null;
            UpdateAllWindows();

            if (_settings.DoubleClickFullScreen) _clickCancelTimer.Start();
            else Cancel();
            return;
        }

        Commit(selection.Value);
    }

    public void OnDoubleClick()
    {
        if (_finished) return;
        _clickCancelTimer.Stop();
        Commit(FullScreenRect());
    }

    public void SelectAll()
    {
        if (_finished) return;
        _clickCancelTimer.Stop();
        Commit(FullScreenRect());
    }

    public void Cancel()
    {
        if (_finished) return;
        Finish(null);
    }

    /// <summary>显示器插拔 / 分辨率变化：直接放弃本次会话，不中途重排 N 个窗口。</summary>
    public void AbortOnDisplayChange() => Cancel();

    private PixelRect FullScreenRect()
    {
        if (_settings.FullScreenUseWholeVirtualDesktop) return _virtualBounds;

        NativeMethods.POINT p = NativeMethods.GetCursorPosition();
        MonitorInfo monitor = _monitors.FirstOrDefault(m => m.Bounds.Contains(p.X, p.Y)) ?? _monitors[0];
        return monitor.Bounds;
    }

    // ---------------- 提交 ----------------

    private void Commit(PixelRect selectionVsp)
    {
        if (_finished) return;

        _selection = selectionVsp;

        // VSP -> 位图像素：只是一个平移，因为位图原点就是虚拟桌面原点。
        PixelRect bitmapRect = selectionVsp.Offset(-_virtualBounds.X, -_virtualBounds.Y);

        BitmapSource image;
        try
        {
            image = CaptureService.Crop(_frame, bitmapRect);
        }
        catch (Exception ex)
        {
            Log.Exception($"crop failed vsp={selectionVsp} bmp={bitmapRect}", ex);
            Finish(null);
            return;
        }

        Log.Write($"commit: vsp={selectionVsp} bmp={bitmapRect} -> {image.PixelWidth}x{image.PixelHeight}");
        Finish(image);
    }

    private void Finish(BitmapSource? result)
    {
        if (_finished) return;
        _finished = true;
        _clickCancelTimer.Stop();

        if (_hasAnchor) NativeMethods.ReleaseCapture();
        _hasAnchor = false;

        CloseWindows();
        try
        {
            _onFinished(result);
        }
        finally
        {
            Dispose();
        }
    }

    private PixelRect Clamp(PixelRect rect)
    {
        int l = Math.Max(rect.X, _virtualBounds.X);
        int t = Math.Max(rect.Y, _virtualBounds.Y);
        int r = Math.Min(rect.Right, _virtualBounds.Right);
        int b = Math.Min(rect.Bottom, _virtualBounds.Bottom);
        return PixelRect.FromLTRB(l, t, r, b);
    }

    private void UpdateAllWindows()
    {
        foreach (OverlayWindow window in _windows)
            window.SetSelection(_selection);
    }

    private void CloseWindows()
    {
        foreach (OverlayWindow window in _windows.ToList())
        {
            window.CloseFromSession();
        }
        _windows.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _clickCancelTimer.Stop();

        if (_hasAnchor)
        {
            NativeMethods.ReleaseCapture();
            _hasAnchor = false;
        }

        CloseWindows();
    }
}
