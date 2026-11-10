// SVG path 渲染：把 Lucide Icons（ISC License，24x24 视框、stroke-width 2、圆角端点）
// 的内嵌 d 数据解析为 GDI+ GraphicsPath，调用方按统一线宽描边（DrawPath），
// 从而保证所有按钮笔画粗细一致、任意 DPI 下矢量清晰。
// 解析支持：M/L/H/V/C/S/Q/T/A/Z（含小写相对形式）；弧线按 SVG 规范转为三次贝塞尔。
using System.Drawing.Drawing2D;

namespace PlainSnip;

public static class IconGlyphRenderer
{
    /// <summary>把图标的全部子路径合并为一条 GraphicsPath（已按 scale 缩放，原点 (0,0)，占 24*scale 见方）。</summary>
    public static GraphicsPath Build(string[] parts, float scale)
    {
        var path = new GraphicsPath { FillMode = FillMode.Winding };
        foreach (var d in parts)
        {
            // 每个 d 是独立子图：不开新 figure 的话，GDI+ 会把上一条路径的终点
            // 与本条起点连成线（× 被连成 ◁、图钉多出贯穿斜线的根因）
            path.StartFigure();
            AppendD(d, path);
        }

        if (scale != 1f)
        {
            using var m = new Matrix(scale, 0, 0, scale, 0, 0);
            path.Transform(m);
        }
        return path;
    }

