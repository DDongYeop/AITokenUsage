namespace AITokenUsage.Core.Taskbar;

/// <summary>A rectangle in physical pixels (right/bottom exclusive, like Win32 RECT).</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public PixelRect Offset(int dx, int dy) => new(Left + dx, Top + dy, Right + dx, Bottom + dy);
    public bool IsInside(PixelRect outer) => Left >= outer.Left && Top >= outer.Top && Right <= outer.Right && Bottom <= outer.Bottom;

    /// <summary>Win32 PtInRect semantics: left/top inclusive, right/bottom exclusive.</summary>
    public bool Contains(PixelPoint point) => point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;
}
