namespace PlainSnip;

/// <summary>
/// 设置窗：两个热键捕获框 + 开机自启开关。
/// 既被托盘上下文打开（保存后回调重挂热键），也可被 `--settings` 独立打开（仅落盘）。
///
/// DPI 约定（§5.1）：PMV2 下 GDI 字体随窗口 DPI 自动放大，但代码里写的布局尺寸是
/// 物理像素、不会自动缩放——必须统一乘 DeviceDpi/96（S() 助手），否则 200% 屏上
/// 出现「大字塞进小布局」的截断。不使用 AutoScaleMode：纯代码窗体上它不生效，
/// 且与手动缩放叠加有双重缩放风险。
/// </summary>
public sealed class SettingsForm : Form
{
    /// <summary>保存成功后触发（托盘模式用于重挂热键）。</summary>
    public event Action<AppSettings>? SettingsSaved;

    private readonly HotkeyBox _boxCapture = new();
    private readonly HotkeyBox _boxPin = new();
    private readonly CheckBox _chkAutostart = new();
    private readonly Button _btnOk = new();
    private readonly Button _btnCancel = new();
    private readonly Icon _icon = IconFactory.CreateTrayIcon(); // 标题栏/任务栏图标，关闭时释放

    public SettingsForm(AppSettings settings)
    {
        AutoScaleMode = AutoScaleMode.None; // 布局缩放由 S() 手动负责

        Icon = _icon;
        Text = "PlainSnip 设置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(S(420), S(200));
        ShowInTaskbar = true;

        int colLabel = S(130);
        int rowH = S(40);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(S(14)),
            ColumnCount = 2,
            RowCount = 4,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, colLabel));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, rowH));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, rowH));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, rowH));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        AddRow(layout, 0, "截图热键：", _boxCapture);
        AddRow(layout, 1, "贴图热键：", _boxPin);

        _chkAutostart.Text = "开机自动启动 PlainSnip";
        _chkAutostart.Checked = Autostart.IsEnabled();
        _chkAutostart.Anchor = AnchorStyles.Left;
        _chkAutostart.AutoSize = true;
        layout.Controls.Add(new Label
        {
            Text = "启动：",
            AutoSize = false,
            Size = new Size(colLabel - S(6), S(26)),
            TextAlign = ContentAlignment.MiddleLeft,
            Anchor = AnchorStyles.Left,
        }, 0, 2);
        layout.Controls.Add(_chkAutostart, 1, 2);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
        };
        _btnOk.Text = "保存";
        _btnOk.MinimumSize = new Size(S(88), S(30));
        _btnCancel.Text = "取消";
        _btnCancel.MinimumSize = new Size(S(88), S(30));
        _btnOk.Click += (_, _) => OnSave();
        _btnCancel.Click += (_, _) => Close();
        buttons.Controls.Add(_btnOk);
        buttons.Controls.Add(_btnCancel);
        layout.Controls.Add(buttons, 1, 3);

        Controls.Add(layout);
        AcceptButton = _btnOk;
        CancelButton = _btnCancel;

        if (HotkeySpec.TryParse(settings.HotkeyCapture, out var specCapture))
        {
            _boxCapture.Spec = specCapture;
            _boxCapture.Text = specCapture.ToString();
        }
        if (HotkeySpec.TryParse(settings.HotkeyPin, out var specPin))
        {
            _boxPin.Spec = specPin;
            _boxPin.Text = specPin.ToString();
        }
    }

    /// <summary>逻辑像素（96dpi 基准）→ 物理像素。</summary>
    private int S(int logical) => logical * DeviceDpi / 96;

    private void AddRow(TableLayoutPanel layout, int row, string label, Control ctrl)
    {
        // CJK 文本的 AutoSize 测量会偏小导致截断，用固定宽度 + MiddleLeft
        var lbl = new Label
        {
            Text = label,
            AutoSize = false,
            Size = new Size((int)layout.ColumnStyles[0].Width - S(6), S(26)),
            TextAlign = ContentAlignment.MiddleLeft,
            Anchor = AnchorStyles.Left,
        };
        layout.Controls.Add(lbl, 0, row);
        ctrl.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        ctrl.Margin = new Padding(0, S(8), 0, S(8));
        layout.Controls.Add(ctrl, 1, row);
    }

    private void OnSave()
    {
        if (_boxCapture.Spec == null || _boxPin.Spec == null)
        {
            MessageBox.Show(this, "请先录入截图热键和贴图热键（点击输入框后直接按键）。", "PlainSnip",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_boxCapture.Spec.Value.Equals(_boxPin.Spec.Value))
        {
            MessageBox.Show(this, "截图热键和贴图热键不能相同。", "PlainSnip",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            Autostart.Set(_chkAutostart.Checked);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "开机自启设置失败：" + ex.Message, "PlainSnip",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var saved = new AppSettings
        {
            SchemaVersion = AppSettings.CurrentSchemaVersion,
            HotkeyCapture = _boxCapture.Spec.Value.ToString(),
            HotkeyPin = _boxPin.Spec.Value.ToString(),
            Autostart = _chkAutostart.Checked,
        };
        saved.Save();
        SettingsSaved?.Invoke(saved);
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        _icon.Dispose();
    }
}
