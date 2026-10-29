using System.Runtime.InteropServices;

namespace PlainSnip;

/// <summary>
/// 窗口自动检测（两级，§5.6）：
/// 候选查找不走 WindowFromPoint——遮罩窗全屏置顶必命中自己，改为 EnumWindows 按 Z 序
/// 自顶向下找第一个「可见 + 未 cloaked + 非自身 PID + DWM 扩展边界包含光标」的顶层窗口，
/// 再用 RealChildWindowFromPoint 递归下钻到最深可见子窗口（过小块回退上一级）。
/// 输出以虚拟桌面物理坐标记录（PMV2 进程，Win32 矩形 API 天然返回物理像素）。
/// </summary>
public static class WindowDetector
{
    public readonly record struct Candidate(Rectangle Bounds, IntPtr Hwnd);

    // ---- P/Invoke ----

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr RealChildWindowFromPoint(IntPtr hwnd, POINT clientPt);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hwnd, ref POINT pt);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;

    // ---- 检测入口 ----

    public static Candidate DetectAt(Point virtualPt)
    {
        IntPtr root = FindTopLevel(virtualPt);
        if (root == IntPtr.Zero) return default;

        // 下钻链：根 -> ... -> 最深子窗口，逐级记录边界供启发式回退
        var chain = new List<Candidate> { new(BoundsOf(root), root) };
        IntPtr cur = root;
        for (int depth = 0; depth < 8; depth++)
        {
            IntPtr child = ChildAt(cur, virtualPt);
            if (child == IntPtr.Zero || child == cur) break;
            chain.Add(new Candidate(BoundsOf(child), child));
            cur = child;
        }

        var picked = PickCandidate(chain);
        return picked;
    }

    /// <summary>Z 序自顶向下找第一个命中的顶层窗口（跳过自身进程）。</summary>
    private static IntPtr FindTopLevel(Point virtualPt)
    {
        IntPtr found = IntPtr.Zero;
        uint selfPid = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;          // 继续枚举
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == selfPid) return true;                                     // 自己的遮罩窗等
            if (IsCloaked(hwnd)) return true;                                    // 隐身 UWP
            var bounds = BoundsOf(hwnd);
            if (bounds.Width <= 0 || bounds.Height <= 0 || !bounds.Contains(virtualPt)) return true;
            found = hwnd;
            return false;                                                        // 命中，停止枚举
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>找 hwnd 下包含虚拟坐标的最深可见子窗口；无则返回 IntPtr.Zero。</summary>
    private static IntPtr ChildAt(IntPtr hwnd, Point virtualPt)
    {
        var pt = new POINT { X = virtualPt.X, Y = virtualPt.Y };
        if (!ScreenToClient(hwnd, ref pt)) return IntPtr.Zero;
        IntPtr child = RealChildWindowFromPoint(hwnd, pt);
        if (child == IntPtr.Zero || child == hwnd || !IsWindowVisible(child)) return IntPtr.Zero;
        return child;
    }

    /// <summary>DWM 扩展边界（去阴影边距）；DWM 不可用回退 GetWindowRect。</summary>
    private static Rectangle BoundsOf(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) == 0)
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        GetWindowRect(hwnd, out r);
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    private static bool IsCloaked(IntPtr hwnd)
        => DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0;

    // ---- 启发式（纯函数，selftest 覆盖） ----

    internal const int MinEdgePx = 32;   // 物理像素
    internal const int MinEdgePy = 24;
    internal const double MinAreaRatio = 0.005;

    /// <summary>过小块判定：边长过小或面积不足根窗口 0.5%。</summary>
    internal static bool TooSmall(Rectangle r, Rectangle root)
    {
        if (r.Width < MinEdgePx || r.Height < MinEdgePy) return true;
        if (root.Width <= 0 || root.Height <= 0) return false;
        return (double)r.Width * r.Height < (double)root.Width * root.Height * MinAreaRatio;
    }

    /// <summary>从最深候选逐级回退，取第一个不过小块；全过小则取根。</summary>
    internal static Candidate PickCandidate(IReadOnlyList<Candidate> chain)
    {
        var root = chain[0];
        for (int i = chain.Count - 1; i > 0; i--)
            if (!TooSmall(chain[i].Bounds, root.Bounds)) return chain[i];
        return root;
    }
}
