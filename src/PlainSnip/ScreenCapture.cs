using System.Runtime.InteropServices;

namespace PlainSnip;

/// <summary>
/// 屏幕捕获服务：M1a 快照冻结与 M4 滚动截图分帧捕获共用的唯一采集入口。
/// 全部物理像素坐标（PMV2 下 Screen.Bounds 即物理矩形）。
/// </summary>
public static class ScreenCapture
{
    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h, IntPtr hdcSrc, int xSrc, int ySrc, int rop);

    /// <summary>单条「屏幕矩形 → 位图」的记录（M1a 快照与 M4 分帧共用）。</summary>
    public readonly record struct ScreenShot(Rectangle Bounds, Bitmap Bitmap);

    /// <summary>截取一块虚拟桌面物理矩形区域（可跨屏）。</summary>
    public static Bitmap CaptureRegion(Rectangle region)
    {
        var bmp = new Bitmap(region.Width, region.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        var hdcDest = g.GetHdc();
        var hdcSrc = GetDC(IntPtr.Zero);
        try
        {
            BitBlt(hdcDest, 0, 0, region.Width, region.Height, hdcSrc, region.X, region.Y, SRCCOPY | CAPTUREBLT);
        }
        finally
        {
            g.ReleaseHdc(hdcDest);
            ReleaseDC(IntPtr.Zero, hdcSrc);
        }
        return bmp;
    }

    /// <summary>冻结所有屏幕快照（M1a 入口）。</summary>
    public static List<ScreenShot> CaptureAllScreens()
    {
        var result = new List<ScreenShot>();
        foreach (Screen screen in Screen.AllScreens)
        {
            result.Add(new ScreenShot(screen.Bounds, CaptureRegion(screen.Bounds)));
        }
        return result;
    }

    /// <summary>
    /// 从各屏快照中合成选区位图（选区可跨屏）。
    /// 纯函数，--selftest 用合成数据覆盖。
    /// </summary>
    public static Bitmap ComposeSelection(IReadOnlyList<ScreenShot> shots, Rectangle selection)
    {
        var bmp = new Bitmap(selection.Width, selection.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        foreach (var shot in shots)
        {
            var inter = Rectangle.Intersect(selection, shot.Bounds);
            if (inter.IsEmpty) continue;
            var src = new Rectangle(inter.X - shot.Bounds.X, inter.Y - shot.Bounds.Y, inter.Width, inter.Height);
            var dest = new Rectangle(inter.X - selection.X, inter.Y - selection.Y, inter.Width, inter.Height);
            g.DrawImage(shot.Bitmap, dest, src, GraphicsUnit.Pixel);
        }
        return bmp;
    }
}
