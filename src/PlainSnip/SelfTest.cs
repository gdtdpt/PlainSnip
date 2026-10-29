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