    /// <summary>解析单条 d 数据并追加到 path。</summary>
    private static void AppendD(string d, GraphicsPath path)
    {
        var toks = Tokenize(d);
        int i = 0;
        var cur = new PointF(0, 0);
        var start = new PointF(0, 0);
        PointF lastCubicCtrl = default, lastQuadCtrl = default;
        bool prevCubic = false, prevQuad = false;

        char NextCmd()
        {
            while (i < toks.Count && toks[i] is string s && s.Length == 1 && char.IsLetter(s[0])) return s[0];
            return '\0';
        }

        float Num() => (float)toks[i++];

        void AddCubic(PointF c1, PointF c2, PointF end)
        {
            path.AddBezier(cur, c1, c2, end);
            cur = end;
            prevCubic = true;
            prevQuad = false;
            lastCubicCtrl = c2;
        }

        void AddLine(PointF p)
        {
            path.AddLine(cur, p);
            cur = p;
            prevCubic = prevQuad = false;
        }

        while (i < toks.Count)
        {
            char cmd = NextCmd();
            if (cmd == '\0') break;
            i++;
            bool rel = char.IsLower(cmd);
            switch (char.ToUpperInvariant(cmd))
            {
                case 'M':
                {
                    float x = Num(), y = Num();
                    if (rel) { x += cur.X; y += cur.Y; }
                    cur = start = new PointF(x, y);
                    prevCubic = prevQuad = false;
                    // 后续连续坐标按 L 处理
                    while (i < toks.Count && toks[i] is float)
                    {
                        float x2 = Num(), y2 = Num();
                        if (rel) { x2 += cur.X; y2 += cur.Y; }
                        AddLine(new PointF(x2, y2));
                    }
                    break;
                }
                case 'L':
                {
                    while (i < toks.Count && toks[i] is float)
                    {
                        float x = Num(), y = Num();
                        if (rel) { x += cur.X; y += cur.Y; }
                        AddLine(new PointF(x, y));
                    }
                    break;
                }
                case 'H':
                {
                    while (i < toks.Count && toks[i] is float)
                    {
                        float x = Num();
                        if (rel) x += cur.X;
                        AddLine(new PointF(x, cur.Y));
                    }
                    break;
                }
                case 'V':
                {
                    while (i < toks.Count && toks[i] is float)
                    {
                        float y = Num();
                        if (rel) y += cur.Y;
                        AddLine(new PointF(cur.X, y));
                    }
                    break;
                }
                case 'C':
                {
                    while (i < toks.Count && toks[i] is float)
                    {
                        float x1 = Num(), y1 = Num(), x2 = Num(), y2 = Num(), x = Num(), y = Num();
                        if (rel) { x1 += cur.X; y1 += cur.Y; x2 += cur.X; y2 += cur.Y; x += cur.X; y += cur.Y; }
                        AddCubic(new PointF(x1, y1), new PointF(x2, y2), new PointF(x, y));
                    }
                    break;
                }
                case 'S':
                {
                    while (i < toks.Count && toks[i] is float)
                    {
                        float x2 = Num(), y2 = Num(), x = Num(), y = Num();
                        if (rel) { x2 += cur.X; y2 += cur.Y; x += cur.X; y += cur.Y; }
                        var c1 = prevCubic ? new PointF(2 * cur.X - lastCubicCtrl.X, 2 * cur.Y - lastCubicCtrl.Y) : cur;
                        AddCubic(c1, new PointF(x2, y2), new PointF(x, y));
                    }
                    break;
                }
                case 'Q':
                {
                    while (i < toks.Count && toks[i] is float)
                    {
                        float cx = Num(), cy = Num(), x = Num(), y = Num();
                        if (rel) { cx += cur.X; cy += cur.Y; x += cur.X; y += cur.Y; }
                        lastQuadCtrl = new PointF(cx, cy);
                        prevQuad = true; prevCubic = false;
                        // 二次转三次
                        var c1 = new PointF(cur.X + 2f / 3f * (cx - cur.X), cur.Y + 2f / 3f * (cy - cur.Y));
                        var c2 = new PointF(x + 2f / 3f * (cx - x), y + 2f / 3f * (cy - y));
                        path.AddBezier(cur, c1, c2, new PointF(x, y));
                        cur = new PointF(x, y);
                    }
                    break;
                }
                case 'T':
                {
                    while (i < toks.Count && toks[i] is float)
                    {
                        float x = Num(), y = Num();
                        if (rel) { x += cur.X; y += cur.Y; }
                        var qc = prevQuad ? new PointF(2 * cur.X - lastQuadCtrl.X, 2 * cur.Y - lastQuadCtrl.Y) : cur;
                        lastQuadCtrl = qc; prevQuad = true; prevCubic = false;
                        var c1 = new PointF(cur.X + 2f / 3f * (qc.X - cur.X), cur.Y + 2f / 3f * (qc.Y - cur.Y));
                        var c2 = new PointF(x + 2f / 3f * (qc.X - x), y + 2f / 3f * (qc.Y - y));
                        path.AddBezier(cur, c1, c2, new PointF(x, y));
                        cur = new PointF(x, y);
                    }
                    break;
                }
                case 'A':
                {
                    while (i < toks.Count && toks[i] is float)
                    {
                        float rx = Num(), ry = Num(), rot = Num(), largeArc = Num(), sweep = Num(), x = Num(), y = Num();
                        if (rel) { x += cur.X; y += cur.Y; }
                        AddArcBezier(path, cur, rx, ry, rot, largeArc != 0, sweep != 0, new PointF(x, y));
                        cur = new PointF(x, y);
                        prevCubic = prevQuad = false;
                    }
                    break;
                }
                case 'Z':
                    if (cur != start) AddLine(start);
                    path.CloseFigure();
                    cur = start;
                    prevCubic = prevQuad = false;
                    break;
            }
        }
    }

    private static readonly char[] NumberStart = "+-.0123456789".ToCharArray();

    private static List<object> Tokenize(string d)
    {
        var toks = new List<object>();
        int n = d.Length, p = 0;
        while (p < n)
        {
            char c = d[p];
            if (char.IsLetter(c))
            {
                toks.Add(c.ToString());
                p++;
            }
            else if (char.IsWhiteSpace(c) || c == ',')
            {
                p++;
            }
            else if (Array.IndexOf(NumberStart, c) >= 0)
            {
                toks.Add(ParseNumber(d, ref p));
            }
            else p++;
        }
        return toks;
    }

