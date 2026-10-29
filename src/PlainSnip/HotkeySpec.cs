namespace PlainSnip;

/// <summary>
/// 热键规格：修饰键 + 虚拟键码。支持与字符串互转（"F1"、"Ctrl+Alt+A"），
/// 解析/序列化是热键配置持久化的唯一通道，--selftest 覆盖 round-trip。
/// </summary>
public readonly struct HotkeySpec : IEquatable<HotkeySpec>
{
    public const uint MOD_ALT = 0x1;
    public const uint MOD_CONTROL = 0x2;
    public const uint MOD_SHIFT = 0x4;
    public const uint MOD_WIN = 0x8;
    public const uint MOD_NOREPEAT = 0x4000;

    /// <summary>修饰键组合（不含 MOD_NOREPEAT）。</summary>
    public uint Modifiers { get; }

    /// <summary>Win32 虚拟键码。</summary>
    public uint VirtualKey { get; }

    public HotkeySpec(uint modifiers, uint virtualKey)
    {
        Modifiers = modifiers & ~MOD_NOREPEAT;
        VirtualKey = virtualKey;
    }

    /// <summary>注册到 RegisterHotKey 时用的 fsModifiers（附加 NOREPEAT）。</summary>
    public uint RegisterModifiers => Modifiers | MOD_NOREPEAT;

    private static readonly Dictionary<string, uint> ModMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = MOD_CONTROL, ["Control"] = MOD_CONTROL,
        ["Alt"] = MOD_ALT,
        ["Shift"] = MOD_SHIFT,
        ["Win"] = MOD_WIN, ["Windows"] = MOD_WIN,
    };

    private static readonly Dictionary<string, uint> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20, ["Enter"] = 0x0D, ["Esc"] = 0x1B, ["Tab"] = 0x09,
        ["PrintScreen"] = 0x2C, ["Insert"] = 0x2D, ["Delete"] = 0x2E,
        ["Home"] = 0x24, ["End"] = 0x23, ["PgUp"] = 0x21, ["PgDn"] = 0x22,
        ["Up"] = 0x26, ["Down"] = 0x28, ["Left"] = 0x25, ["Right"] = 0x27,
    };

    private static readonly Dictionary<uint, string> NamedKeyNames = NamedKeys
        .GroupBy(kv => kv.Value)
        .ToDictionary(g => g.Key, g => g.First().Key);

    public static bool TryParse(string? text, out HotkeySpec spec)
    {
        spec = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        uint mod = 0;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!ModMap.TryGetValue(parts[i], out uint m)) return false;   // 修饰键位置出现非修饰键
            if ((mod & m) != 0) return false;                               // 重复修饰键
            mod |= m;
        }

        string keyPart = parts[^1];
        // 单独一个修饰键不是热键
        if (parts.Length == 1 && ModMap.ContainsKey(keyPart)) return false;
        if (!TryMapKey(keyPart, out uint vk)) return false;

        spec = new HotkeySpec(mod, vk);
        return true;
    }

    private static bool TryMapKey(string key, out uint vk)
    {
        vk = 0;
        if (key.Length == 1)
        {
            char c = char.ToUpperInvariant(key[0]);
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) { vk = c; return true; }
            return false;
        }
        // F1-F24：VK_F1 = 0x70
        if ((key[0] == 'F' || key[0] == 'f') && key.Length >= 2 && key.Length <= 3
            && int.TryParse(key.AsSpan(1), out int f) && f >= 1 && f <= 24)
        {
            vk = (uint)(0x70 + f - 1);
            return true;
        }
        return NamedKeys.TryGetValue(key, out vk);
    }

    public override string ToString()
    {
        var sb = new System.Text.StringBuilder();
        if ((Modifiers & MOD_CONTROL) != 0) sb.Append("Ctrl+");
        if ((Modifiers & MOD_ALT) != 0) sb.Append("Alt+");
        if ((Modifiers & MOD_SHIFT) != 0) sb.Append("Shift+");
        if ((Modifiers & MOD_WIN) != 0) sb.Append("Win+");
        sb.Append(KeyName(VirtualKey));
        return sb.ToString();
    }

    private static string KeyName(uint vk)
    {
        if (vk >= 'A' && vk <= 'Z') return ((char)vk).ToString();
        if (vk >= 0x70 && vk <= 0x87) return $"F{vk - 0x70 + 1}";
        if (NamedKeyNames.TryGetValue(vk, out var name)) return name;
        return $"VK_{vk:X2}";
    }

    public bool Equals(HotkeySpec other) => Modifiers == other.Modifiers && VirtualKey == other.VirtualKey;
    public override bool Equals(object? obj) => obj is HotkeySpec other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Modifiers, VirtualKey);
}
