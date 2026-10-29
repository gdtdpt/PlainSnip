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
}
