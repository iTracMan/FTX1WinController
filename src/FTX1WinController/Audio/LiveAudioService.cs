using System.Threading;
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

    /// Set by MainViewModel from its existing SMeter/Squelch poll data — the
    /// radio's own speaker is silenced by SQL, but this app's local capture
    /// is a raw USB Audio Class passthrough with no idea of squelch state on
    /// its own, so MONITOR needs to be told separately. Recording is
    /// deliberately unaffected — RECORD-to-file should keep everything.
    public bool MonitorMuted { get; set; }

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
        var waveOut = _waveOut;
        _waveOut = null;
        _monitorBuffer = null;
        IsMonitoring = false;
        try
        {
            if (waveOut != null)
            {
                // WaveOutEvent.Stop() only signals its background playback
                // thread to wind down and close the native device handle —
                // it doesn't happen inside this call. Called on app
                // shutdown (MainViewModel.Dispose, from Window.Closing),
                // the process can exit and kill that thread before it gets
                // there, leaving the USB Audio Class device open and the
                // radio's audio still coming out of the speakers after the
                // window has already closed. Block briefly on
                // PlaybackStopped (which the thread raises right before
                // exiting) so the native handle is actually released before
                // this returns; a bounded wait so a wedged driver can't
                // hang shutdown indefinitely.
                using var stopped = new ManualResetEventSlim(false);
                waveOut.PlaybackStopped += (_, _) => stopped.Set();
                waveOut.Stop();
                stopped.Wait(TimeSpan.FromMilliseconds(500));
                waveOut.Dispose();
            }
        }
        catch (Exception ex)
        {
            // MME teardown on some USB Audio Class codecs (this app's
            // whole reason for existing) has been observed to throw here
            // rather than fail cleanly — the app's local state above is
            // already updated, so surface it as a caught error instead of
            // letting NAudio's internal thread take the process down.
            CrashLogger.Log("LiveAudioService.StopMonitoring", ex);
        }
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
        var waveIn = _waveIn;
        _waveIn = null;
        waveIn.DataAvailable -= OnDataAvailable;
        try
        {
            waveIn.StopRecording();
            waveIn.Dispose();
        }
        catch (Exception ex)
        {
            CrashLogger.Log("LiveAudioService.StopCaptureIfIdle", ex);
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!MonitorMuted) _monitorBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
        _recordingWriter?.Write(e.Buffer, 0, e.BytesRecorded);
    }

    public void Dispose()
    {
        StopMonitoring();
        _ = StopRecording();
    }
}
