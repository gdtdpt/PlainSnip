using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PlainSnip;

/// <summary>单帧处理结果状态。</summary>
public enum ScrollFrameStatus
{
    /// <summary>首帧，已作为拼接结果基底。</summary>
    FirstFrame,
    /// <summary>追加了新内容。</summary>
    Appended,
    /// <summary>本帧没有产生新内容（未滚动 / 到底 / 无新行）。</summary>
    NoChange,
    /// <summary>匹配失败：本帧与已拼结果找不到重叠（提示用户回滚）。</summary>
    MatchFailed,
    /// <summary>已达高度上限（本帧内容被截断到上限，应提示停止）。</summary>
    HeightLimit,
}

/// <summary>单帧处理结果。</summary>
public readonly record struct ScrollFrameResult(
    ScrollFrameStatus Status,
    int ViewTopY,       // 绿框：当前帧顶部在拼接结果中的 y（-1 = 未知）
    int ResultHeight,   // 处理后的结果高度
    int AppendedHeight) // 本帧追加的新内容高度
{
    public bool ReachedLimit => Status == ScrollFrameStatus.HeightLimit;
}

/// <summary>
/// M4 滚动截图拼接会话（docs/m4-scroll-design.md §6）。
/// 纯逻辑、无 UI 依赖：UI 层定时送帧（ScreenCapture.CaptureRegion 选区矩形），
/// 会话负责到底检测、对齐拼接、失败恢复（D6）、绿框位置、高度上限（D7）。
/// 对齐算法为自行实现，思路借鉴 ShareX（GPL v3，未复制任何代码）：
/// 在已拼结果底部（深度 ≤ 帧高/2）为帧的每一行找最长连续相同行序列，帧对齐点以下为新内容。
/// </summary>
public sealed class ScrollCaptureSession : IDisposable
{
    /// <summary>结果高度上限（1920 宽 ≈ 126MB 单份位图，见设计文档 D7）。</summary>
    public const int MaxResultHeight = 16384;

    private Bitmap? _result;
    private Bitmap? _lastFrame;          // 上一帧（去重 + 到底检测）
    private int _sameStreak;             // 连续相同帧计数
    private int _bestIgnoreBottom;       // 底部忽略行历史最佳（防边缘抖动来回横跳）
    private int _lastViewTopY = -1;

    public int ResultHeight => _result?.Height ?? 0;

    /// <summary>当前拼接结果（只读借用，随会话释放；UI 显示请自行 Clone）。</summary>
    public Bitmap? Result => _result;

    public void Dispose()
    {
        _result?.Dispose(); _result = null;
        _lastFrame?.Dispose(); _lastFrame = null;
    }

    /// <summary>送入一帧（物理像素、宽 = 选区宽）。线程约束：调用方串行调用。</summary>
    public ScrollFrameResult ProcessFrame(Bitmap frame)
    {
        // 去重 / 到底检测：与上一帧完全相同（设计 D4，连续性由 UI 层按 streak 判定）
        if (_lastFrame != null && SameSize(_lastFrame, frame) && BitmapsEqual(_lastFrame, frame))
        {
            _sameStreak++;
            return new ScrollFrameResult(ScrollFrameStatus.NoChange, _lastViewTopY, ResultHeight, 0);
        }
        _sameStreak = 0;

        // 首帧：直接作为结果基底
        if (_result == null)
        {
            _result = (Bitmap)frame.Clone();
            ReplaceLastFrame(frame);
            _lastViewTopY = 0;
            return new ScrollFrameResult(ScrollFrameStatus.FirstFrame, 0, _result.Height, _result.Height);
        }

        int w = frame.Width, h = frame.Height;
        if (w != _result.Width || h < 4 || h > _result.Height)
            return Fail(frame);   // 尺寸异常按无匹配处理（设计 §6），结果保留待回滚

        var (matchIndex, matchCount, ignoreBottom) = AlignFrame(_result, frame);

        // 失败即失败（D6）：不沿用历史参数强行追加（ShareX bestGuess 会把无关帧贴进结果），
        // 提示用户回滚，已拼结果保持原样，续拼由 AlignFrame 的底部窗口搜索自然完成。
        if (matchCount <= 0)
            return Fail(frame);

        int appended = h - matchIndex - 1;   // 帧对齐点以下为新内容
        if (appended < 0) appended = 0;

        int resultH = _result.Height;
        int targetHeight = resultH - ignoreBottom + appended;
        var status = appended > 0 ? ScrollFrameStatus.Appended : ScrollFrameStatus.NoChange;

        // 高度上限（D7）：截断到上限并提示
        if (targetHeight > MaxResultHeight)
        {
            appended -= targetHeight - MaxResultHeight;
            targetHeight = MaxResultHeight;
            status = ScrollFrameStatus.HeightLimit;
        }

        if (appended > 0)
        {
            var grown = new Bitmap(w, targetHeight, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(grown))
            {
                g.DrawImage(_result,
                    new Rectangle(0, 0, w, resultH - ignoreBottom),
                    new Rectangle(0, 0, w, resultH - ignoreBottom),
                    GraphicsUnit.Pixel);
                g.DrawImage(frame,
                    new Rectangle(0, resultH - ignoreBottom, w, appended),
                    new Rectangle(0, matchIndex + 1, w, appended),
                    GraphicsUnit.Pixel);
            }
            _result.Dispose();
            _result = grown;
        }

        _lastViewTopY = Math.Max(0, resultH - ignoreBottom - 1 - matchIndex);
        ReplaceLastFrame(frame);

        return new ScrollFrameResult(status, _lastViewTopY, _result.Height, appended);
    }

