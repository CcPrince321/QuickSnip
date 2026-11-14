using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuickSnip.Native;
using QuickSnip.Services;

namespace QuickSnip.Overlay;

/// <summary>
/// 一块显示器上的遮罩窗口。它只负责两件事：
/// 1) 把自己那块冻结帧切片按 1:1 物理像素铺出来；
/// 2) 把会话给的**虚拟桌面物理像素选区**换算成本窗口的 DIP 再画出来。
/// 它不知道选区是怎么产生的，也不做任何坐标判断 —— 那是 OverlaySession 的事。
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly OverlaySession _session;
    private readonly Size _clientPhysical;
    private HwndSource? _source;
    private bool _closing;
    private int _moveLogs;
    private bool _loggedFirstSelection;

    public OverlayWindow(OverlaySession session, MonitorInfo monitor, PixelRect virtualBounds, BitmapSource frame, bool showHint)
    {
        InitializeComponent();

        _session = session;
        Monitor = monitor;
        _clientPhysical = new Size(monitor.Bounds.Width, monitor.Bounds.Height);

        int left = monitor.Bounds.X - virtualBounds.X;
        int top = monitor.Bounds.Y - virtualBounds.Y;
        var sliceRect = new Int32Rect(left, top, monitor.Bounds.Width, monitor.Bounds.Height);
        var slice = new CroppedBitmap(frame, sliceRect);
        slice.Freeze();
        FrameImage.Source = slice;

        // 初值：按本屏 DPI 折成 DIP。真正的位置与尺寸由 SourceInitialized 里的
        // SetWindowPos（物理像素）钉死，这里只是一个防止闪一下的近似。
        double scale = monitor.Scale;
        Width = monitor.Bounds.Width / scale;
        Height = monitor.Bounds.Height / scale;
        Left = monitor.Bounds.X / scale;
        Top = monitor.Bounds.Y / scale;

        HintTag.Visibility = showHint ? Visibility.Visible : Visibility.Collapsed;

        SetSelection(null);

        Log.Write($"OverlayWindow ctor: monitor={monitor.Bounds} dpi={monitor.Dpi} scale={scale:0.##} " +
                  $"slice={slice.PixelWidth}x{slice.PixelHeight} dipW={Width:0.##} dipH={Height:0.##}");
    }

    public MonitorInfo Monitor { get; }

    public IntPtr Handle { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        Handle = new WindowInteropHelper(this).Handle;
        NativeMethods.ApplyOverlayStyles(Handle);

        _source = HwndSource.FromHwnd(Handle);
        _source?.AddHook(WndProc);

        // 关键：连调两次 SetWindowPos。
        // 第一次可能触发 WM_DPICHANGED，WPF 会用系统建议的矩形（按 DPI 比例缩放后的）
        // 反过来改窗口尺寸；第二次调用时窗口 DPI 已经稳定，不会再被打断，尺寸就钉住了。
        PositionHard();

        Log.Write($"OverlayWindow hook attached: hwnd=0x{Handle.ToInt64():X} rect={ActualRectText()}");
    }

    private string ActualRectText()
    {
        if (NativeMethods.GetWindowRect(Handle, out NativeMethods.RECT r))
            return $"({r.Left},{r.Top})-({r.Right},{r.Bottom}) {r.Right - r.Left}x{r.Bottom - r.Top}";
        return "<GetWindowRect failed>";
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        // 再兜一次：万一 DPI 切换是在 Show 之后才异步落地的。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            PositionHard();
            Log.Write($"OverlayWindow contentRendered: rect={ActualRectText()}");
        }), DispatcherPriority.Loaded);
    }

    private void PositionHard()
    {
        if (Handle == IntPtr.Zero) return;
        for (int i = 0; i < 2; i++)
        {
            NativeMethods.SetWindowPos(
                Handle,
                NativeMethods.HWND_TOPMOST,
                Monitor.Bounds.X, Monitor.Bounds.Y,
                Monitor.Bounds.Width, Monitor.Bounds.Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeMethods.WM_LBUTTONDOWN:
                handled = true;
                Log.Write($"WM_LBUTTONDOWN hwnd=0x{hwnd.ToInt64():X}");
                _session.OnMouseDown(this);
                break;

            case NativeMethods.WM_MOUSEMOVE:
                handled = true;
                if (_moveLogs < 3)
                {
                    _moveLogs++;
                    NativeMethods.POINT mp = NativeMethods.GetCursorPosition();
                    Log.Write($"WM_MOUSEMOVE # {_moveLogs} cursor=({mp.X},{mp.Y}) capture=0x{NativeMethods.GetCapture().ToInt64():X}");
                }
                _session.OnMouseMove();
                break;

            case NativeMethods.WM_LBUTTONUP:
                handled = true;
                Log.Write("WM_LBUTTONUP");
                _session.OnMouseUp();
                break;

            case NativeMethods.WM_LBUTTONDBLCLK:
                handled = true;
                Log.Write("WM_LBUTTONDBLCLK");
                _session.OnDoubleClick();
                break;

            case NativeMethods.WM_RBUTTONDOWN:
                handled = true;
                Log.Write("WM_RBUTTONDOWN -> cancel");
                _session.Cancel();
                break;

            case NativeMethods.WM_KEYDOWN:
                handled = HandleKey(wParam.ToInt32());
                Log.Write($"WM_KEYDOWN vk=0x{wParam.ToInt32():X} handled={handled}");
                break;

            case NativeMethods.WM_DISPLAYCHANGE:
                // 分辨率 / 显示器拓扑变了，冻结帧和遮罩尺寸就都对不上了。
                // 直接放弃本次会话，让用户重按热键，比中途重排 N 个窗口安全得多。
                Log.Write("WM_DISPLAYCHANGE -> abort session");
                _session.AbortOnDisplayChange();
                break;
        }

        return IntPtr.Zero;
    }

    private bool HandleKey(int virtualKey)
    {
        if (virtualKey == NativeMethods.VK_ESCAPE)
        {
            _session.Cancel();
            return true;
        }

        if (virtualKey == 0x41 /* A */ && IsControlDown())
        {
            _session.SelectAll();
            return true;
        }

        return false;
    }

    private static bool IsControlDown() => (NativeMethods.GetKeyState((int)NativeMethods.VK_CONTROL) & 0x8000) != 0;

    /// <summary>
    /// 把虚拟桌面物理像素选区画到本窗口上。选区可能完全在别的屏上 —— 那样
    /// EvenOdd 挖不出洞，窗口就是整块压暗，正好是想要的效果。
    /// </summary>
    public void SetSelection(PixelRect? selectionVsp)
    {
        double scale = Monitor.Scale;
        double w = _clientPhysical.Width / scale;
        double h = _clientPhysical.Height / scale;

        if (selectionVsp == null)
        {
            Dim.Data = new RectangleGeometry(new Rect(0, 0, w, h));
            SelBorder.Visibility = Visibility.Collapsed;
            SizeTag.Visibility = Visibility.Collapsed;
            return;
        }

        PixelRect s = selectionVsp.Value;
        HintTag.Visibility = Visibility.Collapsed;

        var local = new Rect(
            (s.X - Monitor.Bounds.X) / scale,
            (s.Y - Monitor.Bounds.Y) / scale,
            s.Width / scale,
            s.Height / scale);

        if (!_loggedFirstSelection)
        {
            _loggedFirstSelection = true;
            Log.Write($"first selection render: vsp={s} localDip=({local.Left:0.#},{local.Top:0.#}) {local.Width:0.#}x{local.Height:0.#} " +
                      $"canvas={AnnoLayer.ActualWidth:0.#}x{AnnoLayer.ActualHeight:0.#} root={Root.ActualWidth:0.#}x{Root.ActualHeight:0.#}");
        }

        var geometry = new GeometryGroup { FillRule = FillRule.EvenOdd };
        geometry.Children.Add(new RectangleGeometry(new Rect(0, 0, w, h)));
        geometry.Children.Add(new RectangleGeometry(local));
        Dim.Data = geometry;

        bool visibleHere = local.Right > 0 && local.Bottom > 0 && local.Left < w && local.Top < h;
        if (!visibleHere)
        {
            SelBorder.Visibility = Visibility.Collapsed;
            SizeTag.Visibility = Visibility.Collapsed;
            return;
        }

        SelBorder.Visibility = Visibility.Visible;
        SelBorder.Width = Math.Max(0, local.Width);
        SelBorder.Height = Math.Max(0, local.Height);
        Canvas.SetLeft(SelBorder, local.Left);
        Canvas.SetTop(SelBorder, local.Top);

        // 尺寸读数用物理像素，和最终输出一致，避免用户以为被缩放。
        SizeText.Text = $"{s.Width} × {s.Height}";
        SizeTag.Visibility = Visibility.Visible;
        SizeTag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double tagW = SizeTag.DesiredSize.Width;
        double tagH = SizeTag.DesiredSize.Height;

        double tagLeft = local.Left;
        double tagTop = local.Top - tagH - 4;
        if (tagTop < 0) tagTop = local.Top + 4;
        if (tagLeft + tagW > w) tagLeft = Math.Max(0, w - tagW - 2);
        Canvas.SetLeft(SizeTag, tagLeft);
        Canvas.SetTop(SizeTag, tagTop);
    }

    /// <summary>会话结束时的关闭路径：先摘钩子再关，避免关窗期间还收到消息。</summary>
    public void CloseFromSession()
    {
        if (_closing) return;
        _closing = true;

        if (_source != null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }

        try { Close(); } catch { /* 已经关了 */ }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_source != null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }
        base.OnClosed(e);
    }
}
