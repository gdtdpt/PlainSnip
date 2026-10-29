using System.Runtime.InteropServices;

namespace PlainSnip;

/// <summary>
/// 截图会话：F1 → 冻结快照 → 各屏遮罩窗 → 框选/微调 → 复制/保存/取消。
/// 选区以虚拟桌面物理坐标在此统一记录；遮罩窗只负责各自屏幕的输入与绘制。
/// 设计为「可复用组件」：M4 滚动截图的先选区入口复用同一交互（回调确认结果）。
/// </summary>
public sealed class CaptureSession : IDisposable
{
    public enum SelState { Idle, PendingConfirm, Dragging, Resizing, Moving, Selected, Annotating }

    private List<ScreenCapture.ScreenShot> _shots = new();
    private readonly List<CaptureOverlayForm> _overlays = new();
    private CaptureOverlayForm? _interactive;   // 当前接收鼠标输入的遮罩窗
    private static CaptureSession? _current;
    private Point _confirmAnchor;               // PendingConfirm 按下锚点
    private int _dragThreshold = 4;             // 拖动判定阈值（物理像素，遮罩窗按 DPI 设定）

    // ---- 标注（M3）----
    private readonly AnnotationHistory _history = new();
    private Annotation? _pending;               // 绘制中的记录（Annotating 态）
    private AnnotationTool _activeTool = AnnotationTool.None;
    private Color _strokeColor = Color.Red;
    private int _strokeWidth = 6;               // 物理像素（遮罩窗 SyncDpi 时按本屏 DPI 修正）
    private int _textSize = 24;                 // 文字字号（物理像素）
    private int _serialDiameter = 36;           // 序号徽章直径（物理像素）
    private int _mosaicBlock = 16;              // 马赛克块（物理像素）
    private Bitmap? _composeCache;              // 输出合成缓存
    private int _cacheVersion = -1;
    private Rectangle _cacheSel;

    public SelState State { get; private set; } = SelState.Idle;
    public Rectangle Selection { get; private set; }    // 虚拟桌面物理坐标
    public Rectangle HoverRect { get; private set; }    // 悬停预选候选窗口（虚拟物理坐标，Idle/PendingConfirm 态）
    public int ResizeHandle { get; private set; } = -1; // 0-7，见 CaptureOverlayForm.HandleHitTest
    public Point DragAnchor { get; private set; }       // 拖动起点（虚拟坐标）
    public Rectangle DragOrigSel { get; private set; }  // 拖动开始时的选区
    public bool DebugMode { get; init; }                // --shot 冒烟：自动框选 + 定时退出
    public bool DebugHover { get; init; }               // --hover 冒烟：按光标位置预选 + 定时退出

    // ---- 标注状态暴露（M3）----
    public AnnotationHistory History => _history;
    public Annotation? Pending => _pending;
    public AnnotationTool ActiveTool => _activeTool;
    public Color StrokeColor => _strokeColor;
    public int StrokeWidth => _strokeWidth;
    public int TextSize => _textSize;
    public int SerialDiameter => _serialDiameter;

    public IReadOnlyList<ScreenCapture.ScreenShot> Shots => _shots;
    public CaptureOverlayForm? Interactive => _interactive;

    /// <summary>启动会话。已有会话时返回 null（F1 重复按忽略）。</summary>
    public static CaptureSession? Start(bool debug = false, bool debugHover = false)
    {
        if (_current != null) return null;
        _current = new CaptureSession { DebugMode = debug, DebugHover = debugHover };
        _current.Begin();
        return _current;
    }

    /// <summary>主交互遮罩窗（--shot 冒烟模式用作消息泵主窗体）。</summary>
    public CaptureOverlayForm? InteractiveForm => _interactive;

