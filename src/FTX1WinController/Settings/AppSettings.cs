using System.IO;
using System.Text.Json;

namespace FTX1WinController.Settings;

/// Persists the handful of connection fields worth remembering between
/// launches (CAT-1/CAT-2 COM port) so the user doesn't have to retype them
/// every time — baud rates have a stable default and aren't worth the same
/// treatment. Plain JSON under %AppData%, not the WPF
/// Properties.Settings.settings designer file, to keep this simple and
/// hand-inspectable rather than relying on generated code that can't be
/// compiled/verified locally.
public sealed class AppSettings
{
    public string? Cat1Port { get; set; }
    public string? Cat2Port { get; set; }
    public string? AudioInputDeviceName { get; set; }

    /// SM (RF signal strength) and SQ (squelch threshold) are both 0-255
    /// CAT readings but not the same physical quantity — most Yaesu
    /// FM/AM squelch circuits gate on audio-noise energy, not raw RF
    /// signal strength, and the FTX-1 exposes no CAT command for the
    /// noise detector's actual state. So MONITOR's SM-vs-SQ approximation
    /// (MainViewModel.RefreshMetersAsync) can only ever be a rough match
    /// for the real speaker's squelch point, not an exact one — this trim
    /// (subtracted from SQ before the comparison) exists to be tuned on
    /// the bench against the real radio rather than guessed from source
    /// alone. Default of 1 is a starting point inferred from one hardware
    /// report (2026-09-08): MONITOR was muting as soon as SQL left 0,
    /// while the radio's own speaker didn't actually silence until SQL
    /// was raised to ~2 — still needs further on-radio confirmation/tuning.
    public int MonitorSquelchTrim { get; set; } = 1;

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
