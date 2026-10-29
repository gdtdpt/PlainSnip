using System.Drawing.Drawing2D;

namespace PlainSnip;

/// <summary>标注工具种类（M3 烙定式标注）。</summary>
public enum AnnotationTool
{
    None,    // 选择态：无激活工具
    Rect,
    Ellipse,
    Arrow,
    Pen,
    Text,
    Mosaic,
    Serial,
}

/// <summary>
/// 一条不可变的标注记录（烙定式：提交后不可选中/编辑）。
/// Points 语义随 Kind 而定：
///   Rect/Ellipse/Mosaic = [对角1, 对角2]；Arrow/Pen = 点列；
///   Text/Serial = [锚点]。坐标一律虚拟桌面物理像素。
/// Width：笔画宽度（物理 px）；Text 时 = 字号（px），Serial 时 = 圆徽章直径（px）。
/// Block：马赛克块大小（物理 px）。
/// </summary>
public sealed record Annotation(
    AnnotationTool Kind, Point[] Points, Color Color, int Width,
    string? Text = null, int Serial = 0, int Block = 0)
{
    /// <summary>由两个对角点取整矩形（Rect/Ellipse/Mosaic 通用）。</summary>
    public Rectangle Bounds() => Rectangle.FromLTRB(
        Math.Min(Points[0].X, Points[^1].X), Math.Min(Points[0].Y, Points[^1].Y),
        Math.Max(Points[0].X, Points[^1].X), Math.Max(Points[0].Y, Points[^1].Y));
}

/// <summary>标注历史栈：提交/撤销/重做（纯逻辑，selftest 直接覆盖）。</summary>
public sealed class AnnotationHistory : IReadOnlyList<Annotation>
{
    private readonly List<Annotation> _records = new();
    private readonly Stack<Annotation> _redo = new();

    public int Count => _records.Count;
    public int Version { get; private set; }   // 输出缓存失效依据
    public bool CanUndo => _records.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public Annotation this[int i] => _records[i];

    /// <summary>提交一条记录；新记录清空 redo 栈。</summary>
    public void Push(Annotation a)
    {
        _records.Add(a);
        _redo.Clear();
        Version++;
    }

    /// <summary>撤销：返回被撤销的记录（栈空返回 null）。</summary>
    public Annotation? Undo()
    {
        if (_records.Count == 0) return null;
        var a = _records[^1];
        _records.RemoveAt(_records.Count - 1);
        _redo.Push(a);
        Version++;
        return a;
    }

    /// <summary>重做：返回被恢复的记录（栈空返回 null）。</summary>
    public Annotation? Redo()
    {
        if (_redo.Count == 0) return null;
        var a = _redo.Pop();
        _records.Add(a);
        Version++;
        return a;
    }

    /// <summary>序号规则：取现有最大序号 + 1（撤销后重画取新号，重做恢复原号）。</summary>
    public static int NextSerial(IReadOnlyList<Annotation> records)
    {
        int max = 0;
        foreach (var r in records)
            if (r.Kind == AnnotationTool.Serial) max = Math.Max(max, r.Serial);
        return max + 1;
    }

    public int NextSerial() => NextSerial(_records);

    public IEnumerator<Annotation> GetEnumerator() => _records.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _records.GetEnumerator();
}

/// <summary>箭头几何（纯函数）：主线终点 + 箭头三角。</summary>
public static class AnnotationGeometry
{
    /// <summary>
    /// 由尾点 a、头点 b、线宽 width 计算箭头几何。
    /// 头长 = max(width*3.5, 12)；总长不足两倍头长时收缩头长（短箭头退化为大头）。
    /// 返回 (主线终点 lineEnd, 三角形三点 head)，头点为 b。
    /// </summary>
    public static (Point LineEnd, Point[] Head) ArrowGeometry(Point a, Point b, int width)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        double headLen = Math.Max(width * 3.5, 12);
        if (len < 1e-6)
            return (a, new[] { b, b, b });
        if (headLen * 2 > len) headLen = len / 2;