    private void Begin()
    {
        _shots = ScreenCapture.CaptureAllScreens();
        var cursorScreen = Screen.FromPoint(Cursor.Position);
        foreach (var shot in _shots)
        {
            var screen = Screen.AllScreens.First(s => s.Bounds == shot.Bounds);
            bool interactive = screen.Bounds == cursorScreen.Bounds;
            var overlay = new CaptureOverlayForm(this, shot, interactive);
            _overlays.Add(overlay);
            if (interactive) _interactive = overlay;
        }
        foreach (var overlay in _overlays) overlay.Show();
        _interactive?.Activate();
        _interactive?.SyncDpiToSession();

        if (DebugMode || DebugHover)
        {
            if (DebugHover)
            {
                // 冒烟：按当前光标位置做一次悬停预选，6 秒后自动退出
                UpdateHover(Cursor.Position);
            }
            else
            {
                // 冒烟：自动框选交互屏中部 40% 区域
                var b = _interactive!._screen.Bounds;
                Selection = new Rectangle(b.X + b.Width * 3 / 10, b.Y + b.Height * 3 / 10,
                    b.Width * 2 / 5, b.Height * 2 / 5);
                State = SelState.Selected;

                // 预置演示标注（M3 渲染冒烟）：矩形 + 序号 + 文字
                var r = new Rectangle(Selection.X + Selection.Width * 3 / 10, Selection.Y + Selection.Height * 3 / 10,
                    Selection.Width * 2 / 5, Selection.Height * 2 / 5);
                _history.Push(new Annotation(AnnotationTool.Rect,
                    new[] { r.Location, new Point(r.Right, r.Bottom) }, Color.Red, 6));
                _history.Push(new Annotation(AnnotationTool.Serial,
                    new[] { new Point(Selection.X + Selection.Width * 8 / 10, Selection.Y + Selection.Height * 2 / 10) },
                    Color.FromArgb(255, 0, 120, 215), Math.Max(24, Selection.Height / 18), Serial: 1));
                _history.Push(new Annotation(AnnotationTool.Text,
                    new[] { new Point(Selection.X + Selection.Width / 10, Selection.Y + Selection.Height * 8 / 10) },
                    Color.FromArgb(255, 255, 200, 0), Math.Max(18, Selection.Height / 30), Text: "M3 标注演示"));
                RefreshAll();
            }
            var t = new System.Windows.Forms.Timer { Interval = 6000 };
            t.Tick += (_, _) => { t.Stop(); Cancel(); };
            t.Start();
        }
        else
        {
            UpdateHover(Cursor.Position);   // F1 进入即按光标位置预选一次（§5.6）
        }
    }

    public void SwitchInteractive(CaptureOverlayForm form)
    {
        if (_interactive == form) return;
        _interactive = form;
        foreach (var o in _overlays) { o._interactive = o == form; o.Invalidate(); }
        form.Activate();
    }

    // ---- 选区操作（由遮罩窗输入事件驱动）----

    /// <summary>悬停检测（遮罩窗 50ms 节流后调用）。仅 Idle 态生效。</summary>
    public void UpdateHover(Point virtualPt)
    {
        if (State != SelState.Idle) return;
        var c = WindowDetector.DetectAt(virtualPt);
        var rect = c.Hwnd != IntPtr.Zero ? c.Bounds : Rectangle.Empty;
        if (rect != HoverRect)
        {
            HoverRect = rect;
            RefreshAll();
        }
    }

    /// <summary>MouseDown 命中预选：进入待确认（位移超阈值转拖拽，未超则 MouseUp 确认）。</summary>
    public void BeginPendingConfirm(Point virtualPt)
    {
        State = SelState.PendingConfirm;
        _confirmAnchor = virtualPt;
    }

    /// <summary>PendingConfirm 中位移超阈值 → 转手动拖拽。返回是否转换。</summary>
    public bool UpdatePendingConfirm(Point virtualPt)
    {
        if (State != SelState.PendingConfirm) return false;
        int dx = Math.Abs(virtualPt.X - _confirmAnchor.X);
        int dy = Math.Abs(virtualPt.Y - _confirmAnchor.Y);
        if (Math.Max(dx, dy) <= _dragThreshold) return false;
        BeginDrag(_confirmAnchor);
        UpdateDrag(virtualPt);
        return true;
    }

    /// <summary>PendingConfirm 的 MouseUp：确认预选窗口为选区。</summary>
    public void ConfirmHover()
    {
        if (State != SelState.PendingConfirm || HoverRect.IsEmpty)
        {
            EndInteraction();
            return;
        }
        Selection = HoverRect;
        ClearHover();
        State = SelState.Selected;
        RefreshAll();
    }

    private void ClearHover()
    {
        if (!HoverRect.IsEmpty)
        {
            HoverRect = Rectangle.Empty;
            RefreshAll();
        }
    }

