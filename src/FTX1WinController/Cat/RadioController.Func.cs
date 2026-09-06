namespace FTX1WinController.Cat;

/// v3: FUNC-knob quick-menu state and methods. Ported from the Mac app's
/// RadioControllerV3.swift.
public sealed partial class RadioController
{
    // MARK: - Spectrum Scope quick items (SS)

    private int _scopePeakLevel;
    public int ScopePeakLevel { get => _scopePeakLevel; private set => SetProperty(ref _scopePeakLevel, value); }

    public Task SetScopePeakAsync(int level) =>
        SendSetAsync(CatCommands.SetScopePeak(level), () => ScopePeakLevel = CatCommands.Clamp(level, 0, 4));

    public Task RefreshScopePeakAsync() =>
        FetchAndApplyAsync(CatCommands.ReadScopePeak(), CatCommands.ParseScopePeak, v => ScopePeakLevel = v);

    private bool _scopeMarkerOn;
    public bool ScopeMarkerOn { get => _scopeMarkerOn; private set => SetProperty(ref _scopeMarkerOn, value); }

    public Task SetScopeMarkerAsync(bool on) => SendSetAsync(CatCommands.SetScopeMarker(on), () => ScopeMarkerOn = on);

    public Task RefreshScopeMarkerAsync() =>
        FetchAndApplyAsync(CatCommands.ReadScopeMarker(), CatCommands.ParseScopeMarker, v => ScopeMarkerOn = v);

    private int _scopeColorIndex;
    public int ScopeColorIndex { get => _scopeColorIndex; private set => SetProperty(ref _scopeColorIndex, value); }

    public Task SetScopeColorAsync(int index) =>
        SendSetAsync(CatCommands.SetScopeColor(index), () => ScopeColorIndex = CatCommands.Clamp(index, 0, 10));

    public Task RefreshScopeColorAsync() =>
        FetchAndApplyAsync(CatCommands.ReadScopeColor(), CatCommands.ParseScopeColor, v => ScopeColorIndex = v);

    private double _scopeLevelDb;
    public double ScopeLevelDb { get => _scopeLevelDb; private set => SetProperty(ref _scopeLevelDb, value); }

    public Task SetScopeLevelAsync(double db) =>
        SendSetAsync(CatCommands.SetScopeLevel(db), () => ScopeLevelDb = Math.Max(-30, Math.Min(30, db)));

    public async Task RefreshScopeLevelAsync()
    {
        string reply;
        try { reply = await Cat1.SendAsync(CatCommands.ReadScopeLevel()); }
        catch { return; }
        if (CatCommands.ParseScopeLevel(reply) is { } v) ScopeLevelDb = v;
    }

    // MARK: - Display (DA) — combined 3-value Set; callers must read-then-merge

    private int _displayContrast = 10;
    public int DisplayContrast { get => _displayContrast; private set => SetProperty(ref _displayContrast, value); }

    private int _displayBrightness = 15;
    public int DisplayBrightness { get => _displayBrightness; private set => SetProperty(ref _displayBrightness, value); }

    private int _displayLedBrightness = 20;
    public int DisplayLedBrightness { get => _displayLedBrightness; private set => SetProperty(ref _displayLedBrightness, value); }

    public Task SetDisplayAsync(int contrast, int brightness, int ledBrightness) =>
        SendSetAsync(CatCommands.SetDisplay(contrast, brightness, ledBrightness), () =>
        {
            DisplayContrast = CatCommands.Clamp(contrast, 0, 20);
            DisplayBrightness = CatCommands.Clamp(brightness, 0, 20);
            DisplayLedBrightness = CatCommands.Clamp(ledBrightness, 0, 20);
        });

    public async Task RefreshDisplayAsync()
    {
        string reply;
        try { reply = await Cat1.SendAsync(CatCommands.ReadDisplay()); }
        catch { return; }
        if (CatCommands.ParseDisplay(reply, out var contrast, out var brightness, out var led))
        {
            DisplayContrast = contrast;
            DisplayBrightness = brightness;
            DisplayLedBrightness = led;
        }
    }

    // MARK: - Mic EQ (PR1) — mode-gated

    /// Mic EQ is activated only in LSB, USB, AM, AM-N, FM and FM-N modes —
    /// enforced client-side, not returned as an error by the radio. Mode
    /// codes per RadioMode.CatCode: "1"=LSB "2"=USB "5"=AM "D"=AM-N "4"=FM "B"=FM-N.
    public bool MicEqAvailable => ModeCode is '1' or '2' or '5' or 'D' or '4' or 'B';

