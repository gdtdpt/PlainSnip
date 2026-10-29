using System.Drawing.Drawing2D;

namespace PlainSnip;

/// <summary>
/// M0 托盘图标：运行时用 GDI+ 画（圆角底 + 白色 P），不引 ico 资源文件。
/// 后续里程碑若需要更像样的图标，再换成正式 ico。
/// </summary>
public static class IconFactory
{
    public static Icon CreateTrayIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using var path = new GraphicsPath();
            path.AddArc(0, 0, 10, 10, 180, 90);
            path.AddArc(21, 0, 10, 10, 270, 90);
            path.AddArc(21, 21, 10, 10, 0, 90);
            path.AddArc(0, 21, 10, 10, 90, 90);
            path.CloseFigure();
            using var bg = new SolidBrush(Color.FromArgb(38, 108, 200));
            g.FillPath(bg, path);

            using var font = new Font("Segoe UI", 19f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString("P", font, Brushes.White, new RectangleF(0, -1, 32, 33), sf);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
