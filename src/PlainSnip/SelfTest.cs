using System.Runtime.InteropServices;

namespace PlainSnip;

public static class SelfTest
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    private static int _failed;

    public static int RunAll()
    {
        // WinExe 无控制台：尝试挂到父进程终端；无论成败都落一份日志文件
        AttachConsole(-1);
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* 挂接失败则忽略 */ }

        string logPath = Path.Combine(Path.GetTempPath(), "PlainSnip-selftest.log");

        void Check(string name, bool ok)
        {
            string line = $"[{(ok ? "PASS" : "FAIL")}] {name}";
            Console.WriteLine(line);
            File.AppendAllText(logPath, line + Environment.NewLine);
            if (!ok) _failed++;
        }

        File.WriteAllText(logPath, $"PlainSnip --selftest {DateTime.Now:yyyy-MM-dd HH:mm:ss}" + Environment.NewLine);

        Check("DPI 模式 = PerMonitorV2", Application.HighDpiMode == HighDpiMode.PerMonitorV2);

        // ---- 嵌入图标资源（App.ico 多尺寸） ----
        {
            using var stream = typeof(IconFactory).Assembly.GetManifestResourceStream("PlainSnip.App.ico");
            bool ok = stream != null && stream.Length > 1000;
            if (ok)
            {
                var size = SystemInformation.SmallIconSize;
                using var icon = new Icon(stream!, size.Width, size.Height);
                ok = icon.Width > 0 && icon.Height > 0;
            }
            Check("嵌入图标资源加载（App.ico）", ok);
        }

        // ---- 热键解析 round-trip ----
        foreach (var s in new[] { "F1", "F3", "F12", "F24", "Ctrl+Alt+A", "Ctrl+Shift+P", "Alt+F4", "Win+D", "Shift+Space", "PrintScreen" })
        {
            bool ok = HotkeySpec.TryParse(s, out var spec)
                && string.Equals(spec.ToString(), s, StringComparison.OrdinalIgnoreCase)
                && HotkeySpec.TryParse(spec.ToString(), out var spec2)
                && spec.Equals(spec2);
            Check($"热键 round-trip: {s}", ok);
        }

        foreach (var s in new[] { "", "   ", "Ctrl+", "Ctrl++", "Ctrl+A+B", "Foo", "F25", "Ctrl", "Shift", "Ctrl+Ctrl+A" })
        {
            Check($"非法热键拒绝: \"{s}\"", !HotkeySpec.TryParse(s, out _));
        }

        // 大小写不敏感
        Check("热键解析大小写不敏感", HotkeySpec.TryParse("ctrl+alt+a", out var lower) && lower.ToString() == "Ctrl+Alt+A");

        // ---- 设置持久化 ----
        string tmp = Path.Combine(Path.GetTempPath(), $"PlainSnip.selftest.{Guid.NewGuid():N}.json");
        try
        {
            var s1 = new AppSettings { HotkeyCapture = "Ctrl+Alt+Q", HotkeyPin = "F9", Autostart = false };
            s1.Save(tmp);
            var s2 = AppSettings.Load(tmp);
            Check("设置 round-trip", s2.HotkeyCapture == "Ctrl+Alt+Q" && s2.HotkeyPin == "F9" && s2.SchemaVersion == AppSettings.CurrentSchemaVersion);

            File.WriteAllText(tmp, "{ this is not json");
            var s3 = AppSettings.Load(tmp);
            Check("损坏设置回退默认(F1/F3)", s3.HotkeyCapture == "F1" && s3.HotkeyPin == "F3");
        }
        finally
        {
            File.Delete(tmp);
        }

        // ---- 选区跨屏合成（ScreenCapture.ComposeSelection，M4 复用） ----
        {
            using var red = new Bitmap(100, 100);
            using (var g = Graphics.FromImage(red)) g.Clear(Color.Red);
            using var blue = new Bitmap(100, 100);
            using (var g = Graphics.FromImage(blue)) g.Clear(Color.Blue);
            var shots = new List<ScreenCapture.ScreenShot>
            {
                new(new Rectangle(0, 0, 100, 100), red),
                new(new Rectangle(100, 0, 100, 100), blue),
            };
            using var sel = ScreenCapture.ComposeSelection(shots, new Rectangle(50, 25, 100, 50));
            bool ok = sel.Width == 100 && sel.Height == 50
                && sel.GetPixel(10, 25).R > 200 && sel.GetPixel(10, 25).B < 60
                && sel.GetPixel(90, 25).B > 200 && sel.GetPixel(90, 25).R < 60;
            Check("跨屏选区合成（左右两屏拼图）", ok);
        }

        // ---- 窗口检测下钻启发式（M1b 纯函数） ----
        {
            var root = new WindowDetector.Candidate(new Rectangle(0, 0, 800, 600), IntPtr.Zero);
            var tiny = new WindowDetector.Candidate(new Rectangle(10, 10, 16, 12), IntPtr.Zero);
            var okChild = new WindowDetector.Candidate(new Rectangle(10, 10, 200, 100), IntPtr.Zero);
            Check("下钻启发式：过小块回退根窗口",
                WindowDetector.PickCandidate(new[] { root, tiny }).Bounds == root.Bounds);
            Check("下钻启发式：正常子块保留",
                WindowDetector.PickCandidate(new[] { root, okChild }).Bounds == okChild.Bounds);
            Check("下钻启发式：面积比例回退",
                WindowDetector.TooSmall(new Rectangle(0, 0, 40, 40), new Rectangle(0, 0, 800, 600)));
            Check("下钻启发式：32x24 边长阈值不算过小",
                !WindowDetector.TooSmall(new Rectangle(0, 0, 32, 24), new Rectangle(0, 0, 100, 100)));
        }

        // ---- 贴图几何（M2 纯函数） ----
        {
            Check("贴图缩放比例夹取 [0.1, 8]",
                PinGeometry.ClampScale(0.001f) == 0.1f && PinGeometry.ClampScale(99f) == 8f
                && PinGeometry.ClampScale(float.NaN) == 0.1f);

            var sz = PinGeometry.ScaledSize(new Size(200, 100), 1.5f);
            Check("贴图缩放尺寸计算", sz.Width == 300 && sz.Height == 150);

            // 光标锚点：缩放前后锚点下的相对位置不变
            var zoomed = PinGeometry.ZoomAround(new Rectangle(100, 100, 200, 100), new Size(200, 100), 2f, new Point(150, 150));
            bool anchorKept = zoomed == new Rectangle(50, 50, 400, 200)
                && Math.Abs((150 - zoomed.X) / (double)zoomed.Width - 0.25) < 1e-6
                && Math.Abs((150 - zoomed.Y) / (double)zoomed.Height - 0.5) < 1e-6;
            Check("贴图缩放锚点保持（光标下像素不动）", anchorKept);

            var (s1, b1) = PinGeometry.PlaceInitial(new Rectangle(0, 0, 1920, 1080), new Size(400, 300), new Point(960, 540));
            Check("贴图初始摆放 1:1 居中于光标",
                Math.Abs(s1 - 1f) < 1e-6 && b1 == new Rectangle(760, 390, 400, 300));

            var (s2, b2) = PinGeometry.PlaceInitial(new Rectangle(0, 0, 1920, 1080), new Size(3000, 2000), new Point(960, 540));
            Check("贴图初始摆放：超工作区 80% 等比缩小",
                s2 < 1f && b2.Width <= 1536 && b2.Height <= 864 && b2.Width > 0);

            var (_, b3) = PinGeometry.PlaceInitial(new Rectangle(0, 0, 1920, 1080), new Size(400, 300), new Point(10, 10));
            Check("贴图初始摆放：夹回工作区", b3.X == 0 && b3.Y == 0);

            Check("贴图不透明度换算", PinGeometry.ToAlphaByte(100) == 255
                && PinGeometry.ToAlphaByte(50) == 128
                && PinGeometry.ToAlphaByte(5) == 26);   // 下限 10%

            using var alpha = new Bitmap(20, 20, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(alpha)) g.Clear(Color.FromArgb(0, 255, 0, 0));
            bool detectsAlpha = PinGeometry.HasAlpha(alpha);
            using var flat = PinGeometry.Prepare(alpha, Color.Black);
            Check("贴图 alpha 检测与压平",
                detectsAlpha && !PinGeometry.HasAlpha(flat)
                && flat.GetPixel(10, 10).R < 20 && flat.GetPixel(10, 10).A == 255);
        }

        // ---- 标注历史与几何（M3 纯函数） ----
        {
            var h = new AnnotationHistory();
            var a1 = new Annotation(AnnotationTool.Rect, new[] { new Point(0, 0), new Point(10, 10) }, Color.Red, 2);
            var a2 = new Annotation(AnnotationTool.Serial, new[] { new Point(5, 5) }, Color.Blue, 24, Serial: 1);
            h.Push(a1);
            h.Push(a2);
            int v2 = h.Version;
            Check("标注历史：Push 后可撤销可重做", h.Count == 2 && h.CanUndo && !h.CanRedo);

            var s2 = new Annotation(AnnotationTool.Serial, new[] { new Point(6, 6) }, Color.Blue, 24, Serial: 2);
            h.Push(s2);
            Check("标注序号：新放置取最大号+1", h.NextSerial() == 3);
            Check("标注序号：撤销后重画复用原号（剩余最大号+1）",
                h.Undo() != null && AnnotationHistory.NextSerial(h) == 2);
            Check("标注序号：重做恢复原号", h.Redo()?.Serial == 2);

            h.Undo();
            Check("标注历史：撤销计数与版本递增", h.Count == 2 && h.Version > v2 && h.CanRedo);
            h.Push(new Annotation(AnnotationTool.Pen, new[] { new Point(0, 0), new Point(1, 1) }, Color.Red, 2));
            Check("标注历史：新提交清空 redo 栈", !h.CanRedo && h.Count == 3);

            var (lineEnd, head) = AnnotationGeometry.ArrowGeometry(new Point(0, 0), new Point(100, 0), 4);
            Check("箭头几何：主线终点与三角方向",
                lineEnd.X < 100 && head[0].X == 100 && head[1].X < 100 && head[2].X < 100
                && head[1].Y != head[2].Y);

            var (shortEnd, shortHead) = AnnotationGeometry.ArrowGeometry(new Point(0, 0), new Point(6, 0), 12);
            Check("箭头几何：短箭头收缩头长不越界", shortEnd.X >= 0 && shortHead[0].X == 6);

            Check("马赛克块大小下限", AnnotationGeometry.NormalizeBlock(0) == 4 && AnnotationGeometry.NormalizeBlock(20) == 20);

            // 渲染器烟囱：在合成位图上画全部种类不抛异常，输出尺寸 = 选区尺寸
            var recs = new List<Annotation>
            {
                a1, a2,
                new(AnnotationTool.Ellipse, new[] { new Point(0, 0), new Point(20, 20) }, Color.Green, 2),
                new(AnnotationTool.Arrow, new[] { new Point(0, 0), new Point(30, 30) }, Color.White, 3),
                new(AnnotationTool.Pen, new[] { new Point(0, 0), new Point(5, 5), new Point(10, 0) }, Color.Yellow, 2),
                new(AnnotationTool.Text, new[] { new Point(2, 2) }, Color.Red, 12, Text: "测"),
            };
            using var canvas = new Bitmap(40, 40, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(canvas))
                AnnotationRenderer.Draw(g, recs, _ => new Bitmap(1, 1));
            Check("标注渲染器：全部种类可绘制", true);
        }

        // ---- 开机自启注册表可读（只读，不写） ----
        try
        {
            bool _ = Autostart.IsEnabled();
            Check("开机自启注册表可读", true);
        }
        catch (Exception ex)
        {
            Check("开机自启注册表可读", false);
            File.AppendAllText(logPath, "  Autostart.IsEnabled 异常: " + ex.Message + Environment.NewLine);
        }

        Console.WriteLine($"总计失败: {_failed}");
        File.AppendAllText(logPath, $"总计失败: {_failed}" + Environment.NewLine);
        return _failed == 0 ? 0 : 1;
    }
}