    private bool _micEqOn;
    public bool MicEqOn { get => _micEqOn; private set => SetProperty(ref _micEqOn, value); }

    public Task SetMicEqAsync(bool on) => SendSetAsync(CatCommands.SetMicEq(on), () => MicEqOn = on);

    public Task RefreshMicEqAsync() =>
        FetchAndApplyAsync(CatCommands.ReadMicEq(), CatCommands.ParseMicEq, v => MicEqOn = v);

    // MARK: - Speech Processor Level (PL)

    private int _procLevel;
    public int ProcLevel { get => _procLevel; private set => SetProperty(ref _procLevel, value); }

    public Task SetProcLevelAsync(int level) =>
        SendSetAsync(CatCommands.SetProcLevel(level), () => ProcLevel = CatCommands.Clamp(level, 0, 100));

    public Task RefreshProcLevelAsync() =>
        FetchAndApplyAsync(CatCommands.ReadProcLevel(), CatCommands.ParseProcLevel, v => ProcLevel = v);

    // MARK: - Antenna Tuner (AC) — P1/P2 are hardware identifiers discovered
    // from the radio's own Answer, never guessed; only P3 (on/off/start) is driven.

    private char _tunerP1 = '0';
    public char TunerP1 { get => _tunerP1; private set => SetProperty(ref _tunerP1, value); }

    private char _tunerP2 = '0';
    public char TunerP2 { get => _tunerP2; private set => SetProperty(ref _tunerP2, value); }

    private bool _tunerOn;
    public bool TunerOn { get => _tunerOn; private set => SetProperty(ref _tunerOn, value); }

    public Task SetAntennaTunerAsync(char p3) =>
        SendSetAsync(CatCommands.SetAntennaTuner(TunerP1, TunerP2, p3), () => TunerOn = p3 != '0');

    /// ANT TUNE — momentary "start tuning", P3='3' (confirmed against the
    /// Mac app's RadioControllerV3.startAntennaTuning(); this Windows port
    /// originally guessed P3='2' from the manual's field ordering, which is
    /// why the button did nothing on real hardware). Doesn't touch TunerOn:
    /// starting a tuning cycle isn't the same state as the Tuner on/off
    /// toggle, matching the Mac app's split between setTunerOn and
    /// startAntennaTuning.
    public Task StartAntennaTuningAsync() =>
        SendSetAsync(CatCommands.SetAntennaTuner(TunerP1, TunerP2, '3'), () => { });

    public async Task RefreshAntennaTunerAsync()
    {
        string reply;
        try { reply = await Cat1.SendAsync(CatCommands.ReadAntennaTuner()); }
        catch { return; }
        if (CatCommands.ParseAntennaTuner(reply, out var p1, out var p2, out var p3))
        {
            TunerP1 = p1;
            TunerP2 = p2;
            TunerOn = p3 != '0';
        }
    }

    // MARK: - Noise Blanker Level (NL)

    private int _noiseBlankerLevel;
    public int NoiseBlankerLevel { get => _noiseBlankerLevel; private set => SetProperty(ref _noiseBlankerLevel, value); }

    public Task SetNoiseBlankerAsync(int level) =>
        SendSetAsync(CatCommands.SetNoiseBlanker(level), () => NoiseBlankerLevel = CatCommands.Clamp(level, 0, 10));

    public Task RefreshNoiseBlankerAsync() =>
        FetchAndApplyAsync(CatCommands.ReadNoiseBlanker(), CatCommands.ParseNoiseBlanker, v => NoiseBlankerLevel = v);

    // MARK: - VOX (VX/VG/VD)

    private bool _voxOn;
    public bool VoxOn { get => _voxOn; private set => SetProperty(ref _voxOn, value); }

    public Task SetVoxAsync(bool on) => SendSetAsync(CatCommands.SetVox(on), () => VoxOn = on);

    public Task RefreshVoxAsync() =>
        FetchAndApplyAsync(CatCommands.ReadVox(), CatCommands.ParseVox, v => VoxOn = v);

    private int _voxGain = 50;
    public int VoxGain { get => _voxGain; private set => SetProperty(ref _voxGain, value); }

    public Task SetVoxGainAsync(int gain) =>
        SendSetAsync(CatCommands.SetVoxGain(gain), () => VoxGain = CatCommands.Clamp(gain, 0, 100));

    public Task RefreshVoxGainAsync() =>
        FetchAndApplyAsync(CatCommands.ReadVoxGain(), CatCommands.ParseVoxGain, v => VoxGain = v);