    /// <summary>遮罩窗按本屏 DPI 设定拖动判定阈值（物理像素）。</summary>
    public void SetDragThreshold(int px) => _dragThreshold = Math.Max(1, px);

    public void BeginDrag(Point virtualPt)
    {
        State = SelState.Dragging;
        DragAnchor = virtualPt;
        Selection = Rectangle.Empty;
        ClearHover();
        RefreshAll();
    }

    public void BeginMove(Point virtualPt)
    {
        State = SelState.Moving;
        DragAnchor = virtualPt;
        DragOrigSel = Selection;
        ClearHover();
    }

    public void BeginResize(int handle, Point virtualPt)
    {
        State = SelState.Resizing;
        ResizeHandle = handle;
        DragAnchor = virtualPt;
        DragOrigSel = Selection;
        ClearHover();
    }

    public void UpdateDrag(Point virtualPt)
    {
        switch (State)
        {
            case SelState.Dragging:
                Selection = RectFromPoints(DragAnchor, virtualPt);
                break;
            case SelState.Moving:
                var d = new Size(virtualPt.X - DragAnchor.X, virtualPt.Y - DragAnchor.Y);
                Selection = MoveRectIntoVirtual(DragOrigSel, d);
                break;
            case SelState.Resizing:
                Selection = ResizeRect(DragOrigSel, DragAnchor, virtualPt, ResizeHandle);
                break;
        }
        RefreshAll();
    }

    public void EndInteraction()
    {
        if (State is SelState.Dragging or SelState.Resizing or SelState.Moving)
        {
            if (Selection.IsEmpty || Selection.Width < 2 || Selection.Height < 2)
            {
                State = SelState.Idle;   // 太小视为误触
                Selection = Rectangle.Empty;
            }
            else
            {
                State = SelState.Selected;
            }
        }
        RefreshAll();
    }

    public void Cancel() => Close();

    // ---- 标注操作（M3，由遮罩窗输入事件驱动）----

    /// <summary>遮罩窗按本屏 DPI 修正标注默认尺寸（物理像素，Begin 后调用一次）。</summary>
    public void SetUiScale(int stroke, int text, int serial, int mosaic)
    {
        _strokeWidth = Math.Max(2, stroke);
        _textSize = Math.Max(12, text);
        _serialDiameter = Math.Max(20, serial);
        _mosaicBlock = Math.Max(8, mosaic);
    }

    /// <summary>切换激活工具（再次点击同一工具 = 取消，回到选择态）。</summary>
    public void SetTool(AnnotationTool tool)
    {
        _activeTool = _activeTool == tool ? AnnotationTool.None : tool;
        RefreshAll();
    }

    public void SetStrokeColor(Color c) { _strokeColor = c; RefreshAll(); }

    public void SetStrokeWidth(int px) { _strokeWidth = Math.Max(1, px); RefreshAll(); }

    /// <summary>开始一笔拖动型标注（Rect/Ellipse/Arrow/Pen/Mosaic）。</summary>
    public void BeginAnnotate(AnnotationTool tool, Point v)
    {
        if (_activeTool == AnnotationTool.None || Selection.IsEmpty) return;
        _pending = tool switch
        {
            AnnotationTool.Rect or AnnotationTool.Ellipse or AnnotationTool.Mosaic
                or AnnotationTool.Arrow or AnnotationTool.Pen
                => new Annotation(tool, new[] { v, v }, _strokeColor, _strokeWidth, Block: _mosaicBlock),
            _ => null,   // Text/Serial 是单击型，不走这里
        };
        if (_pending == null) return;
        State = SelState.Annotating;
        RefreshAll();
    }

    public void UpdateAnnotate(Point v)
    {
        if (State != SelState.Annotating || _pending == null) return;
        var pts = _pending.Points;
        if (_pending.Kind == AnnotationTool.Pen)
        {
            var last = pts[^1];
            if (Math.Abs(v.X - last.X) + Math.Abs(v.Y - last.Y) < 2) return;   // 采点间距阈值
            _pending = _pending with { Points = pts.Append(v).ToArray() };
        }
        else
        {
            _pending = _pending with { Points = new[] { pts[0], v } };
        }
        RefreshAll();
    }

