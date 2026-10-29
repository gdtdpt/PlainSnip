using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace PlainSnip;

/// <summary>
/// 全屏截图遮罩窗（per-monitor）：持有本屏快照，负责本屏的输入与绘制。
/// 交互由 CaptureSession 统一协调；跨屏拖动依赖 WinForms 按住鼠标时的自动捕获。
/// </summary>
public sealed class CaptureOverlayForm : Form
{
    private readonly CaptureSession _session;
    internal readonly Screen _screen;
    private readonly Bitmap _snap;
    private readonly Font _uiFont;

    internal bool _interactive;

    // 放大镜参数（物理像素）
    private const int MagBox = 144;
    private const int MagSrc = 16;
    private const int MagZoom = MagBox / MagSrc; // 9x

    /// <summary>工具条按钮标识。</summary>
    private enum ToolBtn { Select, Rect, Ellipse, Arrow, Pen, Text, Mosaic, Serial, Color, Width, Pin, Copy, Save, Cancel }

    private readonly List<(ToolBtn Id, Rectangle Rect)> _tbItems = new();
    private Rectangle _toolbarBounds = Rectangle.Empty;   // 悬停隐藏放大镜用
    private ToolBtn? _hoverBtn;
    private TextBox? _textEditor;                         // 文字标注编辑框
    private Point _textVirtual;
    private Point _cursorVirtual;                    // 光标虚拟桌面物理坐标
    private string? _colorCopiedFeedback;            // 取色复制提示
    private readonly System.Windows.Forms.Timer _feedbackTimer = new() { Interval = 1200 };

    public CaptureOverlayForm(CaptureSession session, ScreenCapture.ScreenShot shot, bool interactive)
    {
        _session = session;
        _screen = Screen.AllScreens.First(s => s.Bounds == shot.Bounds);
        _snap = shot.Bitmap;
        _interactive = interactive;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = _screen.Bounds;
        TopMost = true;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        Cursor = Cursors.Cross;
        KeyPreview = true;
        Text = "PlainSnip 截图";
        _uiFont = new Font("Microsoft YaHei UI", 10f); // pt 值由 GDI 随窗口 DPI 换算

        _feedbackTimer.Tick += (_, _) => { _colorCopiedFeedback = null; _feedbackTimer.Stop(); Invalidate(); };
    }

    // ---- 坐标助手 ----

    private Point VirtualFromClient(Point client) => new(_screen.Bounds.X + client.X, _screen.Bounds.Y + client.Y);
    private Point LocalFromVirtual(Point v) => new(v.X - _screen.Bounds.X, v.Y - _screen.Bounds.Y);
    private int S(int logical) => logical * DeviceDpi / 96;

    // ---- 绘制 ----

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.DrawImageUnscaled(_snap, 0, 0);

        var selLocal = _session.SelectionLocal(_screen);
        using var dark = new SolidBrush(Color.FromArgb(105, 0, 0, 0));

