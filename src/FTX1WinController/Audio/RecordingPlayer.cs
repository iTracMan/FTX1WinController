using NAudio.Wave;

namespace FTX1WinController.Audio;

/// Plays back one saved recording (see RecordingLibrary) through the
/// Windows machine's default audio output device.
public sealed class RecordingPlayer : IDisposable
{
    private WaveOutEvent? _output;
    private AudioFileReader? _reader;

    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;

    /// `onFinished` fires both when playback reaches the end of the file
    /// and when Stop() is called explicitly (WaveOutEvent's
    /// PlaybackStopped fires in both cases) — callers only use it to reset
    /// an IsPlaying flag, which is harmless to set twice.
    public void Play(string filePath, Action onFinished)
    {
        Stop();
        _reader = new AudioFileReader(filePath);
        _output = new WaveOutEvent();
        _output.Init(_reader);
        _output.PlaybackStopped += (_, _) => onFinished();
        _output.Play();
    }

    public void Stop()
    {
        _output?.Stop();
        _output?.Dispose();
        _output = null;
        _reader?.Dispose();
        _reader = null;
    }

    public void Dispose() => Stop();
}