    /// <summary>一笔结束：有效则烙定进栈（清空 redo），过小视为误触丢弃。</summary>
    public void EndAnnotate()
    {
        if (State != SelState.Annotating || _pending == null) return;
        var rec = _pending;
        _pending = null;
        State = SelState.Selected;

        bool ok = rec.Kind switch
        {
            AnnotationTool.Rect or AnnotationTool.Ellipse or AnnotationTool.Mosaic
                => rec.Bounds().Width >= 3 && rec.Bounds().Height >= 3,
            AnnotationTool.Arrow or AnnotationTool.Pen => rec.Points.Length >= 2
                && Math.Abs(rec.Points[^1].X - rec.Points[0].X)
                + Math.Abs(rec.Points[^1].Y - rec.Points[0].Y) >= 3,
            _ => false,
        };
        if (ok) _history.Push(rec);
        RefreshAll();
    }

    /// <summary>Esc 取消当前这一笔（不进栈）。</summary>
    public void CancelPendingAnnotation()
    {
        if (_pending == null) return;
        _pending = null;
        if (State == SelState.Annotating) State = SelState.Selected;
        RefreshAll();
    }

    public void CommitText(Point v, string text)
    {
        if (Selection.IsEmpty || string.IsNullOrWhiteSpace(text)) return;
        _history.Push(new Annotation(AnnotationTool.Text, new[] { v }, _strokeColor, _textSize, Text: text));
        RefreshAll();
    }

    public void CommitSerial(Point v)
    {
        if (Selection.IsEmpty) return;
        _history.Push(new Annotation(AnnotationTool.Serial, new[] { v }, _strokeColor, _serialDiameter,
            Serial: _history.NextSerial()));
        RefreshAll();
    }

    public void Undo()
    {
        if (_history.Undo() != null) RefreshAll();
    }

    public void Redo()
    {
        if (_history.Redo() != null) RefreshAll();
    }