        double ux = dx / len, uy = dy / len;            // 单位方向
        double px = -uy, py = ux;                        // 单位法线
        var lineEnd = new Point((int)Math.Round(b.X - ux * headLen), (int)Math.Round(b.Y - uy * headLen));
        var c1 = new Point((int)Math.Round(lineEnd.X + px * headLen * 0.5), (int)Math.Round(lineEnd.Y + py * headLen * 0.5));
        var c2 = new Point((int)Math.Round(lineEnd.X - px * headLen * 0.5), (int)Math.Round(lineEnd.Y - py * headLen * 0.5));
        return (lineEnd, new[] { b, c1, c2 });
    }

    /// <summary>马赛克块大小下限（防止除零与过细）。</summary>
    public static int NormalizeBlock(int block) => Math.Max(4, block);
}

/// <summary>
/// 标注渲染唯一入口：屏幕显示与输出合成共用（PROJECT.md §5.8）。
/// 在「虚拟桌面物理坐标系」下绘制——调用方自行 Translate/Clip：
///   遮罩窗：Translate(-screen.Bounds.Location) + SetClip(选区∩本屏)；
///   输出合成：Translate(-selection.Location) + SetClip(选区)。
/// mosaicSampler：给定虚拟矩形返回该区域马赛克位图（来自底图快照， renderer 内部 Dispose）。
/// </summary>
public static class AnnotationRenderer
{
    public static void Draw(Graphics g, IReadOnlyList<Annotation> records, Func<Rectangle, Bitmap> mosaicSampler)
    {
        foreach (var rec in records)
        {
            switch (rec.Kind)
            {
                case AnnotationTool.Rect:
                {
                    using var pen = new Pen(rec.Color, Math.Max(1, rec.Width));
                    var r = rec.Bounds();
                    g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                    break;
                }
                case AnnotationTool.Ellipse:
                {
                    using var pen = new Pen(rec.Color, Math.Max(1, rec.Width));
                    var r = rec.Bounds();
                    g.DrawEllipse(pen, r.X, r.Y, r.Width, r.Height);
                    break;
                }
                case AnnotationTool.Arrow:
                {
                    if (rec.Points.Length < 2) break;
                    using var pen = new Pen(rec.Color, Math.Max(1, rec.Width));
                    using var brush = new SolidBrush(rec.Color);
                    var (lineEnd, head) = AnnotationGeometry.ArrowGeometry(rec.Points[0], rec.Points[^1], rec.Width);
                    g.DrawLine(pen, rec.Points[0], lineEnd);
                    g.FillPolygon(brush, head);
                    break;
                }
                case AnnotationTool.Pen:
                {
                    if (rec.Points.Length < 2) break;
                    using var pen = new Pen(rec.Color, Math.Max(1, rec.Width));
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    g.DrawLines(pen, rec.Points);
                    break;
                }
                case AnnotationTool.Text:
                {
                    if (string.IsNullOrEmpty(rec.Text) || rec.Points.Length == 0) break;
                    using var font = new Font("Microsoft YaHei UI", Math.Max(8, rec.Width), GraphicsUnit.Pixel);
                    using var brush = new SolidBrush(rec.Color);
                    g.DrawString(rec.Text, font, brush, rec.Points[0], StringFormat.GenericDefault);
                    break;
                }
                case AnnotationTool.Mosaic:
                {
                    var r = rec.Bounds();
                    if (r.Width < 2 || r.Height < 2) break;
                    using var bmp = mosaicSampler(r);
                    g.DrawImageUnscaled(bmp, r.X, r.Y);
                    break;
                }
                case AnnotationTool.Serial:
                {
                    if (rec.Points.Length == 0) break;
                    int d = Math.Max(16, rec.Width);
                    var r = new Rectangle(rec.Points[0].X - d / 2, rec.Points[0].Y - d / 2, d, d);
                    using var brush = new SolidBrush(rec.Color);
                    g.FillEllipse(brush, r);
                    using var font = new Font("Microsoft YaHei UI", d * 0.55f, GraphicsUnit.Pixel);
                    var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString(rec.Serial.ToString(), font, Brushes.White, r, fmt);
                    break;
                }
            }
        }
    }
}
