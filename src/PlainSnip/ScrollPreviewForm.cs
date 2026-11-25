namespace PlainSnip;

using System.Drawing.Drawing2D;

/// <summary>
/// M4 长截图侧边预览窗（docs/m4-scroll-design.md §8）：
/// 实时显示拼接结果视口（以绿框 = 当前选区画面位置为中心），
/// 底部状态行（到底/匹配失败/上限）。WS_EX_NOACTIVATE 不抢焦点，键盘归遮罩窗。
/// 窗体矩形不与选区相交（PlaceNextTo 保证）⇒ 不会进入帧。
/// </summary>
public sealed class ScrollPreviewForm : Form
{
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private readonly CaptureSession _session;
    private readonly Font _font;
    private readonly Font _titleFont;

    public ScrollPreviewForm(CaptureSession session)
    {
        _session = session;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(24, 24, 24);
        Text = "PlainSnip 长截图";
        _font = new Font("Microsoft YaHei UI", 9f);
        _titleFont = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    /// <summary>摆到选区右侧（放不下翻左侧），纵向贴选区顶。必须在 Show 之后调用（DPI 已就位）。</summary>
    public void PlaceNextTo(Rectangle sel)
    {
        var wa = Screen.FromRectangle(sel).WorkingArea;
        Size = new Size(S(210), S(500));
        int x = sel.Right + S(12);
        if (x + Width > wa.Right) x = sel.Left - S(12) - Width;
        if (x < wa.Left) x = wa.Right - Width - S(8);   // 全宽选区：收进屏内（罕见，接受遮挡）
        int y = Math.Clamp(sel.Y, wa.Top, Math.Max(wa.Top, wa.Bottom - Height));
        Bounds = new Rectangle(x, y, Width, Height);
    }

    private int S(int logical) => logical * DeviceDpi / 96;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        int pad = S(8);
        using (var border = new Pen(Color.FromArgb(64, 64, 64), 1f))
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        // 标题 + 高度
        TextRenderer.DrawText(g, "长截图预览", _titleFont,
            new Rectangle(pad, S(4), Width - pad * 2, S(22)), Color.White);
        TextRenderer.DrawText(g, $"{_session.LongResultHeight}px", _font,
            new Rectangle(pad, S(4), Width - pad * 2 - S(2), S(22)), Color.FromArgb(150, 180, 190, 210),
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

        var result = _session.Scroll?.Result;
        var inner = new Rectangle(pad, S(30), Width - pad * 2, Height - S(30) - S(26));
        if (result == null || inner.Width <= 4 || inner.Height <= 4)
        {
            TextRenderer.DrawText(g, "等待首帧…", _font, inner,
                Color.FromArgb(140, 200, 210, 225),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            DrawStatus(g, pad);
            return;
        }

        float zoom = inner.Width / (float)result.Width;
        int frameH = Math.Max(1, _session.Selection.Height);
        int boxTop = Math.Max(0, _session.LongViewTopY);

        // 视口以绿框为中心；结果不足一屏时贴顶
        int visibleRows = Math.Max(1, (int)(inner.Height / zoom));
        int srcTop = boxTop + frameH / 2 - visibleRows / 2;
        srcTop = Math.Clamp(srcTop, 0, Math.Max(0, result.Height - visibleRows));

        var prevInterp = g.InterpolationMode;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        int drawRows = Math.Min(visibleRows, result.Height - srcTop);
        g.DrawImage(result,
            new Rectangle(inner.X, inner.Y, inner.Width, (int)(drawRows * zoom)),
            new Rectangle(0, srcTop, result.Width, drawRows),
            GraphicsUnit.Pixel);
        g.InterpolationMode = prevInterp;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        // 绿框 = 当前选区画面在长图中的位置（§1.5）
        var st = g.Save();
        g.SetClip(inner);
        using (var green = new Pen(Color.LimeGreen, 2f))
        {
            int gy = inner.Y + (int)((boxTop - srcTop) * zoom);
            int gh = Math.Max(3, (int)(frameH * zoom));
            g.DrawRectangle(green, inner.X + 1, gy, inner.Width - 2, gh);
        }
        g.Restore(st);

        DrawStatus(g, pad);
    }

    private void DrawStatus(Graphics g, int pad)
    {
        var rect = new Rectangle(pad, Height - S(24), Width - pad * 2, S(22));
        TextRenderer.DrawText(g, _session.LongStatusText(), _font, rect,
            _session.LongStatusColor(),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        _font.Dispose();
        _titleFont.Dispose();
    }
}