    /// <summary>马赛克取样：给定虚拟矩形，从底图快照「缩小-最近邻放大」出像素化位图（调用方 Dispose）。跨屏时各屏分别取样拼合。</summary>
    public Bitmap MosaicSample(Rectangle rect)
    {
        int block = AnnotationGeometry.NormalizeBlock(_mosaicBlock);
        var bmp = new Bitmap(rect.Width, rect.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        foreach (var shot in _shots)
        {
            var inter = Rectangle.Intersect(rect, shot.Bounds);
            if (inter.IsEmpty) continue;
            int bw = Math.Max(1, inter.Width / block), bh = Math.Max(1, inter.Height / block);
            using var small = new Bitmap(bw, bh, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var sg = Graphics.FromImage(small))
            {
                sg.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                sg.DrawImage(shot.Bitmap, new Rectangle(0, 0, bw, bh),
                    new Rectangle(inter.X - shot.Bounds.X, inter.Y - shot.Bounds.Y, inter.Width, inter.Height),
                    GraphicsUnit.Pixel);
            }
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(small, new Rectangle(inter.X - rect.X, inter.Y - rect.Y, inter.Width, inter.Height),
                0, 0, bw, bh, GraphicsUnit.Pixel);
        }
        return bmp;
    }

    /// <summary>合成选区输出（底图 + 标注），按标注栈版本缓存。返回克隆（调用方 Dispose）。</summary>
    public Bitmap ComposeOutput()
    {
        if (_composeCache == null || _cacheVersion != _history.Version || _cacheSel != Selection)
        {
            _composeCache?.Dispose();
            _composeCache = ScreenCapture.ComposeSelection(_shots, Selection);
            if (_history.Count > 0)
            {
                using var g = Graphics.FromImage(_composeCache);
                g.SetClip(new Rectangle(0, 0, Selection.Width, Selection.Height));
                g.TranslateTransform(-Selection.X, -Selection.Y);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                AnnotationRenderer.Draw(g, _history, MosaicSample);
            }
            _cacheVersion = _history.Version;
            _cacheSel = Selection;
        }
        return (Bitmap)_composeCache.Clone();
    }

    public void CopySelection()
    {
        if (State != SelState.Selected || Selection.IsEmpty) return;
        using var bmp = ComposeOutput();
        try
        {
            Clipboard.SetImage(bmp);
        }
        catch (ExternalException)
        {
            // 剪贴板偶发占用，失败静默（M1a 级别）
        }
        Close();
    }

    /// <summary>把当前选区钉成贴图（M2）：先关遮罩再建贴图窗，贴在选区原位置。标注随图带出。</summary>
    public void PinSelection()
    {
        if (State != SelState.Selected || Selection.IsEmpty) return;
        using var bmp = ComposeOutput();
        var at = Selection.Location;
        Close();
        PinManager.Current.Create(bmp, at);
    }

    public void SaveSelection()
    {
        if (State != SelState.Selected || Selection.IsEmpty || _interactive == null) return;
        using var bmp = ComposeOutput();
        using var dlg = new SaveFileDialog
        {
            Filter = "PNG 图片|*.png|JPEG 图片|*.jpg|BMP 图片|*.bmp",
            FileName = $"PlainSnip-{DateTime.Now:yyyyMMdd-HHmmss}.png",
        };
        _interactive.TopMost = false;
        try
        {
            if (dlg.ShowDialog(_interactive) == DialogResult.OK)
            {
                System.Drawing.Imaging.ImageFormat fmt =
                    Path.GetExtension(dlg.FileName).Equals(".jpg", StringComparison.OrdinalIgnoreCase) ? System.Drawing.Imaging.ImageFormat.Jpeg :
                    Path.GetExtension(dlg.FileName).Equals(".bmp", StringComparison.OrdinalIgnoreCase) ? System.Drawing.Imaging.ImageFormat.Bmp :
                    System.Drawing.Imaging.ImageFormat.Png;
                bmp.Save(dlg.FileName, fmt);
                Close();
                return;
            }
        }
        finally
        {
            if (_current == this && _interactive != null && !_interactive.IsDisposed)
                _interactive.TopMost = true;
        }
    }

    private void Close()
    {
        _current = null;
        foreach (var o in _overlays) o.Close();
        foreach (var shot in _shots) shot.Bitmap.Dispose();
        _shots.Clear();
        _overlays.Clear();
        _interactive = null;
        _composeCache?.Dispose();
        _composeCache = null;
        _cacheVersion = -1;
        _pending = null;
    }

    public void RefreshAll()
    {
        foreach (var o in _overlays) o.Invalidate();
    }

    /// <summary>选区与本屏的交集（本地物理坐标）；无选区返回 Empty。</summary>
    public Rectangle SelectionLocal(Screen screen)
    {
        if (Selection.IsEmpty) return Rectangle.Empty;
        var inter = Rectangle.Intersect(Selection, screen.Bounds);
        if (inter.IsEmpty) return Rectangle.Empty;
        inter.Offset(-screen.Bounds.X, -screen.Bounds.Y);
        return inter;
    }

    /// <summary>悬停候选与本屏的交集（本地物理坐标）；无候选返回 Empty。</summary>
    public Rectangle HoverLocal(Screen screen)
    {
        if (HoverRect.IsEmpty) return Rectangle.Empty;
        var inter = Rectangle.Intersect(HoverRect, screen.Bounds);
        if (inter.IsEmpty) return Rectangle.Empty;
        inter.Offset(-screen.Bounds.X, -screen.Bounds.Y);
        return inter;
    }

    private static Rectangle RectFromPoints(Point a, Point b) =>
        Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    private static Rectangle MoveRectIntoVirtual(Rectangle rect, Size d)
    {
        var virtualBounds = SystemInformation.VirtualScreen;
        var moved = new Rectangle(rect.Location + d, rect.Size);
        moved.X = Math.Clamp(moved.X, virtualBounds.Left, Math.Max(virtualBounds.Left, virtualBounds.Right - moved.Width));
        moved.Y = Math.Clamp(moved.Y, virtualBounds.Top, Math.Max(virtualBounds.Top, virtualBounds.Bottom - moved.Height));
        return moved;
    }

    private static Rectangle ResizeRect(Rectangle orig, Point anchor, Point cur, int handle)
    {
        int l = orig.Left, t = orig.Top, r = orig.Right, b = orig.Bottom;
        if ((handle & 1) != 0) l = Math.Min(cur.X, r - 2);   // 左
        if ((handle & 2) != 0) t = Math.Min(cur.Y, b - 2);   // 上
        if ((handle & 4) != 0) r = Math.Max(cur.X, l + 2);   // 右
        if ((handle & 8) != 0) b = Math.Max(cur.Y, t + 2);   // 下
        return Rectangle.FromLTRB(l, t, r, b);
    }

    public void Dispose()
    {
        if (_current == this) Close();
    }
}
