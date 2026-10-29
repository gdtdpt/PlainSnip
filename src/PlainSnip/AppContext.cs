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
    private readonly PinManager _pins = PinManager.Current;
    private readonly ToolStripMenuItem _pinMenu;
    private AppSettings _settings;
    private SettingsForm? _settingsForm;

    public AppContext(AppSettings settings)
    {
        _settings = settings;

        _pinMenu = new ToolStripMenuItem("贴图列表(&I)");
        _pinMenu.DropDownOpening += (_, _) => RebuildPinMenu();

        var menu = new ContextMenuStrip();
        menu.Items.Add("截图(&C)", null, (_, _) => DoCapture());
        menu.Items.Add("贴图剪贴板(&P)", null, (_, _) => DoPin());
        menu.Items.Add(_pinMenu);
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
        // M1a：快照冻结 + per-monitor 遮罩框选（§5.5）
        CaptureSession.Start();
    }

    private void DoPin()
    {
        // M2（§5.7）：把剪贴板图钉成置顶悬浮窗
        var img = GetClipboardImage();
        if (img == null)
        {
            _tray.ShowBalloonTip(2000, "PlainSnip", "剪贴板中没有图片。", ToolTipIcon.Info);
            return;
        }
        using (img) _pins.Create(img, Cursor.Position);
    }

    /// <summary>剪贴板取图（剪贴板可能被其他进程短暂占用，短重试）。</summary>
    private static Bitmap? GetClipboardImage()
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                if (!Clipboard.ContainsImage()) return null;
                if (Clipboard.GetImage() is Bitmap bmp) return bmp;
                using var img = Clipboard.GetImage();
                return img == null ? null : new Bitmap(img);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                Thread.Sleep(50);
            }
        }
        return null;
    }

    /// <summary>托盘「贴图列表」：每次展开时按当前贴图重建。</summary>
    private void RebuildPinMenu()
    {
        _pinMenu.DropDownItems.Clear();

        var closeAll = new ToolStripMenuItem("关闭全部贴图") { Enabled = _pins.Count > 0 };
        closeAll.Click += (_, _) => _pins.CloseAll();
        _pinMenu.DropDownItems.Add(closeAll);

        var unthrough = new ToolStripMenuItem("取消全部穿透") { Enabled = _pins.AnyPassthrough };
        unthrough.Click += (_, _) => _pins.ResetAllPassthrough();
        _pinMenu.DropDownItems.Add(unthrough);

        if (_pins.Count == 0)
        {
            _pinMenu.DropDownItems.Add(new ToolStripSeparator());
            _pinMenu.DropDownItems.Add(new ToolStripMenuItem("（暂无贴图）") { Enabled = false });
            return;
        }

        _pinMenu.DropDownItems.Add(new ToolStripSeparator());
        for (int i = 0; i < _pins.Count; i++)
        {
            var pin = _pins.Pins[i];
            string title = $"{i + 1}. {pin.ImageSize.Width} × {pin.ImageSize.Height}"
                + (pin.Passthrough ? "（已穿透）" : "");
            var item = new ToolStripMenuItem(title);
            item.Click += (_, _) => pin.BringToFrontTop();
            _pinMenu.DropDownItems.Add(item);
        }
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
        _pins.CloseAll();
        _hotkeys.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        Application.Exit();
    }
}
