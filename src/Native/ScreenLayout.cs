using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace QuickSnip.Native;

/// <summary>
/// 屏幕布局查询。所有返回值都是**物理像素**，前提是进程已声明 PerMonitorV2
/// （见 app.manifest + <see cref="EnsurePerMonitorV2"/>）。
/// </summary>
public static class ScreenLayout
{
    /// <summary>
    /// 兜底：manifest 已声明 PerMonitorV2 时这里会返回 false（ACCESS_DENIED），无害。
    /// 主要用于被宿主进程拉起等 manifest 不生效的边角情况。
    /// 必须在创建任何窗口 / 获取任何 DC 之前调用。
    /// </summary>
    public static void EnsurePerMonitorV2()
    {
        try { NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
        catch { /* 老系统没有这个导出，忽略 */ }
    }

    /// <summary>
    /// 虚拟桌面物理矩形。x/y 可能为负（副屏在左/上）。
    /// </summary>
    public static PixelRect GetVirtualBounds()
    {
        int x = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        int y = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        int w = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        int h = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        if (w <= 0 || h <= 0) return default;
        return new PixelRect(x, y, w, h);
    }

    public static List<MonitorInfo> Enumerate()
    {
        var list = new List<MonitorInfo>();

        // 委托必须在调用期间保持存活，否则会被 GC 回收导致回调崩溃。
        NativeMethods.MonitorEnumProc callback = (IntPtr hMon, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data) =>
        {
            var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (!NativeMethods.GetMonitorInfo(hMon, ref mi)) return true;

            uint dpi = 96;
            try
            {
                if (NativeMethods.GetDpiForMonitor(hMon, NativeMethods.MDT_EFFECTIVE_DPI, out uint dx, out _) == 0 && dx > 0)
                    dpi = dx;
            }
            catch
            {
                dpi = 96;
            }

            var bounds = PixelRect.FromLTRB(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom);
            bool primary = (mi.dwFlags & 1u) != 0; // MONITORINFOF_PRIMARY
            list.Add(new MonitorInfo(hMon, bounds, dpi, primary));
            return true;
        };

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);

        // 保持稳定顺序：主屏优先，其次按 (Y, X)。不依赖 EnumDisplayMonitors 的枚举顺序。
        list.Sort((a, b) =>
        {
            if (a.IsPrimary != b.IsPrimary) return a.IsPrimary ? -1 : 1;
            int c = a.Bounds.Y.CompareTo(b.Bounds.Y);
            return c != 0 ? c : a.Bounds.X.CompareTo(b.Bounds.X);
        });
        return list;
    }
}