    private static float ParseNumber(string s, ref int p)
    {
        int n = s.Length, start = p;
        // 符号
        if (p < n && (s[p] == '+' || s[p] == '-')) p++;
        // 整数部分
        while (p < n && char.IsDigit(s[p])) p++;
        // 小数部分（'.' 后可再接一段数字，之后若紧跟 '.'/'e'/'符号' 则当前数字结束）
        if (p < n && s[p] == '.')
        {
            p++;
            while (p < n && char.IsDigit(s[p])) p++;
        }
        // 指数
        if (p < n && (s[p] == 'e' || s[p] == 'E'))
        {
            int q = p + 1;
            if (q < n && (s[q] == '+' || s[q] == '-')) q++;
            if (q < n && char.IsDigit(s[q]))
            {
                p = q;
                while (p < n && char.IsDigit(s[p])) p++;
            }
        }
        return float.Parse(s[start..p], System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>SVG 椭圆弧 → 三段以内的三次贝塞尔（SVG 规范 F.6 端点参数化）。</summary>
    private static void AddArcBezier(GraphicsPath path, PointF p1, float rx, float ry, float xRotDeg,
        bool largeArc, bool sweep, PointF p2)
    {
        if (rx == 0 || ry == 0) { path.AddLine(p1, p2); return; }
        rx = Math.Abs(rx); ry = Math.Abs(ry);
        double rad = xRotDeg * Math.PI / 180.0;
        double cosR = Math.Cos(rad), sinR = Math.Sin(rad);
        double dx = (p1.X - p2.X) / 2, dy = (p1.Y - p2.Y) / 2;
        double x1 = cosR * dx + sinR * dy;
        double y1 = -sinR * dx + cosR * dy;

        double lam = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry);
        if (lam > 1)
        {
            double s = Math.Sqrt(lam);
            rx = (float)(s * rx); ry = (float)(s * ry);
        }
        double sign = largeArc != sweep ? 1 : -1;
        double num = rx * rx * ry * ry - rx * rx * y1 * y1 - ry * ry * x1 * x1;
        double den2 = rx * rx * y1 * y1 + ry * ry * x1 * x1;
        double co = sign * Math.Sqrt(Math.Max(0, num) / Math.Max(1e-12, den2));
        double cxp = co * rx * y1 / ry;
        double cyp = -co * ry * x1 / rx;
        double cx = cosR * cxp - sinR * cyp + (p1.X + p2.X) / 2;
        double cy = sinR * cxp + cosR * cyp + (p1.Y + p2.Y) / 2;

        double Theta(double ux, double uy, double vx, double vy)
        {
            double dot = ux * vx + uy * vy;
            double len = Math.Sqrt(ux * ux + uy * uy) * Math.Sqrt(vx * vx + vy * vy);
            double a = Math.Acos(Math.Clamp(dot / (len == 0 ? 1 : len), -1, 1));
            if (ux * vy - uy * vx < 0) a = -a;
            return a;
        }

        double th1 = Theta(1, 0, (x1 - cxp) / rx, (y1 - cyp) / ry);
        double dth = Theta((x1 - cxp) / rx, (y1 - cyp) / ry, (-x1 - cxp) / rx, (-y1 - cyp) / ry);
        if (!sweep && dth > 0) dth -= 2 * Math.PI;
        else if (sweep && dth < 0) dth += 2 * Math.PI;

        int segs = (int)Math.Ceiling(Math.Abs(dth) / (Math.PI / 2));
        double delta = dth / segs;
        double t = 4.0 / 3.0 * Math.Tan(delta / 4);

        double ux = Math.Cos(th1), uy = Math.Sin(th1);
        PointF prev = p1;
        for (int k = 0; k < segs; k++)
        {
            double th2 = th1 + delta;
            double vx = Math.Cos(th2), vy = Math.Sin(th2);
            double px2 = rx * Math.Cos(th2), py2 = ry * Math.Sin(th2);
            var c1 = new PointF(
                (float)(cx + cosR * (rx * (ux - t * uy)) - sinR * (ry * (uy + t * ux))),
                (float)(cy + sinR * (rx * (ux - t * uy)) + cosR * (ry * (uy + t * ux))));
            var c2 = new PointF(
                (float)(cx + cosR * (px2 + rx * t * vy) - sinR * (py2 - ry * t * vx)),
                (float)(cy + sinR * (px2 + rx * t * vy) + cosR * (py2 - ry * t * vx)));
            var end = new PointF((float)(cx + cosR * px2 - sinR * py2), (float)(cy + sinR * px2 + cosR * py2));
            path.AddBezier(prev, c1, c2, end);
            prev = end;
            ux = vx; uy = vy;
            th1 = th2;
        }
    }
}
