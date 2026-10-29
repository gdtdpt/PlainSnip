namespace PlainSnip;

/// <summary>
/// 贴图注册表（§5.7）：进程内所有贴图窗的唯一管理者。
/// 托盘菜单按此列表动态生成「贴图 #n」项、关闭全部、取消全部穿透。
/// </summary>
public sealed class PinManager : IDisposable
{
    private readonly List<PinWindow> _pins = new();

    public static PinManager Current { get; } = new();

    public IReadOnlyList<PinWindow> Pins => _pins;
    public int Count => _pins.Count;
    public bool AnyPassthrough => _pins.Any(p => p.Passthrough);

    /// <summary>
    /// 从位图创建贴图（内部复制一份，调用方可随即释放 source）。
    /// anchor 为物理屏幕坐标，缺省用光标位置。
    /// </summary>
    public PinWindow? Create(Bitmap source, Point? anchor = null, bool debugChrome = false)
    {
        if (source.Width < 1 || source.Height < 1) return null;

        // 带 alpha 的图先压到不透明底（LWA_ALPHA 不支持逐像素透明，§5.7）
        var img = PinGeometry.Prepare(source, Color.FromArgb(255, 30, 30, 32));
        var cursor = anchor ?? Cursor.Position;
        var work = Screen.FromPoint(cursor).WorkingArea;
        var (scale, bounds) = PinGeometry.PlaceInitial(work, img.Size, cursor);

        var pin = new PinWindow(img, scale, bounds) { DebugChrome = debugChrome };
        pin.PinClosed += p => _pins.Remove(p);
        _pins.Add(pin);
        pin.Show();
        return pin;
    }

    public void CloseAll()
    {
        foreach (var pin in _pins.ToArray()) pin.Close();
        _pins.Clear();
    }

    public void ResetAllPassthrough()
    {
        foreach (var pin in _pins) pin.SetPassthrough(false);
    }

    public void Dispose()
    {
        foreach (var pin in _pins.ToArray())
        {
            if (!pin.IsDisposed) pin.Close();
        }
        _pins.Clear();
    }
}
