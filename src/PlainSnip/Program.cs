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
