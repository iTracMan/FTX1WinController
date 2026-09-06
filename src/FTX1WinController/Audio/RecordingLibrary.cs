using System.IO;
using System.Linq;
using System.Text.Json;

namespace FTX1WinController.Audio;

/// One saved RECORD capture's metadata — persisted as a sidecar .json next
/// to its .wav (see RecordingLibrary), so a plain file listing can recover
/// the frequency/mode/duration without re-parsing the audio itself.
public sealed class RecordingMetadata
{
    public DateTimeOffset TimestampUtc { get; set; }
    public int FrequencyHz { get; set; }
    public string Mode { get; set; } = "";
    public double DurationSeconds { get; set; }
}

/// Local recordings made by RECORD's local-capture half (Audio/
/// LiveAudioService) — one .wav + one sidecar .json per recording, under
/// %AppData%\FTX1WinController\Recordings. Purely local Windows storage; no
/// relation to the FTX-1's own SD-card recording (LM1/SdRecording), which
/// this app has no way to browse or retrieve from over USB — the two
/// "recordings" happen to start/stop together (see MainViewModel's
/// ToggleRecordAsync) but live in entirely different places.
public static class RecordingLibrary
{
    private static string FolderPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FTX1WinController", "Recordings");

    public static string ReserveNewFilePath(out string id)
    {
        Directory.CreateDirectory(FolderPath);
        id = Guid.NewGuid().ToString("N");
        return Path.Combine(FolderPath, id + ".wav");
    }

    public static void SaveMetadata(string id, RecordingMetadata metadata)
    {
        Directory.CreateDirectory(FolderPath);
        File.WriteAllText(Path.Combine(FolderPath, id + ".json"), JsonSerializer.Serialize(metadata));
    }

    public static IReadOnlyList<(string Id, RecordingMetadata Metadata)> List()
    {
        if (!Directory.Exists(FolderPath)) return Array.Empty<(string, RecordingMetadata)>();

        var results = new List<(string, RecordingMetadata)>();
        foreach (var jsonPath in Directory.GetFiles(FolderPath, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(jsonPath);
            if (!File.Exists(Path.Combine(FolderPath, id + ".wav"))) continue;
            try
            {
                var metadata = JsonSerializer.Deserialize<RecordingMetadata>(File.ReadAllText(jsonPath));
                if (metadata != null) results.Add((id, metadata));
            }
            catch
            {
                // Skip a corrupt sidecar rather than fail the whole list.
            }
        }
        return results.OrderByDescending(r => r.Item2.TimestampUtc).ToList();
    }

    public static string WavPath(string id) => Path.Combine(FolderPath, id + ".wav");

    public static void Delete(string id)
    {
        TryDelete(Path.Combine(FolderPath, id + ".wav"));
        TryDelete(Path.Combine(FolderPath, id + ".json"));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best-effort — a failed delete shouldn't crash the app.
        }
    }
}
