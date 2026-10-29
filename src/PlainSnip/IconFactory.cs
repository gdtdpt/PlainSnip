using System.Drawing.Drawing2D;

namespace PlainSnip;

/// <summary>
/// 图标工厂：从嵌入资源加载 App.ico（tools/make_icon.ps1 生成，含 16/24/32/48/256 多尺寸）。
/// exe 文件图标由 csproj 的 ApplicationIcon 使用同一 ico，两处保持一致。
/// </summary>
public static class IconFactory
{
    public static Icon CreateTrayIcon()
    {
        using var stream = typeof(IconFactory).Assembly.GetManifestResourceStream("PlainSnip.App.ico")
            ?? throw new InvalidOperationException("嵌入资源 PlainSnip.App.ico 缺失");
        // 按系统小图标尺寸（DPI 感知）选取最合适的条目：200% 屏托盘实际用 32px，避免大图缩小发虚
        var size = SystemInformation.SmallIconSize;
        return new Icon(stream, size.Width, size.Height);
    }
}
