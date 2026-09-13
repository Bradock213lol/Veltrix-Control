using System.Text.Json;
using System.IO;

namespace VeltrixControl.Desktop;

public sealed class DesktopSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string ControllerUrl { get; set; } = "http://localhost:5187/";
    public int RefreshSeconds { get; set; } = 5;
    public bool AutoRefresh { get; set; } = true;

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Veltrix-Control",
        "desktop-settings.json");

    public static DesktopSettings Load()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new DesktopSettings()
                : new DesktopSettings();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new DesktopSettings();
        }
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
