namespace PlainSnip;

/// <summary>
/// 托盘常驻主上下文：无主窗体，NotifyIcon 是唯一常驻 UI。
/// 单实例由 Program.Main 的命名 Mutex 保证。
/// </summary>
public sealed class AppContext : ApplicationContext
{
    private const int HotkeyIdCapture = 1;
    private const int HotkeyIdPin = 2;

    private readonly NotifyIcon _tray;
    private readonly HotkeyManager _hotkeys = new();
    private AppSettings _settings;
    private SettingsForm? _settingsForm;

    public AppContext(AppSettings settings)
    {
        _settings = settings;

        var menu = new ContextMenuStrip();
        menu.Items.Add("截图(&C)", null, (_, _) => DoCapture());
        menu.Items.Add("贴图剪贴板(&P)", null, (_, _) => DoPin());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("设置(&S)…", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出(&X)", null, (_, _) => ExitApp());

        _tray = new NotifyIcon
        {
            Icon = IconFactory.CreateTrayIcon(),
            Text = "PlainSnip — 截图 / 贴图",
            ContextMenuStrip = menu,
            Visible = true,
        };

        ApplyHotkeys();
    }

    /// <summary>按当前设置注册热键；失败则气泡提示并打开设置窗。</summary>
    private void ApplyHotkeys()
    {
        _hotkeys.UnregisterAll();
        bool okCapture = TryRegister(HotkeyIdCapture, _settings.HotkeyCapture, DoCapture);
        bool okPin = TryRegister(HotkeyIdPin, _settings.HotkeyPin, DoPin);
        if (!okCapture || !okPin)
        {
            _tray.ShowBalloonTip(5000, "PlainSnip",
                "热键注册失败（可能被其他程序占用），请在设置中更换热键。",
                ToolTipIcon.Warning);
            ShowSettings();
        }
    }

    private bool TryRegister(int id, string text, Action handler)
    {
        if (!HotkeySpec.TryParse(text, out var spec)) return false;
        return _hotkeys.Register(id, spec, handler);
    }

    private void DoCapture()
    {
        // M0 占位：M1（截图核心）在此实装全屏遮罩 + 框选
        _tray.ShowBalloonTip(2000, "PlainSnip", "截图功能将在 M1（截图核心）提供。", ToolTipIcon.Info);
    }

    private void DoPin()
    {
        // M0 占位：M2（贴图）在此实装置顶悬浮窗
        if (Clipboard.ContainsImage())
            _tray.ShowBalloonTip(2000, "PlainSnip", "贴图功能将在 M2（贴图）提供。", ToolTipIcon.Info);
        else
            _tray.ShowBalloonTip(2000, "PlainSnip", "剪贴板中没有图片。", ToolTipIcon.Info);
    }

    public void ShowSettings()
    {
        if (_settingsForm != null && !_settingsForm.IsDisposed)
        {
            _settingsForm.Activate();
            return;
        }
        _settingsForm = new SettingsForm(_settings);
        _settingsForm.SettingsSaved += s =>
        {
            _settings = s;
            ApplyHotkeys();
        };
        _settingsForm.Show();
    }

    private void ExitApp()
    {
        _hotkeys.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        Application.Exit();
    }
}
