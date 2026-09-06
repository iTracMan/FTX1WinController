using System.IO;

namespace FTX1WinController;

/// Last-resort crash diagnostics. There is otherwise nothing catching
/// exceptions thrown off the UI thread (e.g. NAudio's internal
/// playback/capture threads), and .NET terminates the whole process for
/// those with no dialog and no Windows Event Log entry a user could hand
/// back to us — so this is the only place such a failure gets recorded.
/// Plain text under %AppData%, next to AppSettings/Recordings.
public static class CrashLogger
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FTX1WinController", "crash.log");

    public static void Log(string source, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.AppendAllText(FilePath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging the crash must never itself throw during a crash.
        }
    }
}
