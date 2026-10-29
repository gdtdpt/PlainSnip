using System.Runtime.InteropServices;

namespace PlainSnip;

/// <summary>
/// 截图会话：F1 → 冻结快照 → 各屏遮罩窗 → 框选/微调 → 复制/保存/取消。
/// 选区以虚拟桌面物理坐标在此统一记录；遮罩窗只负责各自屏幕的输入与绘制。
/// 设计为「可复用组件」：M4 滚动截图的先选区入口复用同一交互（回调确认结果）。
/// </summary>
public sealed class CaptureSession : IDisposable
{
    public enum SelState { Idle, Dragging, Resizing, Moving, Selected }

    private List<ScreenCapture.ScreenShot> _shots = new();
    private readonly List<CaptureOverlayForm> _overlays = new();
    private CaptureOverlayForm? _interactive;   // 当前接收鼠标输入的遮罩窗
    private static CaptureSession? _current;

    public SelState State { get; private set; } = SelState.Idle;
    public Rectangle Selection { get; private set; }    // 虚拟桌面物理坐标
    public int ResizeHandle { get; private set; } = -1; // 0-7，见 CaptureOverlayForm.HandleHitTest
    public Point DragAnchor { get; private set; }       // 拖动起点（虚拟坐标）
    public Rectangle DragOrigSel { get; private set; }  // 拖动开始时的选区
    public bool DebugMode { get; init; }                // --shot 冒烟：自动框选 + 定时退出

    public IReadOnlyList<ScreenCapture.ScreenShot> Shots => _shots;
    public CaptureOverlayForm? Interactive => _interactive;

    /// <summary>启动会话。已有会话时返回 null（F1 重复按忽略）。</summary>
    public static CaptureSession? Start(bool debug = false)
    {
        if (_current != null) return null;
        _current = new CaptureSession { DebugMode = debug };
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

        if (DebugMode)
        {
            // 冒烟：自动框选交互屏中部 40% 区域，6 秒后自动退出（防挂死）
            var b = _interactive!._screen.Bounds;
            Selection = new Rectangle(b.X + b.Width * 3 / 10, b.Y + b.Height * 3 / 10,
                b.Width * 2 / 5, b.Height * 2 / 5);
            State = SelState.Selected;
            RefreshAll();
            var t = new System.Windows.Forms.Timer { Interval = 6000 };
            t.Tick += (_, _) => { t.Stop(); Cancel(); };
            t.Start();
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

    public void BeginDrag(Point virtualPt)
    {
        State = SelState.Dragging;
        DragAnchor = virtualPt;
        Selection = Rectangle.Empty;
        RefreshAll();
    }

    public void BeginMove(Point virtualPt)
    {
        State = SelState.Moving;
        DragAnchor = virtualPt;
        DragOrigSel = Selection;
    }

    public void BeginResize(int handle, Point virtualPt)
    {
        State = SelState.Resizing;
        ResizeHandle = handle;
        DragAnchor = virtualPt;
        DragOrigSel = Selection;
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

    public void CopySelection()
    {
        if (State != SelState.Selected || Selection.IsEmpty) return;
        using var bmp = ScreenCapture.ComposeSelection(_shots, Selection);
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

    public void SaveSelection()
    {
        if (State != SelState.Selected || Selection.IsEmpty || _interactive == null) return;
        using var bmp = ScreenCapture.ComposeSelection(_shots, Selection);
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