    /// <summary>连续相同帧计数（UI 层 ≥2 视为到底：自动滚动停止 / 手动提示）。</summary>
    public int SameFrameStreak => _sameStreak;

    /// <summary>匹配失败：保留已拼结果与上帧，等回滚（设计 D6）。</summary>
    private ScrollFrameResult Fail(Bitmap frame)
    {
        ReplaceLastFrame(frame);
        return new ScrollFrameResult(ScrollFrameStatus.MatchFailed, _lastViewTopY, ResultHeight, 0);
    }

    private void ReplaceLastFrame(Bitmap frame)
    {
        _lastFrame?.Dispose();
        _lastFrame = (Bitmap)frame.Clone();
    }

    // ---- 对齐搜索 ----

    private (int MatchIndex, int MatchCount, int IgnoreBottom) AlignFrame(Bitmap result, Bitmap frame)
    {
        int w = frame.Width, h = frame.Height;
        int matchLimit = h / 2;
        int ignoreSide = Math.Clamp(Math.Max(50, w / 20), 50, w / 3);

        // 底部忽略边（AutoIgnoreBottomEdge 思路）：两图自底向上找第一行差异，
        // 忽略行数 = 基线(50 或 h/10) + 差异行序号，与历史最佳取 max（防边缘抖动来回横跳）
        int ignoreBottomMax = h / 3;
        int ignoreBottom = Math.Max(50, h / 10);
        {
            int probeMax = Math.Min(ignoreBottomMax, Math.Min(result.Height, h));
            var resultTail = GetRows(result, result.Height - probeMax, probeMax);
            var frameRows = GetRows(frame, 0, h);
            for (int i = 0; i < probeMax; i++)
            {
                int rr = resultTail.Length / (w * 4) - 1 - i;   // result 区域内行号（自底向上）
                int fr = h - 1 - i;
                if (!RowEqual(resultTail, rr, frameRows, fr, w, ignoreSide))
                {
                    ignoreBottom += i;
                    break;
                }
            }
            if (_bestIgnoreBottom > ignoreBottom) ignoreBottom = _bestIgnoreBottom;
            ignoreBottom = Math.Min(ignoreBottom, ignoreBottomMax);
        }

        // 结果底部比较窗口：必须覆盖对齐搜索的完整深度（rectBottom 向上 matchLimit 行）
        int regionRows = Math.Min(result.Height, matchLimit + ignoreBottom + 1);
        var resultWindow = GetRows(result, result.Height - regionRows, regionRows);
        var frameBuf = GetRows(frame, 0, h);

        int rectBottom = result.Height - ignoreBottom - 1;   // 结果中可对齐的最后行
        int bestCount = 0, bestIndex = 0;
        for (int fy = h - 1; fy >= 0 && bestCount < matchLimit; fy--)
        {
            int count = 0;
            for (int y = 0; fy - y >= 0 && count < matchLimit; y++)
            {
                int ry = rectBottom - y;
                if (ry < 0) break;
                int rrInWin = ry - (result.Height - regionRows);
                if (rrInWin < 0) break;
                if (!RowEqual(resultWindow, rrInWin, frameBuf, fy - y, w, ignoreSide)) break;
                count++;
            }
            if (count > bestCount)
            {
                bestCount = count;
                bestIndex = fy;
            }
        }
        return (bestIndex, bestCount, ignoreBottom);
    }

    /// <summary>把位图指定行区域拷贝为连续行缓冲（每行 w*4 字节，自顶向下）。</summary>
    private static byte[] GetRows(Bitmap bmp, int top, int rows)
    {
        int w = bmp.Width;
        var rect = new Rectangle(0, top, w, rows);
        var bd = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var buf = new byte[w * 4 * rows];
            int rowBytes = w * 4;
            for (int y = 0; y < rows; y++)
                Marshal.Copy(IntPtr.Add(bd.Scan0, y * bd.Stride), buf, y * rowBytes, rowBytes);
            return buf;
        }
        finally { bmp.UnlockBits(bd); }
    }

    private static bool RowEqual(byte[] a, int aRow, byte[] b, int bRow, int w, int ignoreSide)
    {
        int rowBytes = w * 4;
        int skip = ignoreSide * 4;
        int len = rowBytes - skip * 2;
        if (len <= 0) return false;
        return a.AsSpan(aRow * rowBytes + skip, len).SequenceEqual(b.AsSpan(bRow * rowBytes + skip, len));
    }

    private static bool SameSize(Bitmap a, Bitmap b) => a.Width == b.Width && a.Height == b.Height;

    private static bool BitmapsEqual(Bitmap a, Bitmap b)
    {
        if (!SameSize(a, b)) return false;
        var pa = GetRows(a, 0, a.Height);
        var pb = GetRows(b, 0, b.Height);
        return pa.AsSpan().SequenceEqual(pb.AsSpan());
    }
}
