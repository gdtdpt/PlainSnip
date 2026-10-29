using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PlainSnip;

/// <summary>
/// 贴图窗（§5.7）：置顶悬浮窗，LWA_ALPHA 整窗透明度，物理像素恒定（跨屏不重算 DPI）。
/// 三态：可交互 / 鼠标穿透 / 已关闭；拖动·缩放·调透明度是瞬态操作。
/// </summary>
public sealed class PinWindow : Form
{
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int GWL_EXSTYLE = -20;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint LWA_ALPHA = 0x02;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint colorKey, byte alpha, uint flags);

    private readonly Bitmap _source;          // 原始像素图（贴图窗拥有所有权）
    private readonly Rectangle _target;       // 首次显示目标矩形（物理像素）
    private Bitmap _scaled;                   // 当前缩放缓存
    private Size _windowSize;
    private float _scale;
    private int _alphaPercent = 100;
    private bool _passthrough;
    private bool _hover;
    private bool _dragging;
    private Point _dragOffset;
    private Font? _badgeFont;
    private ContextMenuStrip? _menu;
    private string? _toast;
    private int _toastUntil;
    private System.Windows.Forms.Timer? _toastTimer;

    public float ZoomScale => _scale;
    public bool Passthrough => _passthrough;
    public Size ImageSize => _source.Size;
    public int OpacityPercent => _alphaPercent;

    /// <summary>--pin 冒烟：始终显示边框与角标（否则需悬停才出现）。</summary>
    public bool DebugChrome { get; init; }

    public event Action<PinWindow>? PinClosed;

    public PinWindow(Bitmap source, float scale, Rectangle bounds)
    {
        _source = source;
        _scale = PinGeometry.ClampScale(scale);
        _windowSize = PinGeometry.ScaledSize(source.Size, _scale);
        _target = new Rectangle(bounds.X, bounds.Y, _windowSize.Width, _windowSize.Height);
        _scaled = BuildScaled(_windowSize);

        AutoScaleMode = AutoScaleMode.None;      // 尺寸/位置全部物理像素，自管缩放
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Text = "PlainSnip 贴图";
        BackColor = Color.FromArgb(24, 24, 26);
        MinimumSize = Size.Empty;
        Bounds = _target;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_LAYERED;
            if (_passthrough) cp.ExStyle |= WS_EX_TRANSPARENT;
            return cp;
        }
    }

    /// <summary>创建贴图不抢焦点（否则会把用户正在打字的窗口焦点顶掉）。</summary>
    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyAlpha();
        SetBoundsNative(_target);
    }

    // ---- 绘制 ----

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        if (_scaled.Size != _windowSize)
        {
            _scaled.Dispose();
            _scaled = BuildScaled(_windowSize);
        }

        g.InterpolationMode = InterpolationMode.NearestNeighbor;   // 缓存图 1:1 贴，不再缩放
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImageUnscaled(_scaled, 0, 0);

        if (_hover || DebugChrome || _passthrough || _toast != null)
            DrawChrome(g);
    }

    private void DrawChrome(Graphics g)
    {
        float k = DeviceDpi / 96f;
        using var pen = new Pen(Color.FromArgb(_passthrough ? 190 : 235, 90, 160, 240), Math.Max(1f, k));
        if (_passthrough) pen.DashStyle = DashStyle.Dash;
        g.DrawRectangle(pen, 0, 0, _windowSize.Width - 1, _windowSize.Height - 1);

        string text = _toast ?? BuildBadge();
        _badgeFont ??= new Font("Microsoft YaHei UI", 9f * k);
        var size = TextRenderer.MeasureText(text, _badgeFont);
        int padX = (int)(8 * k), padY = (int)(4 * k);
        var box = new Rectangle(0, 0, size.Width + padX * 2, size.Height + padY * 2);
        if (box.Width > _windowSize.Width || box.Height > _windowSize.Height) return;

        using var bg = new SolidBrush(Color.FromArgb(225, 24, 24, 26));
        g.FillRectangle(bg, box);
        TextRenderer.DrawText(g, text, _badgeFont, box, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private string BuildBadge()
    {
        string s = $"{_windowSize.Width} × {_windowSize.Height}";
        if (Math.Abs(_scale - 1f) > 0.005f) s += $"  {_scale * 100:0}%";
        if (_alphaPercent < 100) s += $"  不透明度 {_alphaPercent}%";
        return s;
    }

    private Bitmap BuildScaled(Size size)
    {
        var bmp = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        if (size.Width >= _source.Width && size.Height >= _source.Height)
        {
            g.InterpolationMode = InterpolationMode.NearestNeighbor;   // 放大保持像素清晰
            g.PixelOffsetMode = PixelOffsetMode.Half;
        }
        else
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic; // 缩小抗锯齿
            g.CompositingQuality = CompositingQuality.HighSpeed;
        }
        g.DrawImage(_source, new Rectangle(0, 0, bmp.Width, bmp.Height),
            new Rectangle(0, 0, _source.Width, _source.Height), GraphicsUnit.Pixel);
        return bmp;
    }

    // ---- 几何 ----

    private Rectangle CurrentBounds()
    {
        if (IsHandleCreated && GetWindowRect(Handle, out var r))
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        return _target;
    }

    private void SetBoundsNative(Rectangle r)
    {
        _windowSize = r.Size;
        if (!IsHandleCreated)
        {
            Bounds = r;
            return;
        }
        SetWindowPos(Handle, IntPtr.Zero, r.X, r.Y, r.Width, r.Height, SWP_NOZORDER | SWP_NOACTIVATE);
        Invalidate();
    }

    /// <summary>缩放到指定比例；anchor 为锚点（物理屏幕坐标），锚点下的像素保持不动。</summary>
    public void SetScale(float scale, Point? anchor = null)
    {
        float next = PinGeometry.ClampScale(scale);
        if (Math.Abs(next - _scale) < 0.0005f) return;
        var cur = CurrentBounds();
        var pt = anchor ?? new Point(cur.X + cur.Width / 2, cur.Y + cur.Height / 2);
        var target = PinGeometry.ZoomAround(cur, _source.Size, next, pt);
        _scale = next;
        SetBoundsNative(target);
    }

    public void SetOpacityPercent(int percent)
    {
        _alphaPercent = PinGeometry.ClampOpacityPercent(percent);
        ApplyAlpha();
        Invalidate();
    }

    private void ApplyAlpha()
    {
        if (!IsHandleCreated) return;
        SetLayeredWindowAttributes(Handle, 0, PinGeometry.ToAlphaByte(_alphaPercent), LWA_ALPHA);
    }

    public void SetPassthrough(bool on)
    {
        if (_passthrough == on) return;
        _passthrough = on;
        if (IsHandleCreated)
        {
            int ex = GetWindowLong(Handle, GWL_EXSTYLE);
            ex = on ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
            SetWindowLong(Handle, GWL_EXSTYLE, ex);
        }
        _hover = false;
        Invalidate();
    }

    /// <summary>托盘点选贴图时置前（穿透态同样可置前）。</summary>
    public void BringToFrontTop()
    {
        if (!IsHandleCreated) return;
        var r = CurrentBounds();
        SetWindowPos(Handle, IntPtr.Zero, r.X, r.Y, r.Width, r.Height, SWP_NOACTIVATE);
        Activate();
    }

    private void ShowToast(string message)
    {
        _toast = message;
        _toastUntil = Environment.TickCount + 1200;
        _toastTimer ??= CreateToastTimer();
        _toastTimer.Start();
        Invalidate();
    }

    private System.Windows.Forms.Timer CreateToastTimer()
    {
        var t = new System.Windows.Forms.Timer { Interval = 200 };
        t.Tick += (_, _) =>
        {
            if (Environment.TickCount < _toastUntil) { Invalidate(); return; }
            t.Stop();
            _toast = null;
            Invalidate();
        };
        return t;
    }

    // ---- 动作 ----

    public void CopyToClipboard()
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                Clipboard.SetImage(_source);
                ShowToast("已复制");
                return;
            }
            catch (ExternalException)
            {
                Thread.Sleep(40);
            }
        }
        ShowToast("复制失败：剪贴板被占用");
    }

    public void SaveAs()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "PNG 图片|*.png|JPEG 图片|*.jpg|BMP 图片|*.bmp",
            FileName = $"PlainSnip-Pin-{DateTime.Now:yyyyMMdd-HHmmss}.png",
        };
        bool top = TopMost;
        TopMost = false;
        try
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            string ext = Path.GetExtension(dlg.FileName);
            var fmt = ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ? ImageFormat.Jpeg
                    : ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ? ImageFormat.Bmp
                    : ImageFormat.Png;
            _source.Save(dlg.FileName, fmt);
            ShowToast("已保存");
        }
        catch (Exception ex) when (ex is ExternalException or IOException or UnauthorizedAccessException)
        {
            ShowToast("保存失败");
        }
        finally
        {
            if (!IsDisposed) TopMost = top;
        }
    }

    // ---- 输入 ----

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        if (_passthrough) return;
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_passthrough) return;

        if (e.Button == MouseButtons.Right)
        {
            ShowMenu(e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        var cursor = Cursor.Position;
        _dragging = true;
        _dragOffset = new Point(cursor.X - CurrentBounds().X, cursor.Y - CurrentBounds().Y);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || _passthrough) return;
        var cursor = Cursor.Position;
        var cur = CurrentBounds();
        SetBoundsNative(new Rectangle(cursor.X - _dragOffset.X, cursor.Y - _dragOffset.Y, cur.Width, cur.Height));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_passthrough) return;

        if ((ModifierKeys & Keys.Control) != 0)
        {
            SetOpacityPercent(_alphaPercent + (e.Delta > 0 ? 10 : -10));
            ShowToast($"不透明度 {_alphaPercent}%");
        }
        else
        {
            float factor = e.Delta > 0 ? 1.1f : 1f / 1.1f;
            SetScale(_scale * factor, Cursor.Position);
            ShowToast($"{_scale * 100:0}%");
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) { Close(); return; }
        if (e.Control && e.KeyCode == Keys.C) { CopyToClipboard(); return; }
        if (e.Control && e.KeyCode == Keys.S) { SaveAs(); return; }
    }

    protected override void OnDoubleClick(EventArgs e)
    {
        base.OnDoubleClick(e);
        if (!_passthrough) Close();
    }

    private void ShowMenu(Point local)
    {
        _menu?.Dispose();
        _menu = BuildMenu();
        _menu.Show(this, local);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        var copy = new ToolStripMenuItem("复制到剪贴板(&C)") { ShortcutKeyDisplayString = "Ctrl+C" };
        copy.Click += (_, _) => CopyToClipboard();
        menu.Items.Add(copy);

        var save = new ToolStripMenuItem("保存为图片(&S)…") { ShortcutKeyDisplayString = "Ctrl+S" };
        save.Click += (_, _) => SaveAs();
        menu.Items.Add(save);

        menu.Items.Add(new ToolStripSeparator());

        var zoom = new ToolStripMenuItem("缩放(&Z)");
        foreach (var pct in new[] { 25, 50, 100, 200, 400 })
        {
            var item = new ToolStripMenuItem($"{pct}%") { Checked = Math.Abs(_scale * 100 - pct) < 1 };
            int p = pct;
            item.Click += (_, _) => SetScale(p / 100f);
            zoom.DropDownItems.Add(item);
        }
        menu.Items.Add(zoom);

        var opacity = new ToolStripMenuItem("不透明度(&O)");
        foreach (var pct in new[] { 100, 80, 60, 40, 20 })
        {
            var item = new ToolStripMenuItem($"{pct}%") { Checked = _alphaPercent == pct };
            int p = pct;
            item.Click += (_, _) => SetOpacityPercent(p);
            opacity.DropDownItems.Add(item);
        }
        menu.Items.Add(opacity);

        menu.Items.Add(new ToolStripSeparator());

        var through = new ToolStripMenuItem("鼠标穿透(&T)") { Checked = _passthrough };
        through.Click += (_, _) => SetPassthrough(!_passthrough);
        menu.Items.Add(through);

        var close = new ToolStripMenuItem("关闭(&X)") { ShortcutKeyDisplayString = "Esc / 双击" };
        close.Click += (_, _) => Close();
        menu.Items.Add(close);

        return menu;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        _toastTimer?.Stop();
        _toastTimer?.Dispose();
        _menu?.Dispose();
        _badgeFont?.Dispose();
        _scaled.Dispose();
        _source.Dispose();
        PinClosed?.Invoke(this);
    }
}
