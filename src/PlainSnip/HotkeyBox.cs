namespace PlainSnip;

/// <summary>
/// 按键捕获文本框：聚焦后直接按键即录入热键。
/// Esc 清空；纯修饰键忽略；录入结果以 "Ctrl+Alt+A" 形式展示。
/// </summary>
public sealed class HotkeyBox : TextBox
{
    public HotkeySpec? Spec { get; set; }

    public HotkeyBox()
    {
        ReadOnly = true;            // 只允许按键录入，不允许文本编辑
        BackColor = SystemColors.Window;
        TextAlign = HorizontalAlignment.Center;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.SuppressKeyPress = true;
        e.Handled = true;

        Keys keyCode = e.KeyCode;
        if (keyCode == Keys.Escape)
        {
            Spec = null;
            Text = "";
            return;
        }
        // 纯修饰键按下不构成热键
        if (keyCode is Keys.ControlKey or Keys.Menu or Keys.ShiftKey
            or Keys.LWin or Keys.RWin)
        {
            return;
        }

        uint mod = 0;
        if (e.Control) mod |= HotkeySpec.MOD_CONTROL;
        if (e.Alt) mod |= HotkeySpec.MOD_ALT;
        if (e.Shift) mod |= HotkeySpec.MOD_SHIFT;
        if (e.KeyCode.HasFlag(Keys.LWin) || e.KeyCode.HasFlag(Keys.RWin)) mod |= HotkeySpec.MOD_WIN;

        var spec = new HotkeySpec(mod, (uint)keyCode);
        Spec = spec;
        Text = spec.ToString();
    }

    protected override bool IsInputKey(Keys keyData) => true;
}