        if (selLocal.IsEmpty)
        {
            // Idle/PendingConfirm：悬停候选高亮（区域提亮 + 描边），其余变暗
            var hoverLocal = _session.HoverLocal(_screen);
            bool showHover = !hoverLocal.IsEmpty
                && _session.State is CaptureSession.SelState.Idle or CaptureSession.SelState.PendingConfirm;
            if (showHover)
            {
                var h = hoverLocal;
                g.FillRectangle(dark, new Rectangle(0, 0, ClientSize.Width, Math.Max(0, h.Top)));
                g.FillRectangle(dark, new Rectangle(0, h.Bottom, ClientSize.Width, Math.Max(0, ClientSize.Height - h.Bottom)));
                g.FillRectangle(dark, new Rectangle(0, h.Top, Math.Max(0, h.Left), h.Height));
                g.FillRectangle(dark, new Rectangle(h.Right, h.Top, Math.Max(0, ClientSize.Width - h.Right), h.Height));

                using var glow = new SolidBrush(Color.FromArgb(28, 255, 255, 255));
                g.FillRectangle(glow, h);
                using var accent = new Pen(Color.FromArgb(255, 84, 158, 255), 2f);
                g.DrawRectangle(accent, h.X, h.Y, h.Width - 1, h.Height - 1);

                if (_interactive)
                    DrawSizeLabel(g, h, $"{_session.HoverRect.Width} × {_session.HoverRect.Height}");
            }
            else
            {
                g.FillRectangle(dark, ClientRectangle);
            }
        }
        else
        {
            // 选区外变暗：上下左右四块
            var r = selLocal;
            g.FillRectangle(dark, new Rectangle(0, 0, ClientSize.Width, Math.Max(0, r.Top)));
            g.FillRectangle(dark, new Rectangle(0, r.Bottom, ClientSize.Width, Math.Max(0, ClientSize.Height - r.Bottom)));
            g.FillRectangle(dark, new Rectangle(0, r.Top, Math.Max(0, r.Left), r.Height));
            g.FillRectangle(dark, new Rectangle(r.Right, r.Top, Math.Max(0, ClientSize.Width - r.Right), r.Height));

            // 选区描边
            using var border = new Pen(Color.White, 1f);
            g.DrawRectangle(border, r.X, r.Y, r.Width - 1, r.Height - 1);

            // 标注重放（屏幕显示：裁到选区∩本屏；与输出合成共用唯一渲染入口）
            if (_session.History.Count > 0 || _session.Pending != null)
            {
                var st = g.Save();
                g.SetClip(r);
                g.TranslateTransform(-_screen.Bounds.X, -_screen.Bounds.Y);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                AnnotationRenderer.Draw(g, _session.History, _session.MosaicSample);
                if (_session.Pending != null)
                    AnnotationRenderer.Draw(g, new[] { _session.Pending }, _session.MosaicSample);
                g.Restore(st);
            }

            if (_interactive)
            {
                if (_session.State == CaptureSession.SelState.Selected) DrawHandles(g, r);
                DrawSizeLabel(g, r, $"{_session.Selection.Width} × {_session.Selection.Height}");
                if (_session.State == CaptureSession.SelState.Selected) DrawToolbar(g);
            }
        }

