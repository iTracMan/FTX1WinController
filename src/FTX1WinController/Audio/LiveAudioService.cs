using NAudio.Wave;

namespace FTX1WinController.Audio;

/// Captures audio from the FTX-1's USB audio input device — a plain USB
/// Audio Class device once the cable's plugged into this Windows machine,
/// completely separate from the CAT serial link, no CAT command involved —
/// and either plays it live through the Windows machine's default output
/// device (Live Monitor) or writes it to a WAV file (Record), independently
/// or both at once, sharing one capture stream.
///
/// Uses NAudio's MME-based WaveInEvent/WaveOutEvent rather than WASAPI —
/// this doesn't need WASAPI's lower latency for a listen-to-the-radio use
/// case, and MME's plain integer device index is simpler to work with than
/// WASAPI's MMDevice GUIDs.
///
/// The input device is looked up by name (not a cached index) every time
/// capture starts, since device indices aren't stable across USB
/// reconnects/reboots but a device's product name is.
public sealed class LiveAudioService : IDisposable
{
    private WaveInEvent? _waveIn;
    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _monitorBuffer;
    private WaveFileWriter? _recordingWriter;
    private DateTime _recordingStartedUtc;

    public string? SelectedInputDeviceName { get; set; }

    public bool IsMonitoring { get; private set; }
    public bool IsRecording { get; private set; }

    public static IReadOnlyList<(int Index, string Name)> GetInputDevices()
    {
        var devices = new List<(int, string)>();
        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
            devices.Add((i, WaveInEvent.GetCapabilities(i).ProductName));
        return devices;
    }

    public void StartMonitoring()
    {
        if (IsMonitoring) return;
        EnsureCaptureStarted();
        _monitorBuffer = new BufferedWaveProvider(_waveIn!.WaveFormat) { DiscardOnBufferOverflow = true };
        _waveOut = new WaveOutEvent();
        _waveOut.Init(_monitorBuffer);
        _waveOut.Play();
        IsMonitoring = true;
    }

    public void StopMonitoring()
    {
        if (!IsMonitoring) return;
        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;
        _monitorBuffer = null;
        IsMonitoring = false;
        StopCaptureIfIdle();
    }

    public void StartRecording(string filePath)
    {
        if (IsRecording) return;
        EnsureCaptureStarted();
        _recordingWriter = new WaveFileWriter(filePath, _waveIn!.WaveFormat);
        _recordingStartedUtc = DateTime.UtcNow;
        IsRecording = true;
    }

    public TimeSpan StopRecording()
    {
        if (!IsRecording) return TimeSpan.Zero;
        var duration = DateTime.UtcNow - _recordingStartedUtc;
        _recordingWriter?.Flush();
        _recordingWriter?.Dispose();
        _recordingWriter = null;
        IsRecording = false;
        StopCaptureIfIdle();
        return duration;
    }

    private void EnsureCaptureStarted()
    {
        if (_waveIn != null) return;
        if (string.IsNullOrEmpty(SelectedInputDeviceName))
            throw new InvalidOperationException("No audio input device selected — pick one in Settings first.");

        var index = GetInputDevices()
            .Where(d => d.Name == SelectedInputDeviceName)
            .Select(d => (int?)d.Index)
            .FirstOrDefault();
        if (index is not { } deviceIndex)
            throw new InvalidOperationException($"Audio input device '{SelectedInputDeviceName}' not found — reselect it in Settings.");

        _waveIn = new WaveInEvent { DeviceNumber = deviceIndex, WaveFormat = new WaveFormat(44100, 1) };
        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.StartRecording();
    }

    private void StopCaptureIfIdle()
    {
        if (IsMonitoring || IsRecording || _waveIn == null) return;
        _waveIn.DataAvailable -= OnDataAvailable;
        _waveIn.StopRecording();
        _waveIn.Dispose();
        _waveIn = null;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        _monitorBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
        _recordingWriter?.Write(e.Buffer, 0, e.BytesRecorded);
    }

    public void Dispose()
    {
        StopMonitoring();
        _ = StopRecording();
    }
}
