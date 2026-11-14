using System;
using System.Windows.Media.Imaging;
using QuickSnip.Native;
using QuickSnip.Services;

namespace QuickSnip.CoordProbe;

/// <summary>
/// 无界面自检：验证「虚拟桌面物理像素」这一层的取数、抓帧、裁剪尺寸是否正确。
/// 不保存任何图像，只打印尺寸，用于多屏混合 DPI 的验收排查。
/// 用法：dotnet run --project tools/CoordProbe
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        Log.Write("=== CoordProbe start ===");
        Console.WriteLine($"log file     : {Log.CurrentFile ?? "<none>"}");

        ScreenLayout.EnsurePerMonitorV2();

        PixelRect virt = ScreenLayout.GetVirtualBounds();
        Console.WriteLine($"VSP bounds   : ({virt.X}, {virt.Y})  {virt.Width} x {virt.Height}");
        if (virt.IsEmpty)
        {
            Console.WriteLine("FAIL: 虚拟桌面尺寸为 0");
            return 1;
        }

        var monitors = ScreenLayout.Enumerate();
        Console.WriteLine($"monitors     : {monitors.Count}");
        int sumW = 0;
        foreach (MonitorInfo m in monitors)
        {
            Console.WriteLine($"  bounds={m.Bounds}  dpi={m.Dpi}  scale={m.Scale:0.##}  primary={m.IsPrimary}");
            sumW += m.Bounds.Width;
        }

        // 主屏在 (0,0) 时，虚拟原点就是所有 rc 的左上角；副屏在左/上时 X/Y 必为负。
        bool negativeOrigin = monitors.Exists(m => m.Bounds.X < 0 || m.Bounds.Y < 0);
        Console.WriteLine($"has negative-origin monitor: {negativeOrigin}");

        BitmapSource frame = CaptureService.CaptureVirtualDesktop(virt);
        Console.WriteLine($"frame        : {frame.PixelWidth} x {frame.PixelHeight}  dpiX={frame.DpiX:0.##}");
        Console.WriteLine($"frame size ok: {frame.PixelWidth == virt.Width && frame.PixelHeight == virt.Height}");
        Console.WriteLine($"frame dpi ok : {Math.Abs(frame.DpiX - 96) < 0.01 && Math.Abs(frame.DpiY - 96) < 0.01}");

        int failures = 0;

        // 每块屏中央各裁 200x200，尺寸必须原样返回（不多不少一个像素）。
        for (int i = 0; i < monitors.Count; i++)
        {
            PixelRect b = monitors[i].Bounds;
            var sel = new PixelRect(b.X + b.Width / 2 - 100, b.Y + b.Height / 2 - 100, 200, 200);
            var crop = CaptureService.Crop(frame, sel.Offset(-virt.X, -virt.Y));
            bool ok = crop.PixelWidth == 200 && crop.PixelHeight == 200 && Math.Abs(crop.DpiX - 96) < 0.01;
            if (!ok) failures++;
            Console.WriteLine($"crop monitor{i}: {crop.PixelWidth} x {crop.PixelHeight}  dpiX={crop.DpiX:0.##}  -> {(ok ? "OK" : "FAIL")}");
        }

        // 贴到虚拟桌面右下角外 50px 的选区应被夹紧到边界内，不能抛异常也不能越界。
        var edge = new PixelRect(virt.Right - 30, virt.Bottom - 40, 200, 200);
        var edgeCrop = CaptureService.Crop(frame, edge.Intersect(virt).Offset(-virt.X, -virt.Y));
        bool edgeOk = edgeCrop.PixelWidth == 30 && edgeCrop.PixelHeight == 40;
        if (!edgeOk) failures++;
        Console.WriteLine($"edge crop    : {edgeCrop.PixelWidth} x {edgeCrop.PixelHeight}  -> {(edgeOk ? "OK" : "FAIL")}");

        Console.WriteLine(failures == 0 ? "RESULT: PASS" : $"RESULT: {failures} FAIL");
        return failures == 0 ? 0 : 2;
    }
}
