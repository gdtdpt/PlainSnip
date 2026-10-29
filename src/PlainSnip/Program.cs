namespace PlainSnip;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        string mode = args.Length > 0 ? args[0].TrimStart('-').ToLowerInvariant() : "";

        switch (mode)
        {
            case "selftest":
                // 无界面自测：退出码 0 = 全部通过
                return SelfTest.RunAll();

            case "settings":
                // 独立设置窗：不占单实例互斥体、不建托盘，可与常驻实例并存（UI 冒烟入口）
                Application.Run(new SettingsForm(AppSettings.Load()));
                return 0;

            case "shot":
                // UI 冒烟入口：自动框选 + 6 秒自动退出，供截图脚本验证遮罩 UI
                var shotSession = CaptureSession.Start(debug: true);
                if (shotSession?.InteractiveForm != null)
                    Application.Run(shotSession.InteractiveForm);
                return 0;

            case "hover":
                // UI 冒烟入口：按光标位置悬停预选 + 6 秒自动退出，验证 M1b 高亮渲染
                var hoverSession = CaptureSession.Start(debugHover: true);
                if (hoverSession?.InteractiveForm != null)
                    Application.Run(hoverSession.InteractiveForm);
                return 0;

            case "pin":
                // UI 冒烟入口：造一张合成图钉到屏幕中央（带边框与角标），8 秒后自动退出
                return RunPinSmoke();

            default:
                break;
        }

        // 常驻托盘：单实例互斥
        using var mutex = new Mutex(initiallyOwned: true, name: @"Local\PlainSnip.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("PlainSnip 已经在运行中。", "PlainSnip",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        Application.Run(new AppContext(AppSettings.Load()));
        return 0;
    }

    /// <summary>--pin 冒烟：合成一张带文字的测试图，钉在屏幕中央，定时自动退出。</summary>
    private static int RunPinSmoke()
    {
        var work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        int w = Math.Min(760, work.Width / 2), h = Math.Min(460, work.Height / 2);

        var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            using var bg = new System.Drawing.Drawing2D.LinearGradientBrush(
                new Rectangle(0, 0, w, h), Color.FromArgb(38, 44, 58), Color.FromArgb(18, 20, 26), 45f);
            g.FillRectangle(bg, 0, 0, w, h);
            using var grid = new Pen(Color.FromArgb(60, 120, 160, 200), 1f);
            for (int x = 0; x < w; x += 40) g.DrawLine(grid, x, 0, x, h);
            for (int y = 0; y < h; y += 40) g.DrawLine(grid, 0, y, w, y);
            using var font = new Font("Microsoft YaHei UI", 20f, FontStyle.Bold);
            TextRenderer.DrawText(g, "PlainSnip 贴图", font, new Rectangle(0, h / 2 - 40, w, 50),
                Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            using var small = new Font("Consolas", 11f);
            TextRenderer.DrawText(g, $"pin smoke · {w}x{h}", small, new Rectangle(0, h / 2 + 10, w, 30),
                Color.FromArgb(150, 220, 230, 245), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        var pin = PinManager.Current.Create(bmp, new Point(work.X + work.Width / 2, work.Y + work.Height / 2), debugChrome: true);
        bmp.Dispose();
        if (pin == null) return 1;

        var timer = new System.Windows.Forms.Timer { Interval = 8000 };
        timer.Tick += (_, _) => { timer.Stop(); timer.Dispose(); pin.Close(); };
        timer.Start();

        Application.Run(pin);
        return 0;
    }
}