    private int _voxDelayIndex;
    public int VoxDelayIndex { get => _voxDelayIndex; private set => SetProperty(ref _voxDelayIndex, value); }

    public int VoxDelayMs => CatCommands.VoxDelayMs(VoxDelayIndex);

    public Task SetVoxDelayAsync(int index) =>
        SendSetAsync(CatCommands.SetVoxDelay(index), () => VoxDelayIndex = CatCommands.Clamp(index, 0, 33));

    public Task RefreshVoxDelayAsync() =>
        FetchAndApplyAsync(CatCommands.ReadVoxDelay(), CatCommands.ParseVoxDelay, v => VoxDelayIndex = v);

    // MARK: - TXW (TS) — a latching toggle, only meaningful while Split is
    // on; wire format is plain 0/1, the UI is responsible for disabling the
    // control unless SplitOn.

    private bool _txwOn;
    public bool TxwOn { get => _txwOn; private set => SetProperty(ref _txwOn, value); }

    public Task SetTxwAsync(bool on) => SendSetAsync(CatCommands.SetTxw(on), () => TxwOn = on);

    public Task RefreshTxwAsync() =>
        FetchAndApplyAsync(CatCommands.ReadTxw(), CatCommands.ParseTxw, v => TxwOn = v);

    // MARK: - Mic Gain (MG)

    private int _micGain = 50;
    public int MicGain { get => _micGain; private set => SetProperty(ref _micGain, value); }

    public Task SetMicGainAsync(int gain) =>
        SendSetAsync(CatCommands.SetMicGain(gain), () => MicGain = CatCommands.Clamp(gain, 0, 100));

    public Task RefreshMicGainAsync() =>
        FetchAndApplyAsync(CatCommands.ReadMicGain(), CatCommands.ParseMicGain, v => MicGain = v);

    // MARK: - AMC Output Level (AO)

    private int _amcLevel = 50;
    public int AmcLevel { get => _amcLevel; private set => SetProperty(ref _amcLevel, value); }

    public Task SetAmcLevelAsync(int level) =>
        SendSetAsync(CatCommands.SetAmcLevel(level), () => AmcLevel = CatCommands.Clamp(level, 1, 100));

    public Task RefreshAmcLevelAsync() =>
        FetchAndApplyAsync(CatCommands.ReadAmcLevel(), CatCommands.ParseAmcLevel, v => AmcLevel = v);

    // MARK: - CW group: Keyer, Break-In, Pitch, Speed, Break-In Delay, Spot

    private bool _keyerOn;
    public bool KeyerOn { get => _keyerOn; internal set => SetProperty(ref _keyerOn, value); }

    public Task SetKeyerAsync(bool on) => SendSetAsync(CatCommands.SetKeyer(on), () => KeyerOn = on);

    public Task RefreshKeyerAsync() =>
        FetchAndApplyAsync(CatCommands.ReadKeyer(), CatCommands.ParseKeyer, v => KeyerOn = v);

    private bool _breakInOn;
    public bool BreakInOn { get => _breakInOn; private set => SetProperty(ref _breakInOn, value); }

    public Task SetBreakInAsync(bool on) => SendSetAsync(CatCommands.SetBreakIn(on), () => BreakInOn = on);

    public Task RefreshBreakInAsync() =>
        FetchAndApplyAsync(CatCommands.ReadBreakIn(), CatCommands.ParseBreakIn, v => BreakInOn = v);

    private int _keyPitchHz = 700;
    public int KeyPitchHz { get => _keyPitchHz; private set => SetProperty(ref _keyPitchHz, value); }

    public Task SetKeyPitchAsync(int hz) =>
        SendSetAsync(CatCommands.SetKeyPitch(hz), () => KeyPitchHz = 300 + CatCommands.Clamp((hz - 300) / 10, 0, 75) * 10);

    public Task RefreshKeyPitchAsync() =>
        FetchAndApplyAsync(CatCommands.ReadKeyPitch(), CatCommands.ParseKeyPitch, v => KeyPitchHz = v);

    private int _keySpeedWpm = 20;
    public int KeySpeedWpm { get => _keySpeedWpm; private set => SetProperty(ref _keySpeedWpm, value); }

    public Task SetKeySpeedAsync(int wpm) =>
        SendSetAsync(CatCommands.SetKeySpeed(wpm), () => KeySpeedWpm = CatCommands.Clamp(wpm, 4, 60));

    public Task RefreshKeySpeedAsync() =>
        FetchAndApplyAsync(CatCommands.ReadKeySpeed(), CatCommands.ParseKeySpeed, v => KeySpeedWpm = v);