        if (_interactive) DrawMagnifier(g);
    }

    private void DrawSizeLabel(Graphics g, Rectangle selLocal, string text)
    {
        var size = TextRenderer.MeasureText(text, _uiFont);
        int padX = S(8);
        var box = new Size(size.Width + padX * 2, size.Height + S(4));
        Point pos = new(selLocal.X, selLocal.Y - box.Height - S(6));
        if (pos.Y < 0) pos.Y = selLocal.Y + S(6);                 // 贴顶则放选区内
        if (pos.X + box.Width > ClientSize.Width) pos.X = ClientSize.Width - box.Width;

        using var bg = new SolidBrush(Color.FromArgb(210, 20, 20, 20));
        g.FillRectangle(bg, new Rectangle(pos, box));
        TextRenderer.DrawText(g, text, _uiFont, new Rectangle(pos, box), Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private static readonly Cursor?[] _handleCursors =
    {
        Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE,
        Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE,
    };

    /// <summary>8 手柄中心点（位掩码 1=L,2=T,4=R,8=B；角=组合）。</summary>
    public static Point[] HandleCenters(Rectangle r)
    {
        int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        return new Point[]
        {
            new(r.X, r.Y), new(cx, r.Y), new(r.Right, r.Y), new(r.Right, cy),
            new(r.Right, r.Bottom), new(cx, r.Bottom), new(r.X, r.Bottom), new(r.X, cy),
        };
    }

    private static readonly int[] _handleBits = { 3, 2, 6, 4, 12, 8, 9, 1 };

    /// <summary>命中测试：返回手柄索引（0-7），未命中返回 -1。命中半径 S(12)。</summary>
    public int HandleHitTest(Point local)
    {
        if (_session.State != CaptureSession.SelState.Selected) return -1;
        var selLocal = _session.SelectionLocal(_screen);
        if (selLocal.IsEmpty) return -1;
        int radius = S(12);
        var centers = HandleCenters(selLocal);
        for (int i = 0; i < centers.Length; i++)
        {
            if (Math.Abs(local.X - centers[i].X) <= radius && Math.Abs(local.Y - centers[i].Y) <= radius)
                return i;
        }
        return -1;
    }

    private void DrawHandles(Graphics g, Rectangle selLocal)
    {
        int hs = S(9);
        using var fill = new SolidBrush(Color.White);
        using var edge = new Pen(Color.FromArgb(60, 60, 60), 1f);
        foreach (var c in HandleCenters(selLocal))
        {
            var rect = new Rectangle(c.X - hs / 2, c.Y - hs / 2, hs, hs);
            g.FillRectangle(fill, rect);
            g.DrawRectangle(edge, rect);
        }
    }

    private void DrawToolbar(Graphics g)
    {
        var selLocal = _session.SelectionLocal(_screen);
        int btnH = S(38), margin = S(8), padX = S(16), gap = S(2), iconBtn = S(36), sepW = S(10);
        var textBtns = new[] { (ToolBtn.Pin, "贴图"), (ToolBtn.Copy, "复制"), (ToolBtn.Save, "保存"), (ToolBtn.Cancel, "取消") };

        // 总宽：8 工具图标 + 分隔 + 颜色/线宽 2 图标 + 4 文字按钮
        int total = S(8) * 2 + iconBtn * 10 + gap * 13 + sepW;
        foreach (var (_, text) in textBtns) total += TextRenderer.MeasureText(text, _uiFont).Width + padX * 2 + gap;

        var tb = new Rectangle(selLocal.X, selLocal.Bottom + margin, total, btnH);
        if (tb.Bottom > ClientSize.Height) tb.Y = selLocal.Y - margin - btnH;      // 贴底翻转
        tb.X = Math.Clamp(tb.X, 0, Math.Max(0, ClientSize.Width - total));
        _toolbarBounds = tb;

        using var bg = new SolidBrush(Color.FromArgb(225, 28, 28, 30));
        using var hoverBg = new SolidBrush(Color.FromArgb(255, 70, 130, 220));
        using var hoverBgPlain = new SolidBrush(Color.FromArgb(255, 64, 64, 68));
        using var activeBg = new SolidBrush(Color.FromArgb(255, 40, 96, 170));
        g.FillRectangle(bg, tb);

        _tbItems.Clear();
        int x = tb.X + S(8);

        Rectangle AddBtn(ToolBtn id, int w, bool active, bool danger = false)
        {
            var rect = new Rectangle(x, tb.Y, w, btnH);
            _tbItems.Add((id, rect));
            if (active) g.FillRectangle(activeBg, rect);
            else if (_hoverBtn == id) g.FillRectangle(danger ? hoverBgPlain : hoverBg, rect);
            x += w + gap;
            return rect;
        }

        var tools = new[] { (ToolBtn.Select, AnnotationTool.None), (ToolBtn.Rect, AnnotationTool.Rect),
            (ToolBtn.Ellipse, AnnotationTool.Ellipse), (ToolBtn.Arrow, AnnotationTool.Arrow),
            (ToolBtn.Pen, AnnotationTool.Pen), (ToolBtn.Text, AnnotationTool.Text),
            (ToolBtn.Mosaic, AnnotationTool.Mosaic), (ToolBtn.Serial, AnnotationTool.Serial) };
        foreach (var (id, tool) in tools)
            DrawToolIcon(g, id, AddBtn(id, iconBtn, _session.ActiveTool == tool));

        using var sepPen = new Pen(Color.FromArgb(80, 255, 255, 255), 1f);
        g.DrawLine(sepPen, x + sepW / 2, tb.Y + S(8), x + sepW / 2, tb.Bottom - S(8));
        x += sepW;

        DrawToolIcon(g, ToolBtn.Color, AddBtn(ToolBtn.Color, iconBtn, false));
        DrawToolIcon(g, ToolBtn.Width, AddBtn(ToolBtn.Width, iconBtn, false));

        foreach (var (id, text) in textBtns)
        {
            var rect = AddBtn(id, TextRenderer.MeasureText(text, _uiFont).Width + padX * 2, false,
                danger: id == ToolBtn.Cancel);
            TextRenderer.DrawText(g, text, _uiFont, rect, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    /// <summary>工具条图标（GDI 自绘，全部按 rect 居中）。</summary>
    private void DrawToolIcon(Graphics g, ToolBtn id, Rectangle r)
    {
        using var pen = new Pen(Color.White, 1.6f);
        int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        switch (id)
        {
            case ToolBtn.Select:
            {
                using var fill = new SolidBrush(Color.White);
                g.FillPolygon(fill, new[]
                {
                    new Point(cx - 4, cy - 7), new Point(cx - 4, cy + 7), new Point(cx - 1, cy + 3),
                    new Point(cx + 2, cy + 7), new Point(cx + 4, cy + 5), new Point(cx + 1, cy + 2),
                });
                break;
            }
            case ToolBtn.Rect:
                g.DrawRectangle(pen, cx - 8, cy - 6, 16, 12);
                break;
            case ToolBtn.Ellipse:
                g.DrawEllipse(pen, cx - 8, cy - 6, 16, 12);
                break;
            case ToolBtn.Arrow:
            {
                g.DrawLine(pen, cx - 8, cy + 6, cx + 2, cy - 4);
                using var fill = new SolidBrush(Color.White);
                g.FillPolygon(fill, new[]
                {
                    new Point(cx + 8, cy - 8), new Point(cx - 1, cy - 5), new Point(cx + 5, cy + 1),
                });
                break;
            }
            case ToolBtn.Pen:
            {
                using var thick = new Pen(Color.White, 3f);
                g.DrawLine(thick, cx - 5, cy + 5, cx + 5, cy - 5);
                g.FillRectangle(Brushes.White, cx - 8, cy + 5, 4, 4);
                break;
            }
            case ToolBtn.Text:
                TextRenderer.DrawText(g, "文", _uiFont, r, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                break;
            case ToolBtn.Mosaic:
            {
                int s2 = S(7), o = S(2);
                using var dim = new SolidBrush(Color.FromArgb(150, 255, 255, 255));
                g.FillRectangle(Brushes.White, cx - s2 - o, cy - s2 - o, s2, s2);
                g.FillRectangle(dim, cx + o, cy - s2 - o, s2, s2);
                g.FillRectangle(dim, cx - s2 - o, cy + o, s2, s2);
                g.FillRectangle(Brushes.White, cx + o, cy + o, s2, s2);
                break;
            }
            case ToolBtn.Serial:
            {
                int d = S(18);
                using var fill = new SolidBrush(Color.FromArgb(255, 0, 120, 215));
                g.FillEllipse(fill, cx - d / 2, cy - d / 2, d, d);
                TextRenderer.DrawText(g, "1", _uiFont, r, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                break;
            }
            case ToolBtn.Color:
            {
                int s2 = S(14);
                using var fill = new SolidBrush(_session.StrokeColor);
                var rect = new Rectangle(cx - s2 / 2, cy - s2 / 2, s2, s2);
                g.FillRectangle(fill, rect);
                g.DrawRectangle(pen, rect);
                break;
            }
            case ToolBtn.Width:
            {
                int w2 = S(18);
                using var thick = new Pen(Color.White, Math.Clamp(_session.StrokeWidth, 2, 10));
                g.DrawLine(thick, cx - w2 / 2, cy, cx + w2 / 2, cy);
                break;
            }
        }
    }

    private ToolBtn? HitToolbar(Point local)
    {
        foreach (var (id, rect) in _tbItems)
            if (rect.Contains(local)) return id;
        return null;
    }

    private void HandleToolbarClick(ToolBtn id, MouseEventArgs e)
    {
        switch (id)
        {
            case ToolBtn.Select: _session.SetTool(AnnotationTool.None); break;
            case ToolBtn.Rect: _session.SetTool(AnnotationTool.Rect); break;
            case ToolBtn.Ellipse: _session.SetTool(AnnotationTool.Ellipse); break;
            case ToolBtn.Arrow: _session.SetTool(AnnotationTool.Arrow); break;
            case ToolBtn.Pen: _session.SetTool(AnnotationTool.Pen); break;
            case ToolBtn.Text: _session.SetTool(AnnotationTool.Text); break;
            case ToolBtn.Mosaic: _session.SetTool(AnnotationTool.Mosaic); break;
            case ToolBtn.Serial: _session.SetTool(AnnotationTool.Serial); break;
            case ToolBtn.Color: ShowColorMenu(e.Location); break;
            case ToolBtn.Width: ShowWidthMenu(e.Location); break;
            case ToolBtn.Pin: _session.PinSelection(); break;
            case ToolBtn.Copy: _session.CopySelection(); break;
            case ToolBtn.Save: _session.SaveSelection(); break;
            case ToolBtn.Cancel: _session.Cancel(); break;
        }
    }

    private void ShowColorMenu(Point local)
    {
        var menu = new ContextMenuStrip();
        (string Name, Color C)[] colors =
        {
            ("红色", Color.Red), ("橙色", Color.Orange), ("绿色", Color.LimeGreen),
            ("蓝色", Color.DodgerBlue), ("白色", Color.White), ("黑色", Color.Black),
        };
        foreach (var (name, c) in colors)
        {
            double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;
            var item = new ToolStripMenuItem(name) { BackColor = c, ForeColor = lum > 0.5 ? Color.Black : Color.White };
            var cc = c;
            item.Click += (_, _) => _session.SetStrokeColor(cc);
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) => menu.Dispose();
        menu.Show(this, local);
    }

    private void ShowWidthMenu(Point local)
    {
        var menu = new ContextMenuStrip();
        foreach (var (name, logical) in new[] { ("细", 2), ("中", 4), ("粗", 8) })
        {
            var item = new ToolStripMenuItem($"{name}（{logical}px）");
            int px = S(logical);
            item.Click += (_, _) => _session.SetStrokeWidth(px);
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) => menu.Dispose();
        menu.Show(this, local);
    }

    // ---- 文字标注编辑框 ----

    private void OpenTextEditor(Point v, Point local)
    {
        CloseTextEditor(commit: false);
        int fontPx = Math.Max(12, _session.TextSize);
        _textEditor = new TextBox
        {
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(245, 245, 245),
            ForeColor = _session.StrokeColor.GetBrightness() > 0.35f ? Color.Black : _session.StrokeColor,
            Font = new Font("Microsoft YaHei UI", fontPx, GraphicsUnit.Pixel),
            Location = new Point(local.X, local.Y - fontPx / 2),
            Width = S(220),
        };
        _textEditor.Height = fontPx + S(12);
        _textVirtual = v;
        _textEditor.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; CloseTextEditor(commit: true); }
            else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; CloseTextEditor(commit: false); }
        };
        _textEditor.LostFocus += (s, e) => CloseTextEditor(commit: true);
        Controls.Add(_textEditor);
        _textEditor.BringToFront();
        _textEditor.Focus();
        Invalidate();
    }

    private void CloseTextEditor(bool commit)
    {
        if (_textEditor == null) return;
        var editor = _textEditor;
        _textEditor = null;   // 先清引用，防 LostFocus→Dispose 重入
        string? text = commit ? editor.Text.Trim() : null;
        editor.Dispose();
        if (!string.IsNullOrEmpty(text)) _session.CommitText(_textVirtual, text);
        Invalidate();
    }

    private void DrawMagnifier(Graphics g)
    {
        var cur = LocalFromVirtual(_cursorVirtual);
        // 绘制标注 / 文字编辑中不画放大镜（不遮挡笔画）
        if (_session.State == CaptureSession.SelState.Annotating || _textEditor != null) return;
        // 光标不在本屏内则不画
        if (!ClientRectangle.Contains(cur)) return;
        // 光标悬停工具条时隐藏放大镜
        if (_toolbarBounds.Contains(cur)) return;

        int magX = cur.X + S(24), magY = cur.Y + S(24);
        if (magX + MagBox > ClientSize.Width) magX = cur.X - S(24) - MagBox;      // 右缘翻转
        if (magY + MagBox + S(24) > ClientSize.Height) magY = cur.Y - S(24) - MagBox - S(24);
        magX = Math.Clamp(magX, 0, Math.Max(0, ClientSize.Width - MagBox));
        magY = Math.Clamp(magY, 0, Math.Max(0, ClientSize.Height - MagBox - S(24)));

        // 源区（快照本地坐标），越界则裁掉并等比收缩目标区
        int srcX = Math.Clamp(cur.X - MagSrc / 2, 0, Math.Max(0, _snap.Width - MagSrc));
        int srcY = Math.Clamp(cur.Y - MagSrc / 2, 0, Math.Max(0, _snap.Height - MagSrc));
        int srcW = Math.Min(MagSrc, _snap.Width - srcX);
        int srcH = Math.Min(MagSrc, _snap.Height - srcY);
        var srcRect = new Rectangle(srcX, srcY, srcW, srcH);
        var destRect = new Rectangle(magX, magY, srcW * MagZoom, srcH * MagZoom);

        var oldInterp = g.InterpolationMode;
        var oldOffset = g.PixelOffsetMode;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_snap, destRect, srcRect, GraphicsUnit.Pixel);
        g.InterpolationMode = oldInterp;
        g.PixelOffsetMode = oldOffset;

        using var border = new Pen(Color.White, 1f);
        g.DrawRectangle(border, destRect.X, destRect.Y, destRect.Width - 1, destRect.Height - 1);

        // 中心十字
        int cx = destRect.X + (cur.X - srcX) * MagZoom, cy = destRect.Y + (cur.Y - srcY) * MagZoom;
        using var cross = new Pen(Color.FromArgb(160, 255, 60, 60), 1f);
        g.DrawLine(cross, cx - S(12), cy, cx + S(12), cy);
        g.DrawLine(cross, cx, cy - S(12), cx, cy + S(12));

        // 取色信息面板：坐标 / 色块+值 / 快捷键提示（四行，行宽按最长内容计算）
        if (cur.X >= 0 && cur.X < _snap.Width && cur.Y >= 0 && cur.Y < _snap.Height)
        {
            Color px = _snap.GetPixel(Math.Clamp(cur.X, 0, _snap.Width - 1), Math.Clamp(cur.Y, 0, _snap.Height - 1));
            string value = _colorFormatHex
                ? $"#{px.R:X2}{px.G:X2}{px.B:X2}"
                : $"{px.R}, {px.G}, {px.B}";
            string coord = $"({cur.X}, {cur.Y})";
            string hintCopy = "按 C 复制颜色值";
            string hintSwitch = "按 Shift 切换 HEX/RGB";

            int pad = S(10), lineH = S(20), swatch = S(12), gap = S(6);
            int wCoord = TextRenderer.MeasureText(coord, _uiFont).Width;
            int wValue = TextRenderer.MeasureText(value, _uiFont).Width + swatch + gap;
            int wH1 = TextRenderer.MeasureText(hintCopy, _uiFont).Width;
            int wH2 = TextRenderer.MeasureText(hintSwitch, _uiFont).Width;
            var panel = new Rectangle(0, 0, Math.Max(Math.Max(wCoord, wValue), Math.Max(wH1, wH2)) + pad * 2,
                pad * 2 + lineH * 4 + S(2));

            // 位置：默认贴放大镜下方，放不下则翻到上方；水平随放大镜夹取
            panel.X = Math.Clamp(magX, 0, Math.Max(0, ClientSize.Width - panel.Width));
            panel.Y = magY + MagBox + S(6);
            if (panel.Bottom > ClientSize.Height) panel.Y = magY - S(6) - panel.Height;

            using var panelBg = new SolidBrush(Color.FromArgb(215, 15, 15, 15));
            g.FillRectangle(panelBg, panel);
            using var swatchPen = new Pen(Color.FromArgb(140, 255, 255, 255), 1f);

            int tx = panel.X + pad;
            int ty = panel.Y + pad;
            int textW = panel.Right - pad - tx;
            TextRenderer.DrawText(g, coord, _uiFont, new Rectangle(tx, ty, textW, lineH),
                Color.FromArgb(190, 255, 255, 255), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            ty += lineH;
            var swRect = new Rectangle(tx, ty + (lineH - swatch) / 2, swatch, swatch);
            using var swatchBrush = new SolidBrush(px);
            g.FillRectangle(swatchBrush, swRect);
            g.DrawRectangle(swatchPen, swRect);
            TextRenderer.DrawText(g, value, _uiFont, new Rectangle(tx + swatch + gap, ty, textW - swatch - gap, lineH),
                Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            ty += lineH;
            TextRenderer.DrawText(g, hintCopy, _uiFont, new Rectangle(tx, ty, textW, lineH),
                Color.FromArgb(225, 255, 255, 255), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            ty += lineH;
            TextRenderer.DrawText(g, hintSwitch, _uiFont, new Rectangle(tx, ty, textW, lineH),
                Color.FromArgb(225, 255, 255, 255), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            if (_colorCopiedFeedback != null)
                TextRenderer.DrawText(g, _colorCopiedFeedback, _uiFont,
                    new Point(panel.X, panel.Bottom + S(4)), Color.FromArgb(190, 255, 230, 120));
        }
    }

    // ---- 输入 ----

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!_interactive) { _session.SwitchInteractive(this); }

        var v = VirtualFromClient(e.Location);
        if (e.Button == MouseButtons.Right) { _session.Cancel(); return; }
        if (e.Button != MouseButtons.Left) return;

        // 工具条按钮
        if (_session.State == CaptureSession.SelState.Selected)
        {
            var btn = HitToolbar(e.Location);
            if (btn != null) { HandleToolbarClick(btn.Value, e); return; }
        }

        // 标注工具（激活时优先于手柄/移动；点选区内即开始）
        if (_session.State == CaptureSession.SelState.Selected && _session.ActiveTool != AnnotationTool.None
            && _session.SelectionLocal(_screen).Contains(e.Location))
        {
            if (_textEditor != null) { CloseTextEditor(commit: true); return; }   // 先提交正在编辑的文字
            switch (_session.ActiveTool)
            {
                case AnnotationTool.Text: OpenTextEditor(v, e.Location); return;
                case AnnotationTool.Serial: _session.CommitSerial(v); return;
                default: _session.BeginAnnotate(_session.ActiveTool, v); return;
            }
        }

        int handle = HandleHitTest(e.Location);
        if (handle >= 0)
        {
            _session.BeginResize(_handleBits[handle], v);
        }
        else if (_session.State == CaptureSession.SelState.Selected && _session.SelectionLocal(_screen).Contains(e.Location))
        {
            _session.BeginMove(v);
        }
        else if (_session.State == CaptureSession.SelState.Idle)
        {
            // 命中预选 → 待确认（单击确认 / 拖动转手拉）；未命中 → 直接框选
            if (!_session.HoverRect.IsEmpty && _session.HoverRect.Contains(v))
                _session.BeginPendingConfirm(v);
            else
                _session.BeginDrag(v);
        }
        else
        {
            _session.BeginDrag(v);   // 选区外点击 = 重新框选
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var v = VirtualFromClient(e.Location);
        _cursorVirtual = v;

        if (!_interactive)
        {
            _session.SwitchInteractive(this);
            Invalidate();
            return;
        }

        var local = e.Location;

        // 光标形状
        Cursor = _session.State switch
        {
            CaptureSession.SelState.Selected when _toolbarBounds.Contains(local) && HitToolbar(local) != null => Cursors.Hand,
            CaptureSession.SelState.Selected when HandleHitTest(local) >= 0 => _handleCursors[HandleHitTest(local)],
            CaptureSession.SelState.Selected when _session.SelectionLocal(_screen).Contains(local)
                && _session.ActiveTool == AnnotationTool.None => Cursors.SizeAll,
            _ => Cursors.Cross,
        };

        // 工具条悬停高亮
        var hover = HitToolbar(local);
        if (hover != _hoverBtn)
        {
            _hoverBtn = hover;
            Invalidate();
        }

        if (_session.State == CaptureSession.SelState.Annotating)
        {
            _session.UpdateAnnotate(v);
            return;
        }

        if (_session.State == CaptureSession.SelState.PendingConfirm)
        {
            _session.UpdatePendingConfirm(v);   // 超阈值自动转 Dragging
            return;
        }

        if (_session.State is CaptureSession.SelState.Dragging or CaptureSession.SelState.Moving or CaptureSession.SelState.Resizing)
        {
            _session.UpdateDrag(v);
            return;
        }

        // Idle：50ms 节流悬停检测 + 放大镜跟随
        // 无符号差值：初值 int.MinValue 时有符号差会溢出为负、检测永不触发（真踩过），且可免疫 TickCount 回绕
        int now = Environment.TickCount;
        if (_session.State == CaptureSession.SelState.Idle && (uint)(now - _lastHoverTick) >= 50)
        {
            _lastHoverTick = now;
            _session.UpdateHover(v);
        }
        Invalidate();
    }

    private int _lastHoverTick;

    /// <summary>把本屏 DPI 换算的拖动阈值与标注默认尺寸交给会话（会话 Begin 后调用一次）。</summary>
    internal void SyncDpiToSession()
    {
        _session.SetDragThreshold(S(4));
        _session.SetUiScale(S(3), S(16), S(24), S(8));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left)
        {
            if (_session.State == CaptureSession.SelState.Annotating)
                _session.EndAnnotate();
            else if (_session.State == CaptureSession.SelState.PendingConfirm)
                _session.ConfirmHover();
            else
                _session.EndInteraction();
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        // 标注工具激活时双击是绘制动作，不复制
        if (_session.ActiveTool != AnnotationTool.None) return;
        // WinForms 双击序列：Down/Up/Down/DoubleClick/Up——第二次 Down 已把状态切成 Moving，
        // 此处先收敛回 Selected 再复制，否则条件永不成立（双击复制失效）。
        if (e.Button == MouseButtons.Left
            && _session.State is CaptureSession.SelState.Selected or CaptureSession.SelState.Moving
            && _session.SelectionLocal(_screen).Contains(e.Location))
        {
            if (_session.State == CaptureSession.SelState.Moving) _session.EndInteraction();
            _session.CopySelection();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_textEditor != null) return;   // 文字编辑中，按键归编辑框

        switch (e.KeyCode)
        {
            case Keys.Escape:
                // 绘制中 → 取消当前笔；否则取消整个截图（M1a 肌肉记忆）
                if (_session.Pending != null) { _session.CancelPendingAnnotation(); e.Handled = true; }
                else _session.Cancel();
                break;
            case Keys.Enter:
                _session.CopySelection();
                break;
            case Keys.Z when e.Control && e.Shift:
                _session.Redo();
                e.Handled = true;
                break;
            case Keys.Z when e.Control:
                _session.Undo();
                e.Handled = true;
                break;
            case Keys.Y when e.Control:
                _session.Redo();
                e.Handled = true;
                break;
            case Keys.C when _session.State is CaptureSession.SelState.Idle or CaptureSession.SelState.Selected:
                // 取色复制（Snipaste 键位）；点击放大镜不可达——放大镜永远偏移光标 24px 跟随
                CopyColorAtCursor();
                e.Handled = true;
                break;
            case Keys.Up or Keys.Down or Keys.Left or Keys.Right when _session.State == CaptureSession.SelState.Selected && !_session.Selection.IsEmpty:
                int step = e.Shift ? 10 : 1;
                var d = e.KeyCode switch
                {
                    Keys.Up => new Size(0, -step),
                    Keys.Down => new Size(0, step),
                    Keys.Left => new Size(-step, 0),
                    _ => new Size(step, 0),
                };
                // 方向键微调：借道 Moving 的钳位逻辑
                var orig = _session.Selection;
                _session.BeginMove(orig.Location);
                _session.UpdateDrag(orig.Location + d);
                _session.EndInteraction();
                if (e.Shift) _shiftSeenWithOtherKey = true;   // 抑制松开 Shift 时翻转取色格式
                e.Handled = true;
                break;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        // Shift 切换取色格式放 KeyUp：KeyDown 会因按住自动重复而连续翻转
        if (e.KeyCode is Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey
            && _session.State is CaptureSession.SelState.Idle or CaptureSession.SelState.Selected)
        {
            if (_shiftSeenWithOtherKey)
            {
                _shiftSeenWithOtherKey = false;   // 组合键（如 Shift+方向键微调）后不翻转
            }
            else
            {
                _colorFormatHex = !_colorFormatHex;
                Invalidate();
            }
        }
    }

    private bool _shiftSeenWithOtherKey;

    private bool _colorFormatHex = true;

    private void CopyColorAtCursor()
    {
        var cur = LocalFromVirtual(_cursorVirtual);
        if (cur.X < 0 || cur.X >= _snap.Width || cur.Y < 0 || cur.Y >= _snap.Height) return;
        Color px = _snap.GetPixel(cur.X, cur.Y);
        string text = _colorFormatHex
            ? $"#{px.R:X2}{px.G:X2}{px.B:X2}"
            : $"{px.R}, {px.G}, {px.B}";
        try { Clipboard.SetText(text); } catch (ExternalException) { }
        _colorCopiedFeedback = $"已复制 {text}";
        _feedbackTimer.Start();
        Invalidate();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // 阻止 Tab/方向键触发焦点移动干扰微调
        if (keyData is Keys.Tab or Keys.Up or Keys.Down or Keys.Left or Keys.Right)
            return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        CloseTextEditor(commit: false);
        _uiFont.Dispose();
        _feedbackTimer.Stop();
        _feedbackTimer.Dispose();
    }
}
