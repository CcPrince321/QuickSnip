using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickSnip.Native;

namespace QuickSnip.Services;

/// <summary>
/// 冻结帧采集与裁剪。全部在物理像素空间操作，从不经过 DIP。
/// </summary>
public static class CaptureService
{
    /// <summary>
    /// 一次性抓取整个虚拟桌面，返回一张 DPI 元数据固定为 96 的位图。
    /// 位图像素 (0,0) 对应虚拟桌面物理坐标 (virtualBounds.X, virtualBounds.Y)。
    /// </summary>
    public static BitmapSource CaptureVirtualDesktop(PixelRect virtualBounds)
    {
        if (virtualBounds.IsEmpty)
            throw new InvalidOperationException("虚拟桌面尺寸无效（GetSystemMetrics 返回 0）。");

        int w = virtualBounds.Width;
        int h = virtualBounds.Height;

        // 让 DWM 先把当前帧合成完，减少抓到半帧的概率。
        try { NativeMethods.DwmFlush(); } catch { }

        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) throw new Win32Exception("GetDC(NULL) 失败。");

        IntPtr memDc = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldObj = IntPtr.Zero;

        try
        {
            memDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero) throw new Win32Exception("CreateCompatibleDC 失败。");

            hBitmap = NativeMethods.CreateCompatibleBitmap(screenDc, w, h);
            if (hBitmap == IntPtr.Zero) throw new Win32Exception("CreateCompatibleBitmap 失败（虚拟桌面过大？）。");

            oldObj = NativeMethods.SelectObject(memDc, hBitmap);

            // 源坐标用虚拟桌面原点，可以是负数；屏幕 DC 覆盖整个虚拟桌面，负坐标合法。
            // CAPTUREBLT 让分层窗口（部分带透明效果的窗口）也能被包含进来。
            bool ok = NativeMethods.BitBlt(
                memDc, 0, 0, w, h,
                screenDc, virtualBounds.X, virtualBounds.Y,
                NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);

            if (!ok) throw new Win32Exception("BitBlt 失败（目标区域可能被独占全屏独占）。");

            BitmapSource raw = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            return NormalizeTo96Dpi(raw);
        }
        finally
        {
            if (oldObj != IntPtr.Zero) NativeMethods.SelectObject(memDc, oldObj);
            if (hBitmap != IntPtr.Zero) NativeMethods.DeleteObject(hBitmap);
            if (memDc != IntPtr.Zero) NativeMethods.DeleteDC(memDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>
    /// 从冻结帧里裁一块（入参是位图像素坐标，即 VSP 减去虚拟原点）。
    /// 纯内存拷贝，不碰屏幕。
    /// </summary>
    public static BitmapSource Crop(BitmapSource frame, PixelRect bitmapRect)
    {
        var r = bitmapRect.Intersect(new PixelRect(0, 0, frame.PixelWidth, frame.PixelHeight));
        if (r.IsEmpty) throw new ArgumentOutOfRangeException(nameof(bitmapRect), "裁剪区域为空或超出冻结帧范围。");

        var cropped = new CroppedBitmap(frame, new Int32Rect(r.X, r.Y, r.Width, r.Height));
        cropped.Freeze();

        // 再拷一次，保证输出与原冻结帧完全解耦，且 DPI 元数据确定为 96。
        return CopyWithDpi(cropped, 96);
    }

    /// <summary>
    /// 保证位图的 DPI 元数据是 96：否则粘到 Word / 微信里会被二次缩放，
    /// 用户会看到「截图变大或变小」。
    /// </summary>
    private static BitmapSource NormalizeTo96Dpi(BitmapSource source)
    {
        if (Math.Abs(source.DpiX - 96) < 0.01 && Math.Abs(source.DpiY - 96) < 0.01)
        {
            if (source.CanFreeze) source.Freeze();
            return source;
        }
        return CopyWithDpi(source, 96);
    }

    private static BitmapSource CopyWithDpi(BitmapSource source, double dpi)
    {
        PixelFormat format = source.Format;
        int stride = (source.PixelWidth * format.BitsPerPixel + 7) / 8;
        byte[] buffer = new byte[stride * source.PixelHeight];
        source.CopyPixels(buffer, stride, 0);

        BitmapSource copy = BitmapSource.Create(
            source.PixelWidth, source.PixelHeight, dpi, dpi,
            format, source.Palette, buffer, stride);
        copy.Freeze();
        return copy;
    }
}
