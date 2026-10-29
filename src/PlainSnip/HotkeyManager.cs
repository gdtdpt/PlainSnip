using System.Runtime.InteropServices;

namespace PlainSnip;

/// <summary>
/// 用隐藏消息窗承载 RegisterHotKey / WM_HOTKEY。
/// 纯 Win32 正路，不建可见窗口也能收热键消息。
/// </summary>
public sealed class HotkeyManager : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly Dictionary<int, Action> _handlers = new();

    public HotkeyManager()
    {
        CreateHandle(new CreateParams());
    }

    /// <summary>注册热键；失败（被占用等）返回 false，由调用方决定提示方式。</summary>
    public bool Register(int id, HotkeySpec spec, Action handler)
    {
        if (!RegisterHotKey(Handle, id, spec.RegisterModifiers, spec.VirtualKey))
            return false;
        _handlers[id] = handler;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (int id in _handlers.Keys)
            UnregisterHotKey(Handle, id);
        _handlers.Clear();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && _handlers.TryGetValue(m.WParam.ToInt32(), out var handler))
        {
            handler();
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose() => UnregisterAll();
}
