using System;

namespace QuickSnip.Native;

/// <summary>
/// 物理像素矩形。坐标系约定：<see cref="X"/>/<see cref="Y"/> 是虚拟桌面物理像素（VSP）坐标，
/// 原点可以是负数（副屏在主屏左侧或上方时）。
/// 全项目只有这一套坐标，任何 DIP 都只是绘制时的临时中间值。
/// </summary>
public readonly struct PixelRect : IEquatable<PixelRect>
{
    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }

    public PixelRect(int x, int y, int width, int height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static PixelRect FromLTRB(int left, int top, int right, int bottom)
        => new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));

    public static PixelRect FromPoints(int x1, int y1, int x2, int y2)
        => FromLTRB(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));

    public PixelRect Offset(int dx, int dy) => new(X + dx, Y + dy, Width, Height);

    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

    public PixelRect Intersect(PixelRect other)
    {
        int l = Math.Max(X, other.X);
        int t = Math.Max(Y, other.Y);
        int r = Math.Min(Right, other.Right);
        int b = Math.Min(Bottom, other.Bottom);
        return r <= l || b <= t ? default : FromLTRB(l, t, r, b);
    }

    public bool Equals(PixelRect other) => X == other.X && Y == other.Y && Width == other.Width && Height == other.Height;
    public override bool Equals(object? obj) => obj is PixelRect r && Equals(r);
    public override int GetHashCode() => HashCode.Combine(X, Y, Width, Height);
    public override string ToString() => $"({X},{Y}) {Width}×{Height}";
}

/// <summary>一块显示器的物理信息。Scale = DPI / 96.0，只用于绘制与命中测试。</summary>
public sealed class MonitorInfo
{
    public MonitorInfo(IntPtr handle, PixelRect bounds, uint dpi, bool isPrimary)
    {
        Handle = handle;
        Bounds = bounds;
        Dpi = dpi == 0 ? 96u : dpi;
        IsPrimary = isPrimary;
    }

    public IntPtr Handle { get; }

    /// <summary>rcMonitor，虚拟桌面物理像素坐标。</summary>
    public PixelRect Bounds { get; }

    public uint Dpi { get; }

    /// <summary>DPI / 96.0</summary>
    public double Scale => Dpi / 96.0;

    public bool IsPrimary { get; }

    public override string ToString() => $"{Bounds} @{Dpi}dpi{(IsPrimary ? " (primary)" : string.Empty)}";
}