    private int _cwBreakInDelayIndex;
    public int CwBreakInDelayIndex { get => _cwBreakInDelayIndex; private set => SetProperty(ref _cwBreakInDelayIndex, value); }

    public int CwBreakInDelayMs => CatCommands.CwBreakInDelayMs(CwBreakInDelayIndex);

    public Task SetCwBreakInDelayAsync(int index) =>
        SendSetAsync(CatCommands.SetCwBreakInDelay(index), () => CwBreakInDelayIndex = CatCommands.Clamp(index, 0, 33));

    public Task RefreshCwBreakInDelayAsync() =>
        FetchAndApplyAsync(CatCommands.ReadCwBreakInDelay(), CatCommands.ParseCwBreakInDelay, v => CwBreakInDelayIndex = v);

    private bool _cwSpotOn;
    public bool CwSpotOn { get => _cwSpotOn; private set => SetProperty(ref _cwSpotOn, value); }

    public Task SetCwSpotAsync(bool on) => SendSetAsync(CatCommands.SetCwSpot(on), () => CwSpotOn = on);

    public Task RefreshCwSpotAsync() =>
        FetchAndApplyAsync(CatCommands.ReadCwSpot(), CatCommands.ParseCwSpot, v => CwSpotOn = v);

    // MARK: - Voice Message (LM P1=0 arms recording, NOT play) / SD
    // Recording (LM P1=1, received-audio QSO logger) / Message Playback
    // (PB, the genuine playback trigger) — three distinct features; see
    // CatCommands.Func.cs's LM/PB comment for why they must stay distinct.

    /// 0 = none selected/stopped, 1-5 = the channel last selected (armed for record).
    private int _voiceMessageChannel = 1;
    public int VoiceMessageChannel { get => _voiceMessageChannel; private set => SetProperty(ref _voiceMessageChannel, value); }

    public Task SetVoiceMessageChannelAsync(int channel) =>
        SendSetAsync(CatCommands.SetVoiceMessageChannel(channel), () => VoiceMessageChannel = CatCommands.Clamp(channel, 0, 5));

    public Task RefreshVoiceMessageChannelAsync() =>
        FetchAndApplyAsync(CatCommands.ReadVoiceMessageChannel(), CatCommands.ParseVoiceMessageChannel, v => VoiceMessageChannel = v);

    /// Recording *received* audio to the SD card (a QSO logger) — not
    /// voice-memory recording.
    private bool _sdRecordingOn;
    public bool SdRecordingOn { get => _sdRecordingOn; private set => SetProperty(ref _sdRecordingOn, value); }

    public Task SetSdRecordingAsync(bool on) => SendSetAsync(CatCommands.SetSdRecording(on), () => SdRecordingOn = on);

    public Task RefreshSdRecordingAsync() =>
        FetchAndApplyAsync(CatCommands.ReadSdRecording(), CatCommands.ParseSdRecording, v => SdRecordingOn = v);

    /// 0 = stopped, 1-5 = the channel currently playing — genuinely
    /// distinct from VoiceMessageChannel's LM-based record-arm.
    private int _messagePlaybackChannel;
    public int MessagePlaybackChannel { get => _messagePlaybackChannel; private set => SetProperty(ref _messagePlaybackChannel, value); }

    public Task SetMessagePlaybackAsync(int channel) =>
        SendSetAsync(CatCommands.SetMessagePlayback(channel), () => MessagePlaybackChannel = CatCommands.Clamp(channel, 0, 5));

    public Task RefreshMessagePlaybackAsync() =>
        FetchAndApplyAsync(CatCommands.ReadMessagePlayback(), CatCommands.ParseMessagePlayback, v => MessagePlaybackChannel = v);

    // MARK: - Monitor Level (ML1)

    private int _moniLevel;
    public int MoniLevel { get => _moniLevel; private set => SetProperty(ref _moniLevel, value); }

    public Task SetMonitorLevelAsync(int level) =>
        SendSetAsync(CatCommands.SetMonitorLevel(level), () => MoniLevel = CatCommands.Clamp(level, 0, 100));

    public Task RefreshMonitorLevelAsync() =>
        FetchAndApplyAsync(CatCommands.ReadMonitorLevel(), CatCommands.ParseMonitorLevel, v => MoniLevel = v);

    // MARK: - Zero In (ZI0) — momentary, Set-only

    public Task TriggerZeroInAsync() => Cat1.IsConnected
        ? Cat1.SendAsync(CatCommands.TriggerZeroIn(), expectsReply: false)
        : Task.CompletedTask;
}
