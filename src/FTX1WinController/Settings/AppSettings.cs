using System.IO;
using System.Text.Json;

namespace FTX1WinController.Settings;

/// Persists the handful of connection fields worth remembering between
/// launches (bridge host, CAT-1/CAT-2 port) so the user doesn't have to
/// retype them every time — everything else (baud rates, the bridge's own
/// TCP port) has a stable default and isn't worth the same treatment.
/// Plain JSON under %AppData%, not the WPF Properties.Settings.settings
/// designer file, to keep this simple and hand-inspectable rather than
/// relying on generated code that can't be compiled/verified locally.
public sealed class AppSettings
{
    public string? BridgeHost { get; set; }
    public string? Cat1Port { get; set; }
    public string? Cat2Port { get; set; }
    public List<PresetData> Presets { get; set; } = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FTX1WinController", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null) return settings;
            }
        }
        catch
        {
            // Corrupt/unreadable settings file — fall back to defaults
            // rather than blocking startup.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch
        {
            // Best-effort — a failed save shouldn't crash the app.
        }
    }
}
