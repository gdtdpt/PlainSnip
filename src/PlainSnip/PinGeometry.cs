using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PlainSnip;

/// <summary>
/// 贴图几何与像素处理的纯函数集合（§5.7），--selftest 用合成数据覆盖。
/// 全部坐标为虚拟桌面物理像素。
/// </summary>
public static class PinGeometry
{
    public const float MinScale = 0.1f;
    public const float MaxScale = 8f;

    private const int MinOpacityPercent = 10;
    private const int MaxOpacityPercent = 100;

    public static float ClampScale(float scale) =>
        float.IsNaN(scale) || scale <= 0 ? MinScale : Math.Clamp(scale, MinScale, MaxScale);

    public static int ClampOpacityPercent(int percent) =>
        Math.Clamp(percent, MinOpacityPercent, MaxOpacityPercent);

    /// <summary>不透明度百分比 → 分层窗口 alpha 字节。</summary>
    public static byte ToAlphaByte(int percent) =>
        (byte)Math.Round(ClampOpacityPercent(percent) * 255f / 100f);

    public static Size ScaledSize(Size imageSize, float scale)
    {
        scale = ClampScale(scale);
        return new Size(
            Math.Max(1, (int)Math.Round(imageSize.Width * scale)),
            Math.Max(1, (int)Math.Round(imageSize.Height * scale)));
    }

    /// <summary>
    /// 绕过锚点缩放：保持锚点下的像素在缩放前后位置不变（滚轮缩放以光标为锚点）。
    /// </summary>
    public static Rectangle ZoomAround(Rectangle window, Size imageSize, float newScale, Point anchor)
    {
        var size = ScaledSize(imageSize, newScale);
        double rx = window.Width > 0 ? (anchor.X - window.X) / (double)window.Width : 0.5;
        double ry = window.Height > 0 ? (anchor.Y - window.Y) / (double)window.Height : 0.5;
        int x = (int)Math.Round(anchor.X - rx * size.Width);
        int y = (int)Math.Round(anchor.Y - ry * size.Height);
        return new Rectangle(x, y, size.Width, size.Height);
    }

    /// <summary>
    /// 初始摆放：1:1 物理像素，居中于光标；图大于工作区 80% 时先等比缩小，最后夹回工作区内。
    /// </summary>
    public static (float Scale, Rectangle Bounds) PlaceInitial(Rectangle workArea, Size imageSize, Point cursor)
    {
        float scale = 1f;
        if (imageSize.Width > workArea.Width * 0.8f || imageSize.Height > workArea.Height * 0.8f)
        {
            float fit = Math.Min(
                workArea.Width * 0.8f / Math.Max(1, imageSize.Width),
                workArea.Height * 0.8f / Math.Max(1, imageSize.Height));
            scale = ClampScale(fit);
        }

        var size = ScaledSize(imageSize, scale);
        int x = cursor.X - size.Width / 2;
        int y = cursor.Y - size.Height / 2;
        x = Math.Clamp(x, workArea.Left, Math.Max(workArea.Left, workArea.Right - size.Width));
        y = Math.Clamp(y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - size.Height));
        return (scale, new Rectangle(x, y, size.Width, size.Height));
    }

    /// <summary>把源图整理成可缓存的 32bppPArgb 位图；带 alpha 时先压到不透明背景上（LWA_ALPHA 不支持逐像素透明）。</summary>
    public static Bitmap Prepare(Bitmap source, Color background)
    {
        var bmp = new Bitmap(Math.Max(1, source.Width), Math.Max(1, source.Height), PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(background);
        g.DrawImage(source, new Rectangle(0, 0, bmp.Width, bmp.Height),
            new Rectangle(0, 0, source.Width, source.Height), GraphicsUnit.Pixel);
        return bmp;
    }

    /// <summary>抽样检测位图是否含半透明像素（逐行抽样，避免全图 GetPixel）。</summary>
    public static bool HasAlpha(Bitmap bmp)
    {
        var pf = bmp.PixelFormat;
        bool alphaFormat = pf is PixelFormat.Format32bppArgb or PixelFormat.Format32bppPArgb;
        if (!alphaFormat) return false;

        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = Math.Abs(data.Stride);
            var row = new byte[stride];
            int step = Math.Max(1, bmp.Height / 64);   // 最多抽 64 行
            for (int y = 0; y < bmp.Height; y += step)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, stride);
                for (int x = 0; x < bmp.Width; x++)
                {
                    if (row[x * 4 + 3] < 250) return true;
                }
            }
            return false;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
