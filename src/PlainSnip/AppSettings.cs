using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlainSnip;

/// <summary>
/// 设置持久化：System.Text.Json（.NET 内置）写 %APPDATA%\PlainSnip\settings.json。
/// SchemaVersion 预留，未来字段迁移用。
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string HotkeyCapture { get; set; } = "F1";
    public string HotkeyPin { get; set; } = "F3";
    public bool Autostart { get; set; }

    [JsonIgnore]
    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PlainSnip", "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultFilePath;
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOpts);
                if (s != null) return s;
            }
        }
        catch
        {
            // 设置文件损坏：回退默认，不崩托盘
        }
        return new AppSettings();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    }
}
