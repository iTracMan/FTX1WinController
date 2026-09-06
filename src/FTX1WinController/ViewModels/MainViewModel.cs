using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using FTX1WinController.Audio;
using FTX1WinController.Cat;
using FTX1WinController.Models;
using FTX1WinController.Settings;

namespace FTX1WinController.ViewModels;

/// MAIN AF/RF/SQL knob's current target — mirrors VFODialPanelView's own
/// `SubDialTarget` in the Mac app.
public enum SubDialTarget { Af, Rf, Sql }

/// Drives the UI by talking to a RadioController, which talks CAT directly
/// over two real COM ports (System.IO.Ports.SerialPort, via
/// WindowsSerialTransport) — no bridge, no network. Poll cadence (250ms
/// meters, every 8th tick = 2s for freq/mode/PTT) mirrors
/// RadioController.startPolling() in the Mac app; this app's own
/// DispatcherTimer drives it since RadioController deliberately doesn't run
/// its own poll loop (see RadioController.cs).
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly RadioController _radio = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly LiveAudioService _audio = new();
    private readonly RecordingPlayer _player = new();
    private DispatcherTimer? _pollTimer;
    private int _tickCount;
    private int _funcPage1RotationIndex;

    // Set while a local RECORD capture is in progress, so ToggleRecordAsync
    // can save frequency/mode as of the moment recording *started* (not
    // whatever they've drifted to by the time it stops).
    private string? _currentRecordingId;
    private long _recordingStartFrequencyHz;
    private string _recordingStartMode = "";

    public MainViewModel()
    {
        _selectedCat1Port = _settings.Cat1Port;
        _selectedCat2Port = _settings.Cat2Port;

        // Mirror the two pieces of RadioController state this ViewModel
        // doesn't otherwise poll for on every change (LastError/
        // ConnectionState are set from inside RadioController's own
        // methods, including ones this ViewModel doesn't call directly,
        // like CatConnection's internal timeout handling).
        _radio.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(RadioController.LastError):
                    LastError = _radio.LastError;
                    break;
                case nameof(RadioController.ConnectionState):
                    ConnectionState = _radio.ConnectionState;
                    break;
            }
        };

        ModeButtons = new ObservableCollection<ModeButtonViewModel>(BuildModeGrid());
        BandButtons = new ObservableCollection<BandButtonViewModel>(
            BandCodeInfo.All.Select(b => new BandButtonViewModel(b, code => _ = SetBandAsync(code))));

        RefreshPortsCommand = new RelayCommand(async () => await RefreshPortsAsync(), () => ConnectionState != ConnectionState.Connecting);
        ConnectCommand = new RelayCommand(async () => await ConnectAsync(), CanConnect);
        DisconnectCommand = new RelayCommand(async () => await DisconnectAsync(), () => ConnectionState == ConnectionState.Connected);
        SetFrequencyCommand = new RelayCommand(async () => await SetFrequencyAsync(), () => ConnectionState == ConnectionState.Connected);
        TogglePttCommand = new RelayCommand(async () => await TogglePttAsync(), () => ConnectionState == ConnectionState.Connected);
        CycleClarifierCommand = new RelayCommand(async () => await CycleClarifierAsync(), () => ConnectionState == ConnectionState.Connected);
        ClarifierOffsetUpCommand = new RelayCommand(async () => await AdjustClarifierOffsetAsync(10), () => ConnectionState == ConnectionState.Connected);
        ClarifierOffsetDownCommand = new RelayCommand(async () => await AdjustClarifierOffsetAsync(-10), () => ConnectionState == ConnectionState.Connected);
        CycleFineTuningCommand = new RelayCommand(async () => await CycleFineTuningAsync(), () => ConnectionState == ConnectionState.Connected);
        QmbRecallCommand = new RelayCommand(async () => await QmbRecallAsync(), () => ConnectionState == ConnectionState.Connected);
        QmbStoreCommand = new RelayCommand(async () => await QmbStoreAsync(), () => ConnectionState == ConnectionState.Connected);
        VfoStepUpCommand = new RelayCommand(async () => await AdjustFrequencyAsync(1), () => ConnectionState == ConnectionState.Connected);
        VfoStepDownCommand = new RelayCommand(async () => await AdjustFrequencyAsync(-1), () => ConnectionState == ConnectionState.Connected);
        SelectAfCommand = new RelayCommand(() => SelectSubDial(SubDialTarget.Af));
        SelectRfCommand = new RelayCommand(() => SelectSubDial(SubDialTarget.Rf));
        SelectSqlCommand = new RelayCommand(() => SelectSubDial(SubDialTarget.Sql));
        SubDialUpCommand = new RelayCommand(async () => await AdjustSubDialAsync(1), () => ConnectionState == ConnectionState.Connected);
        SubDialDownCommand = new RelayCommand(async () => await AdjustSubDialAsync(-1), () => ConnectionState == ConnectionState.Connected);

        // FUNC Page 1 — see SettingViewModels.cs for the reusable Int/
        // Toggle/Choice helper types this leans on instead of ~15 lines of
        // near-identical property/command/refresh boilerplate per control.
        // Each is wired directly to a RadioController Set/Refresh method
        // pair — no protocol string in sight, unlike the bridge-based version.
        bool Connected() => ConnectionState == ConnectionState.Connected;
        void OnError(string msg) => LastError = msg;

        DPeak = new IntSettingViewModel("D-Peak", "SCOPEPEAK", 0, 4, 1,
            () => _radio.ScopePeakLevel, _radio.RefreshScopePeakAsync, _radio.SetScopePeakAsync, Connected, OnError) { Format = v => $"LV{v + 1}" };
        DColor = new IntSettingViewModel("D-Color", "SCOPECOLOR", 0, 10, 1,
            () => _radio.ScopeColorIndex, _radio.RefreshScopeColorAsync, _radio.SetScopeColorAsync, Connected, OnError) { Format = v => $"COLOR-{v + 1}" };
        // Display (DA) has one combined Set for contrast/brightness/LED —
        // each of these three sends the other two's last-known value
        // alongside its own change, same read-then-merge the bridge used
        // to do server-side.
        DContrast = new IntSettingViewModel("D-Contrast", "DISPLAYCONTRAST", 0, 20, 1,
            () => _radio.DisplayContrast, _radio.RefreshDisplayAsync,
            v => _radio.SetDisplayAsync(v, _radio.DisplayBrightness, _radio.DisplayLedBrightness), Connected, OnError);
        DimmerBrightness = new IntSettingViewModel("Dimmer (TFT)", "DISPLAYBRIGHTNESS", 0, 20, 1,
            () => _radio.DisplayBrightness, _radio.RefreshDisplayAsync,
            v => _radio.SetDisplayAsync(_radio.DisplayContrast, v, _radio.DisplayLedBrightness), Connected, OnError);
        DimmerLed = new IntSettingViewModel("Dimmer (LED)", "DISPLAYLED", 0, 20, 1,
            () => _radio.DisplayLedBrightness, _radio.RefreshDisplayAsync,
            v => _radio.SetDisplayAsync(_radio.DisplayContrast, _radio.DisplayBrightness, v), Connected, OnError);
        ProcLevel = new IntSettingViewModel("Proc Level", "PROCLEVEL", 0, 100, 1,
            () => _radio.ProcLevel, _radio.RefreshProcLevelAsync, _radio.SetProcLevelAsync, Connected, OnError);
        NoiseBlanker = new IntSettingViewModel("NB", "NB", 0, 10, 1,
            () => _radio.NoiseBlankerLevel, _radio.RefreshNoiseBlankerAsync, _radio.SetNoiseBlankerAsync, Connected, OnError) { Format = v => v == 0 ? "OFF" : v.ToString() };
        NoiseReduction = new IntSettingViewModel("DNR", "NR", 0, 10, 1,
            () => _radio.NoiseReductionLevel, _radio.RefreshNoiseReductionAsync, _radio.SetNoiseReductionAsync, Connected, OnError) { Format = v => v == 0 ? "OFF" : v.ToString() };
        MicGain = new IntSettingViewModel("Mic Gain", "MICGAIN", 0, 100, 1,
            () => _radio.MicGain, _radio.RefreshMicGainAsync, _radio.SetMicGainAsync, Connected, OnError);
        AmcLevel = new IntSettingViewModel("AMC Level", "AMCLEVEL", 1, 100, 1,
            () => _radio.AmcLevel, _radio.RefreshAmcLevelAsync, _radio.SetAmcLevelAsync, Connected, OnError);
        VoxGain = new IntSettingViewModel("VOX Gain", "VOXGAIN", 0, 100, 1,
            () => _radio.VoxGain, _radio.RefreshVoxGainAsync, _radio.SetVoxGainAsync, Connected, OnError);
        VoxDelay = new IntSettingViewModel("VOX Delay", "VOXDELAY", 0, 33, 1,
            () => _radio.VoxDelayIndex, _radio.RefreshVoxDelayAsync, _radio.SetVoxDelayAsync, Connected, OnError) { Format = v => $"{VoxDelayMs(v)}ms" };

        DMarker = new ToggleSettingViewModel("D-Marker", "SCOPEMARKER",
            () => _radio.ScopeMarkerOn, _radio.RefreshScopeMarkerAsync, _radio.SetScopeMarkerAsync, Connected, OnError);
        Attenuator = new ToggleSettingViewModel("ATT", "ATT",
            () => _radio.RfAttenuatorOn, _radio.RefreshRfAttenuatorAsync, _radio.SetRfAttenuatorAsync, Connected, OnError);
        AutoNotch = new ToggleSettingViewModel("DNF", "DNF",
            () => _radio.AutoNotchOn, _radio.RefreshAutoNotchAsync, _radio.SetAutoNotchAsync, Connected, OnError);
        MicEq = new ToggleSettingViewModel("Mic EQ", "MICEQ",
            () => _radio.MicEqOn, _radio.RefreshMicEqAsync, _radio.SetMicEqAsync, () => Connected() && MicEqAvailable, OnError);
        // Antenna Tuner's on/off (P3) and the momentary "start tuning"
        // action (AntTuneCommand, below) both drive the same AC command's
        // P3 field — P1/P2 (which tuner is fitted) are discovered from the
        // radio's own Answer and never guessed, see RadioController.Func.cs.
        Tuner = new ToggleSettingViewModel("Tuner", "TUNER",
            () => _radio.TunerOn, _radio.RefreshAntennaTunerAsync, on => _radio.SetAntennaTunerAsync(on ? '1' : '0'), Connected, OnError);
        Vox = new ToggleSettingViewModel("VOX", "VOX",
            () => _radio.VoxOn, _radio.RefreshVoxAsync, _radio.SetVoxAsync, Connected, OnError);
        // TXW only actually takes effect while Split is on (confirmed on
        // the Mac app's hardware testing — see CatCommands.Func.cs's TXW
        // comment); mirrors the Mac app's own FUNC panel, which disables
        // this button unless splitOn.
        Txw = new ToggleSettingViewModel("TXW", "TXW",
            () => _radio.TxwOn, _radio.RefreshTxwAsync, _radio.SetTxwAsync, () => Connected() && _radio.SplitOn, OnError);

        Agc = new ChoiceSettingViewModel<int>(
            "AGC", "AGC",
            // Mirrors the radio's own on-screen button order (OFF/AUTO/
            // FAST/MID/SLOW), not AgcMode's raw declaration order.
            new[] { (0, "OFF"), (4, "AUTO"), (1, "FAST"), (2, "MID"), (3, "SLOW") },
            () => (int)_radio.AgcMode, _radio.RefreshAgcAsync, v => _radio.SetAgcAsync((AgcMode)v),
            v => v.ToString(), s => (int.TryParse(s, out var v), v),
            Connected, OnError);

        Ant = new ChoiceSettingViewModel<int>(
            "ANT", "HFANT",
            new[] { (0, "ANT1"), (1, "ANT2") },
            () => _radio.HfAntSelect, _radio.RefreshHfAntSelectAsync, _radio.SetHfAntSelectAsync,
            v => v.ToString(), s => (int.TryParse(s, out var v), v),
            () => Connected() && IsHfBand, OnError);

        AntTuneCommand = new RelayCommand(async () => await AntTuneAsync(), Connected);
        DLevelUpCommand = new RelayCommand(async () => await AdjustDLevelAsync(0.5), Connected);
        DLevelDownCommand = new RelayCommand(async () => await AdjustDLevelAsync(-0.5), Connected);
        RfPowerUpCommand = new RelayCommand(async () => await AdjustRfPowerAsync(1), Connected);
        RfPowerDownCommand = new RelayCommand(async () => await AdjustRfPowerAsync(-1), Connected);
        IpoCell = new ChoiceCellViewModel("IPO", PreampOptions);
        RebuildPreampOptions();

        // FUNC Page 2 (CW) — same reusable Int/Toggle helpers as Page 1.
        MoniLevel = new IntSettingViewModel("Moni Level", "MONITORLEVEL", 0, 100, 1,
            () => _radio.MoniLevel, _radio.RefreshMonitorLevelAsync, _radio.SetMonitorLevelAsync, Connected, OnError);
        CwSpeed = new IntSettingViewModel("CW Speed", "KEYSPEED", 4, 60, 1,
            () => _radio.KeySpeedWpm, _radio.RefreshKeySpeedAsync, _radio.SetKeySpeedAsync, Connected, OnError) { Format = v => $"{v}wpm" };
        CwPitch = new IntSettingViewModel("CW Pitch", "KEYPITCH", 300, 1050, 10,
            () => _radio.KeyPitchHz, _radio.RefreshKeyPitchAsync, _radio.SetKeyPitchAsync, Connected, OnError) { Format = v => $"{v}Hz" };
        // Same 0-33 index -> ms table as VOX Delay (see VoxDelayMs) — the
        // manual documents both with identical irregular-then-100ms-step
        // values, confirmed 1:1 against CatCommands.CwBreakInDelayMs.
        CwBreakInDelay = new IntSettingViewModel("BK-Delay", "CWBREAKINDELAY", 0, 33, 1,
            () => _radio.CwBreakInDelayIndex, _radio.RefreshCwBreakInDelayAsync, _radio.SetCwBreakInDelayAsync, Connected, OnError) { Format = v => $"{VoxDelayMs(v)}ms" };

        Keyer = new ToggleSettingViewModel("Keyer", "KEYER",
            () => _radio.KeyerOn, _radio.RefreshKeyerAsync, _radio.SetKeyerAsync, () => Connected() && KeyerAvailable, OnError);
        BreakIn = new ToggleSettingViewModel("BK-IN", "BREAKIN",
            () => _radio.BreakInOn, _radio.RefreshBreakInAsync, _radio.SetBreakInAsync, Connected, OnError);
        CwSpot = new ToggleSettingViewModel("CW Spot", "CWSPOT",
            () => _radio.CwSpotOn, _radio.RefreshCwSpotAsync, _radio.SetCwSpotAsync, Connected, OnError);
        SdRecording = new ToggleSettingViewModel("Record", "SDRECORDING",
            () => _radio.SdRecordingOn, _radio.RefreshSdRecordingAsync, _radio.SetSdRecordingAsync, Connected, OnError);

        ZeroInCommand = new RelayCommand(async () => await ZeroInAsync(), Connected);
        MessageArmCommand = new RelayCommand(ArmMessageRecording, Connected);
        RecordCommand = new RelayCommand(async () => await ToggleRecordAsync(), Connected);
        RefreshRecordingsCommand = new RelayCommand(async () => await RefreshRecordingsAsync(), Connected);
        StopPlaybackCommand = new RelayCommand(async () => await StopPlaybackAsync(), Connected);
        ToggleAudioMonitorCommand = new RelayCommand(async () => await ToggleAudioMonitorAsync(), Connected);
        RefreshAudioDevicesCommand = new RelayCommand(RefreshAudioDevices);
        RefreshAudioDevices();
        // Restore the saved device by name only if it's still actually
        // present — otherwise leave it null rather than silently "select"
        // a device that isn't there (e.g. the radio unplugged since last run).
        if (_settings.AudioInputDeviceName is { } savedDevice && AvailableAudioInputDevices.Contains(savedDevice))
        {
            SelectedAudioInputDevice = savedDevice;
        }

        for (var ch = 1; ch <= 5; ch++)
        {
            var channel = ch;
            MessageChannelOptions.Add(new ChoiceOptionViewModel<int>(channel, channel.ToString(), SelectAndPlayMessageChannel, Connected));
        }
        MessageCell = new ChoiceCellViewModel("MESSAGE", MessageChannelOptions);

        // Scan/Split
        Scan = new ChoiceSettingViewModel<int>(
            "Scan", "SCAN",
            new[] { (0, "Stop"), (1, "Up"), (2, "Down") },
            () => (int)_radio.ScanState, _radio.RefreshScanAsync, v => _radio.SetScanAsync((CatCommands.ScanState)v),
            v => v.ToString(), s => (int.TryParse(s, out var v), v),
            Connected, OnError);
        Split = new ToggleSettingViewModel("Split", "SPLIT",
            () => _radio.SplitOn, _radio.RefreshSplitAsync, _radio.SetSplitAsync, Connected, OnError);
        // Deliberately excluded from Presets (see AllPresetSettings) —
        // hardware-confirmed that this switches the radio's currently-
        // active VFO, not just which side transmits during split.
        TxSide = new ChoiceSettingViewModel<bool>(
            "TX", "TXSIDE",
            new[] { (true, "MAIN"), (false, "SUB") },
            () => _radio.TxSideIsMain, _radio.RefreshTxSideAsync, _radio.SetTxSideAsync,
            v => v ? "0" : "1", s => (s == "0" || s == "1", s == "0"),
            Connected, OnError);
        RepeaterShift = new ChoiceSettingViewModel<string>(
            "Shift", "REPEATERSHIFT",
            new[] { ("0", "SIMPLEX"), ("1", "+"), ("2", "−"), ("3", "ARS") },
            () => _radio.RepeaterShift.CatCode().ToString(), _radio.RefreshRepeaterShiftAsync,
            v => RepeaterShiftInfo.FromCatCode(v[0]) is { } shift ? _radio.SetRepeaterShiftAsync(shift) : Task.CompletedTask,
            v => v, s => (true, s),
            Connected, OnError);
        ToneType = new ChoiceSettingViewModel<string>(
            "Type", "TONETYPE",
            new[] { ("0", "OFF"), ("1", "ENC"), ("2", "ENC+DEC"), ("3", "DCS") },
            () => _radio.ToneType.CatCode().ToString(), _radio.RefreshToneTypeAsync,
            v => Models.ToneTypeInfo.FromCatCode(v[0]) is { } tone ? _radio.SetToneTypeAsync(tone) : Task.CompletedTask,
            v => v, s => (true, s),
            Connected, OnError);
        CtcssIndex = new IntSettingViewModel("CTCSS Tone", "CTCSSINDEX", 0, ToneTables.CtcssHz.Length - 1, 1,
            () => _radio.CtcssIndex, _radio.RefreshToneNumberAsync, _radio.SetCtcssIndexAsync, Connected, OnError) { Format = ToneTables.CtcssLabel };
        DcsIndex = new IntSettingViewModel("DCS Code", "DCSINDEX", 0, ToneTables.DcsCodes.Length - 1, 1,
            () => _radio.DcsIndex, _radio.RefreshToneNumberAsync, _radio.SetDcsIndexAsync, Connected, OnError) { Format = ToneTables.DcsLabel };
        SetSubFrequencyCommand = new RelayCommand(async () => await SetSubFrequencyAsync(), Connected);

        SavePresetCommand = new RelayCommand(async () => await SavePresetAsync(), () => Connected() && !string.IsNullOrWhiteSpace(NewPresetName));
        foreach (var data in _settings.Presets) Presets.Add(MakePresetViewModel(data));
    }

    // MARK: - CAT-1/CAT-2 port fields (real Windows COM port names, e.g. "COM3")

    public ObservableCollection<string> AvailablePorts { get; } = new();

    private string? _selectedCat1Port;
    public string? SelectedCat1Port { get => _selectedCat1Port; set => SetProperty(ref _selectedCat1Port, value); }

    private string _cat1Baud = "38400";
    public string Cat1Baud { get => _cat1Baud; set => SetProperty(ref _cat1Baud, value); }

    private string? _selectedCat2Port;
    public string? SelectedCat2Port { get => _selectedCat2Port; set => SetProperty(ref _selectedCat2Port, value); }

    private string _cat2Baud = "38400";
    public string Cat2Baud { get => _cat2Baud; set => SetProperty(ref _cat2Baud, value); }

    // MARK: - Connection/radio state

    private ConnectionState _connectionState = ConnectionState.Disconnected;
    public ConnectionState ConnectionState
    {
        get => _connectionState;
        private set { if (SetProperty(ref _connectionState, value)) RaiseCanExecuteChanged(); }
    }

    private string? _lastError;
    public string? LastError { get => _lastError; private set => SetProperty(ref _lastError, value); }

    private long _frequencyHz;
    public long FrequencyHz { get => _frequencyHz; private set { if (SetProperty(ref _frequencyHz, value)) OnPropertyChanged(nameof(FrequencyDisplay)); } }

    public string FrequencyDisplay => (_frequencyHz / 1_000_000.0).ToString("F6") + " MHz";

    private string _directEntryText = "";
    public string DirectEntryText { get => _directEntryText; set => SetProperty(ref _directEntryText, value); }

    private char _modeCode = '1';
    private string _modeDisplayName = "LSB";
    public string ModeDisplayName { get => _modeDisplayName; private set => SetProperty(ref _modeDisplayName, value); }

    private bool _isTransmitting;
    public bool IsTransmitting { get => _isTransmitting; private set => SetProperty(ref _isTransmitting, value); }

    private int _sMeterValue;
    public int SMeterValue { get => _sMeterValue; private set => SetProperty(ref _sMeterValue, value); }

    private int _powerOutputValue;
    public int PowerOutputValue { get => _powerOutputValue; private set => SetProperty(ref _powerOutputValue, value); }

    // MARK: - Clarifier (CF)

    private bool _clarifierRxOn;
    public bool ClarifierRxOn { get => _clarifierRxOn; private set { if (SetProperty(ref _clarifierRxOn, value)) { OnPropertyChanged(nameof(ClarifierStatusText)); OnPropertyChanged(nameof(ClarifierActive)); } } }

    private bool _clarifierTxOn;
    public bool ClarifierTxOn { get => _clarifierTxOn; private set { if (SetProperty(ref _clarifierTxOn, value)) { OnPropertyChanged(nameof(ClarifierStatusText)); OnPropertyChanged(nameof(ClarifierActive)); } } }

    private int _clarifierOffsetHz;
    public int ClarifierOffsetHz { get => _clarifierOffsetHz; private set => SetProperty(ref _clarifierOffsetHz, value); }

    /// Drives the VFO dial's glow-arc color (VfoDialView.IsClarifierActive)
    /// — blue normally, red while CLAR is RX, TX, or both.
    public bool ClarifierActive => _clarifierRxOn || _clarifierTxOn;

    /// Same display rule as the Mac app's own `clarifierStatusText` — CLAR
    /// cycles OFF -> RX -> TX -> RX/TX -> OFF, not a plain toggle.
    public string ClarifierStatusText => (_clarifierRxOn, _clarifierTxOn) switch
    {
        (false, false) => "OFF",
        (true, false) => "RX",
        (false, true) => "TX",
        (true, true) => "RX/TX",
    };

    // MARK: - Fine/Fast (FN)

    private string _fineTuningDisplayName = "OFF";
    public string FineTuningDisplayName { get => _fineTuningDisplayName; private set => SetProperty(ref _fineTuningDisplayName, value); }

    // MARK: - MAIN AF/RF/SQL knob

    private int _afGain;
    public int AfGain { get => _afGain; private set { if (SetProperty(ref _afGain, value)) OnPropertyChanged(nameof(SubDialCurrentValueDisplay)); } }

    private int _rfGain;
    public int RfGain { get => _rfGain; private set { if (SetProperty(ref _rfGain, value)) { OnPropertyChanged(nameof(SubDialCurrentValueDisplay)); OnPropertyChanged(nameof(RfOrSquelchValue)); } } }

    private int _squelch;
    public int Squelch { get => _squelch; private set { if (SetProperty(ref _squelch, value)) { OnPropertyChanged(nameof(SubDialCurrentValueDisplay)); OnPropertyChanged(nameof(RfOrSquelchValue)); } } }

    /// Whichever of RF Gain/Squelch is relevant for the current mode — the
    /// Display panel's "RF" rail meter (RadioVolMeterView) always shows
    /// this one, independent of which the MAIN AF/RF/SQL sub-dial
    /// currently has *selected* for stepping (SubDialTarget).
    public int RfOrSquelchValue => ModeShowsSquelchNotRF ? Squelch : RfGain;

    /// Label to match — the rail's own name needs to say which one it's
    /// currently showing.
    public string RfOrSquelchLabel => ModeShowsSquelchNotRF ? "SQL" : "RFG";

    private SubDialTarget _subDialTarget = SubDialTarget.Af;
    public SubDialTarget SubDialTarget { get => _subDialTarget; private set { if (SetProperty(ref _subDialTarget, value)) OnPropertyChanged(nameof(SubDialCurrentValueDisplay)); } }

    /// Whichever of AF Gain/RF Gain/Squelch is currently selected — lets
    /// the XAML show one shared value label next to the -/+ stepper
    /// instead of three always-visible ones.
    public int SubDialCurrentValueDisplay => SubDialTarget switch
    {
        SubDialTarget.Af => AfGain,
        SubDialTarget.Rf => RfGain,
        SubDialTarget.Sql => Squelch,
    };

    /// Same hardware-confirmed mode-family rule as RadioController's own
    /// `ModeShowsSquelchNotRf`: FM/FM-N/C4FM-DN/D-FM/D-FM-N/C4FM-VW show
    /// SQL, every other mode shows RF.
    private static readonly HashSet<char> SquelchModeCodes = new() { '4', 'B', 'H', 'A', 'F', 'I' };
    public bool ModeShowsSquelchNotRF => SquelchModeCodes.Contains(_modeCode);

    private bool IsSubDialTargetValid(SubDialTarget target) => target switch
    {
        SubDialTarget.Af => true,
        SubDialTarget.Rf => !ModeShowsSquelchNotRF,
        SubDialTarget.Sql => ModeShowsSquelchNotRF,
    };

    // MARK: - FUNC Page 1

    public IntSettingViewModel DPeak { get; private set; } = null!;
    public IntSettingViewModel DColor { get; private set; } = null!;
    public IntSettingViewModel DContrast { get; private set; } = null!;
    public IntSettingViewModel DimmerBrightness { get; private set; } = null!;
    public IntSettingViewModel DimmerLed { get; private set; } = null!;
    public IntSettingViewModel ProcLevel { get; private set; } = null!;
    public IntSettingViewModel NoiseBlanker { get; private set; } = null!;
    public IntSettingViewModel NoiseReduction { get; private set; } = null!;
    public IntSettingViewModel MicGain { get; private set; } = null!;
    public IntSettingViewModel AmcLevel { get; private set; } = null!;
    public IntSettingViewModel VoxGain { get; private set; } = null!;
    public IntSettingViewModel VoxDelay { get; private set; } = null!;

    public ToggleSettingViewModel DMarker { get; private set; } = null!;
    public ToggleSettingViewModel Attenuator { get; private set; } = null!;
    public ToggleSettingViewModel AutoNotch { get; private set; } = null!;
    public ToggleSettingViewModel MicEq { get; private set; } = null!;
    public ToggleSettingViewModel Tuner { get; private set; } = null!;
    public ToggleSettingViewModel Vox { get; private set; } = null!;
    public ToggleSettingViewModel Txw { get; private set; } = null!;

    public ChoiceSettingViewModel<int> Agc { get; private set; } = null!;
    public ChoiceSettingViewModel<int> Ant { get; private set; } = null!;

    // MARK: - FUNC Page 2 (CW)

    public IntSettingViewModel MoniLevel { get; private set; } = null!;
    public IntSettingViewModel CwSpeed { get; private set; } = null!;
    public IntSettingViewModel CwPitch { get; private set; } = null!;
    public IntSettingViewModel CwBreakInDelay { get; private set; } = null!;
    public ToggleSettingViewModel Keyer { get; private set; } = null!;
    public ToggleSettingViewModel BreakIn { get; private set; } = null!;
    public ToggleSettingViewModel CwSpot { get; private set; } = null!;
    public ToggleSettingViewModel SdRecording { get; private set; } = null!;
    public RelayCommand ZeroInCommand { get; private set; } = null!;

    /// Keyer (KR) only stays on in CW-L/CW-U — mode codes "7"=CW-L, "3"=CW-U.
    private static readonly HashSet<char> KeyerModeCodes = new() { '7', '3' };
    public bool KeyerAvailable => KeyerModeCodes.Contains(_modeCode);

    // MARK: - MESSAGE MEMORY (LM/PB) — a channel tap plays it back (PB);
    // only MEM arms recording (LM0), with a local 5s countdown mirroring
    // the radio's own arm window (no CAT-readable "armed" flag exists to
    // poll instead). Both LM and PB are real CAT commands, fully wired to
    // RadioController — unlike RECORD/PLAY below, which needs Windows audio
    // support this app doesn't have yet.

    public ObservableCollection<ChoiceOptionViewModel<int>> MessageChannelOptions { get; } = new();

    /// Adapts MessageChannelOptions to the FUNC-grid choice cell's
    /// expected shape (see ChoiceCellViewModel) — same pattern as IpoCell.
    public ChoiceCellViewModel MessageCell { get; } = null!;

    private int _selectedMessageChannel = 1;
    public int SelectedMessageChannel { get => _selectedMessageChannel; private set => SetProperty(ref _selectedMessageChannel, value); }

    private bool _messageArmed;
    public bool MessageArmed { get => _messageArmed; private set => SetProperty(ref _messageArmed, value); }

    private CancellationTokenSource? _messageArmTimeoutCts;

    public RelayCommand MessageArmCommand { get; private set; } = null!;

    // MARK: - RECORD/PLAY/Live Monitor — purely local Windows audio now
    // that the FTX-1's USB-C cable plugs straight into this laptop (its USB
    // audio interface shows up as a normal Windows input device). RECORD
    // drives two independent things at once: the radio's own SD-card CAT
    // recording (SdRecording/LM1, unrelated, lives on the radio, this app
    // can't browse it) AND a local WAV capture (Audio/LiveAudioService +
    // RecordingLibrary) that PLAY lists and plays back. MONITOR (formerly
    // "MAC SPEAKER") is a live passthrough of the same capture stream to
    // this machine's default output device, independent of RECORD.
    public RelayCommand RecordCommand { get; private set; } = null!;
    public RelayCommand RefreshRecordingsCommand { get; private set; } = null!;
    public RelayCommand StopPlaybackCommand { get; private set; } = null!;

    public ObservableCollection<RecordingEntryViewModel> Recordings { get; } = new();

    private RecordingEntryViewModel? _selectedRecording;
    public RecordingEntryViewModel? SelectedRecording { get => _selectedRecording; set => SetProperty(ref _selectedRecording, value); }

    private bool _isPlaying;
    public bool IsPlaying { get => _isPlaying; private set => SetProperty(ref _isPlaying, value); }

    private bool _audioMonitorOn;
    public bool AudioMonitorOn { get => _audioMonitorOn; private set { if (SetProperty(ref _audioMonitorOn, value)) OnPropertyChanged(nameof(AudioMonitorStatusText)); } }
    public string AudioMonitorStatusText => AudioMonitorOn ? "ON" : "OFF";
    public RelayCommand ToggleAudioMonitorCommand { get; private set; } = null!;

    // MARK: - Audio input device (the FTX-1's USB audio interface) —
    // looked up by name at capture time (see LiveAudioService), so a picker
    // is needed since there's no reliable way to auto-identify which input
    // device is the radio versus a built-in mic/webcam.
    public ObservableCollection<string> AvailableAudioInputDevices { get; } = new();

    private string? _selectedAudioInputDevice;
    public string? SelectedAudioInputDevice
    {
        get => _selectedAudioInputDevice;
        set
        {
            if (!SetProperty(ref _selectedAudioInputDevice, value)) return;
            _audio.SelectedInputDeviceName = value;
            _settings.AudioInputDeviceName = value;
            _settings.Save();
        }
    }

    public RelayCommand RefreshAudioDevicesCommand { get; private set; } = null!;

    private void RefreshAudioDevices()
    {
        AvailableAudioInputDevices.Clear();
        foreach (var (_, name) in LiveAudioService.GetInputDevices()) AvailableAudioInputDevices.Add(name);
    }

    // MARK: - Scan/Split
    public ChoiceSettingViewModel<int> Scan { get; private set; } = null!;
    public ToggleSettingViewModel Split { get; private set; } = null!;
    public ChoiceSettingViewModel<bool> TxSide { get; private set; } = null!;
    public ChoiceSettingViewModel<string> RepeaterShift { get; private set; } = null!;
    public ChoiceSettingViewModel<string> ToneType { get; private set; } = null!;
    public IntSettingViewModel CtcssIndex { get; private set; } = null!;
    public IntSettingViewModel DcsIndex { get; private set; } = null!;

    private long _subFrequencyHz;
    public long SubFrequencyHz { get => _subFrequencyHz; private set { if (SetProperty(ref _subFrequencyHz, value)) OnPropertyChanged(nameof(SubFrequencyDisplay)); } }
    public string SubFrequencyDisplay => (_subFrequencyHz / 1_000_000.0).ToString("F6") + " MHz";

    private string _subFrequencyEntryText = "";
    public string SubFrequencyEntryText { get => _subFrequencyEntryText; set => SetProperty(ref _subFrequencyEntryText, value); }

    public RelayCommand SetSubFrequencyCommand { get; private set; } = null!;

    // MARK: - Presets — an app-level save/recall convenience, NOT the
    // FTX-1's own [PRESET] button (that's FT8-specific, unrelated to this).
    public ObservableCollection<PresetViewModel> Presets { get; } = new();

    private string _newPresetName = "";
    // Explicit RaiseCanExecuteChanged rather than relying on WPF's
    // CommandManager auto-requery — unreliable for input inside an
    // AllowsTransparency Popup.
    public string NewPresetName
    {
        get => _newPresetName;
        set { if (SetProperty(ref _newPresetName, value)) SavePresetCommand.RaiseCanExecuteChanged(); }
    }

    public RelayCommand SavePresetCommand { get; private set; } = null!;

    public RelayCommand AntTuneCommand { get; private set; } = null!;
    public RelayCommand DLevelUpCommand { get; private set; } = null!;
    public RelayCommand DLevelDownCommand { get; private set; } = null!;
    public RelayCommand RfPowerUpCommand { get; private set; } = null!;
    public RelayCommand RfPowerDownCommand { get; private set; } = null!;

    /// Per the Advance Manual: Mic EQ is only activated in LSB/USB/AM/
    /// AM-N/FM/FM-N.
    private static readonly HashSet<char> MicEqModeCodes = new() { '1', '2', '5', 'D', '4', 'B' };
    public bool MicEqAvailable => MicEqModeCodes.Contains(_modeCode);

    /// Same HF/50 threshold as RadioController.Dsp.cs's own
    /// `PreampBandForFrequency` (<60MHz = HF/50), used to gate HF ANT
    /// SELECT the same way — not independently confirmed on hardware for
    /// this exact boundary.
    private bool IsHfBand => FrequencyHz < 60_000_000;

    private string CurrentPreampBandCode => FrequencyHz switch
    {
        < 60_000_000 => "0",
        < 300_000_000 => "1",
        _ => "2",
    };

    private string? _lastPreampBandCode;

    public ObservableCollection<ChoiceOptionViewModel<int>> PreampOptions { get; } = new();

    /// Adapts PreampOptions to the FUNC-grid choice cell's expected shape
    /// (see ChoiceCellViewModel) — bound directly by MainWindow.xaml's IPO
    /// cell instead of PreampOptions itself.
    public ChoiceCellViewModel IpoCell { get; }

    private int _preampValue;
    public int PreampValue { get => _preampValue; private set => SetProperty(ref _preampValue, value); }

    private void UpdateIpoCurrentDisplayName()
    {
        IpoCell.CurrentDisplayName = PreampOptions.FirstOrDefault(o => o.IsActive)?.DisplayName;
    }

    /// IPO's option set/meaning depends on which band the radio is
    /// currently on (HF/50: IPO/AMP1/AMP2; VHF/UHF: OFF/ON). Rebuilt only
    /// when the band actually changes (tracked via `_lastPreampBandCode`),
    /// not on every frequency poll tick.
    private void RebuildPreampOptions()
    {
        PreampOptions.Clear();
        var options = CurrentPreampBandCode == "0"
            ? new[] { (0, "IPO"), (1, "AMP1"), (2, "AMP2") }
            : new[] { (0, "OFF"), (1, "ON") };
        foreach (var (value, name) in options)
        {
            PreampOptions.Add(new ChoiceOptionViewModel<int>(value, name, v => _ = SetPreampAsync(v), () => ConnectionState == ConnectionState.Connected));
        }
        UpdateIpoCurrentDisplayName();
    }

    private void UpdatePreampBandIfNeeded()
    {
        var band = CurrentPreampBandCode;
        if (band == _lastPreampBandCode) return;
        _lastPreampBandCode = band;
        RebuildPreampOptions();
        _ = RefreshPreampAsync();
    }

    private async Task RefreshPreampAsync()
    {
        await _radio.RefreshPreampAsync();
        PreampValue = _radio.PreampValue;
        foreach (var o in PreampOptions) o.IsActive = o.Value == PreampValue;
        UpdateIpoCurrentDisplayName();
    }

    private async Task SetPreampAsync(int value)
    {
        await _radio.SetPreampAsync(value);
        PreampValue = _radio.PreampValue;
        foreach (var o in PreampOptions) o.IsActive = o.Value == PreampValue;
        UpdateIpoCurrentDisplayName();
    }

    private double _dLevel;
    public double DLevel { get => _dLevel; private set => SetProperty(ref _dLevel, value); }

    private async Task RefreshDLevelAsync()
    {
        await _radio.RefreshScopeLevelAsync();
        DLevel = _radio.ScopeLevelDb;
    }

    /// Direct "set to this value" for the popup slider (FUNC-grid D-Level
    /// cell) — reuses the existing relative-step logic by computing the
    /// equivalent delta, same trick as SetRfPowerDirectAsync below.
    public Task SetDLevelDirectAsync(double target) => AdjustDLevelAsync(target - DLevel);

    private async Task AdjustDLevelAsync(double delta)
    {
        var target = Math.Clamp(DLevel + delta, -30, 30);
        await _radio.SetScopeLevelAsync(target);
        DLevel = _radio.ScopeLevelDb;
    }

    private int _rfPowerWatts;
    public int RfPowerWatts { get => _rfPowerWatts; private set { if (SetProperty(ref _rfPowerWatts, value)) OnPropertyChanged(nameof(RfPowerMax)); } }
    public int RfPowerMin => 5;
    public int RfPowerMax => _radio.PowerAmpId == 2 ? 100 : 10;

    private async Task RefreshRfPowerAsync()
    {
        await _radio.RefreshRfPowerAsync();
        OnPropertyChanged(nameof(RfPowerMax));
        RfPowerWatts = _radio.PowerWatts;
    }

    /// Direct "set to this value" for the popup slider (FUNC-grid RF Power
    /// cell) — reuses the existing relative-step logic.
    public Task SetRfPowerDirectAsync(int target) => AdjustRfPowerAsync(target - RfPowerWatts);

    private async Task AdjustRfPowerAsync(int delta)
    {
        var target = Math.Clamp(RfPowerWatts + delta, RfPowerMin, RfPowerMax);
        await _radio.SetRfPowerAsync(target);
        OnPropertyChanged(nameof(RfPowerMax));
        RfPowerWatts = _radio.PowerWatts;
    }

    /// Sends the "start tuning" pulse (AC's P3=2, alongside 0=off/1=on for
    /// the Tuner toggle above) — per the Yaesu manual's own "on/off/start"
    /// wording order for this field; not yet independently hardware-confirmed.
    private async Task AntTuneAsync() => await _radio.SetAntennaTunerAsync('2');

    /// Same table as CatCommands.VoxDelayMs/CwBreakInDelayMs.
    private static int VoxDelayMs(int index) => index switch
    {
        0 => 30,
        1 => 50,
        2 => 100,
        3 => 150,
        4 => 200,
        5 => 250,
        _ => 300 + (Math.Clamp(index, 6, 33) - 6) * 100,
    };

    /// Display/Scope half of FUNC Page 1 — split out from TX/Audio core the
    /// same way the Mac app's own refreshFuncTXCore is split from
    /// refreshDisplay/refreshScopeQuickItems, so the poll-loop rotation
    /// (see OnPollTickAsync) has two evenly-sized slots instead of one
    /// large group landing on a single tick.
    private async Task RefreshFuncPage1DisplayGroupAsync()
    {
        await RefreshDLevelAsync();
        await DPeak.RefreshAsync();
        await DMarker.RefreshAsync();
        await DColor.RefreshAsync();
        await DContrast.RefreshAsync();
        await DimmerBrightness.RefreshAsync();
        await DimmerLed.RefreshAsync();
    }

    private async Task RefreshFuncPage1TXCoreGroupAsync()
    {
        await Attenuator.RefreshAsync();
        await RefreshPreampAsync();
        await AutoNotch.RefreshAsync();
        await Agc.RefreshAsync();
        await MicEq.RefreshAsync();
        await ProcLevel.RefreshAsync();
        await Tuner.RefreshAsync();
        await NoiseBlanker.RefreshAsync();
        await NoiseReduction.RefreshAsync();
        await Ant.RefreshAsync();
        await Txw.RefreshAsync();
        await RefreshRfPowerAsync();
        await MicGain.RefreshAsync();
        await AmcLevel.RefreshAsync();
        await Vox.RefreshAsync();
        await VoxGain.RefreshAsync();
        await VoxDelay.RefreshAsync();
    }

    /// Full pass across every FUNC Page 1 control — used right after
    /// connecting so the UI reflects actual radio state immediately rather
    /// than waiting out the poll loop's rotation.
    private async Task RefreshFuncPage1Async()
    {
        await RefreshFuncPage1DisplayGroupAsync();
        await RefreshFuncPage1TXCoreGroupAsync();
    }

    // MARK: - FUNC Page 2 (CW)

    private async Task RefreshFuncPage2Async()
    {
        await MoniLevel.RefreshAsync();
        await CwSpeed.RefreshAsync();
        await CwPitch.RefreshAsync();
        await CwBreakInDelay.RefreshAsync();
        await Keyer.RefreshAsync();
        await BreakIn.RefreshAsync();
        await CwSpot.RefreshAsync();
        await SdRecording.RefreshAsync();
        await RefreshMessageMemoryAsync();
        await RefreshRecordingsAsync();
        await RefreshPlaybackStatusAsync();
        await RefreshAudioMonitorAsync();
    }

    private async Task RefreshMessageMemoryAsync()
    {
        await _radio.RefreshVoiceMessageChannelAsync();
        // channel 0 means "nothing selected" (stopped) — only apply a real
        // (>0) selection.
        var ch = _radio.VoiceMessageChannel;
        if (ch <= 0) return;
        SelectedMessageChannel = ch;
        foreach (var o in MessageChannelOptions) o.IsActive = o.Value == ch;
        MessageCell.CurrentDisplayName = ch.ToString();
    }

    private async Task ZeroInAsync() => await _radio.TriggerZeroInAsync();

    /// A channel tap plays it back (PB) — unless MEM is currently armed, in
    /// which case tapping a different channel re-targets the recording
    /// (LM0 for the new channel) and restarts the countdown instead of
    /// previewing, matching the radio's own re-arm behavior (see
    /// ArmMessageRecording).
    private void SelectAndPlayMessageChannel(int channel)
    {
        SelectedMessageChannel = channel;
        foreach (var o in MessageChannelOptions) o.IsActive = o.Value == channel;
        MessageCell.CurrentDisplayName = channel.ToString();
        if (MessageArmed) ArmMessageRecording();
        else _ = _radio.SetMessagePlaybackAsync(channel);
    }

    private void ArmMessageRecording()
    {
        _ = _radio.SetVoiceMessageChannelAsync(SelectedMessageChannel);
        MessageArmed = true;
        _ = RunMessageArmTimeoutAsync();
    }

    private async Task RunMessageArmTimeoutAsync()
    {
        _messageArmTimeoutCts?.Cancel();
        var cts = new CancellationTokenSource();
        _messageArmTimeoutCts = cts;
        try
        {
            await Task.Delay(5000, cts.Token);
            MessageArmed = false;
        }
        catch (TaskCanceledException)
        {
        }
    }

    // MARK: - RECORD/PLAY/Live Monitor — RECORD drives two independent
    // things: the radio's own SD-card CAT recording (SdRecording/LM1) and a
    // local WAV capture via LiveAudioService; PLAY/Recordings only ever
    // touch the local WAV files (RecordingLibrary), never the radio's SD
    // card, which this app has no way to browse over USB.

    private Task ToggleRecordAsync()
    {
        var turningOn = !SdRecording.IsOn;
        SdRecording.ToggleCommand.Execute(null);
        try
        {
            if (turningOn)
            {
                var path = RecordingLibrary.ReserveNewFilePath(out var id);
                _currentRecordingId = id;
                _recordingStartFrequencyHz = FrequencyHz;
                _recordingStartMode = ModeDisplayName;
                _audio.StartRecording(path);
            }
            else if (_currentRecordingId is { } id)
            {
                var duration = _audio.StopRecording();
                RecordingLibrary.SaveMetadata(id, new RecordingMetadata
                {
                    TimestampUtc = DateTimeOffset.UtcNow,
                    FrequencyHz = (int)_recordingStartFrequencyHz,
                    Mode = _recordingStartMode,
                    DurationSeconds = duration.TotalSeconds,
                });
                _currentRecordingId = null;
                _ = RefreshRecordingsAsync();
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _currentRecordingId = null;
        }
        return Task.CompletedTask;
    }

    private Task ToggleAudioMonitorAsync()
    {
        try
        {
            if (_audio.IsMonitoring)
            {
                _audio.StopMonitoring();
                AudioMonitorOn = false;
            }
            else
            {
                _audio.StartMonitoring();
                AudioMonitorOn = true;
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        return Task.CompletedTask;
    }

    private Task RefreshAudioMonitorAsync()
    {
        AudioMonitorOn = _audio.IsMonitoring;
        return Task.CompletedTask;
    }

    private Task RefreshRecordingsAsync()
    {
        var selectedId = SelectedRecording?.Id;
        Recordings.Clear();
        foreach (var (id, meta) in RecordingLibrary.List())
        {
            Recordings.Add(new RecordingEntryViewModel(
                id, meta.TimestampUtc, meta.FrequencyHz, meta.Mode, meta.DurationSeconds,
                r => _ = PlayRecordingAsync(r), r => _ = DeleteRecordingAsync(r)));
        }
        if (selectedId != null) SelectedRecording = Recordings.FirstOrDefault(r => r.Id == selectedId);
        return Task.CompletedTask;
    }

    private Task PlayRecordingAsync(RecordingEntryViewModel recording)
    {
        SelectedRecording = recording;
        try
        {
            _player.Play(RecordingLibrary.WavPath(recording.Id), () => IsPlaying = false);
            IsPlaying = true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        return Task.CompletedTask;
    }

    private Task DeleteRecordingAsync(RecordingEntryViewModel recording)
    {
        if (SelectedRecording == recording)
        {
            _player.Stop();
            IsPlaying = false;
        }
        RecordingLibrary.Delete(recording.Id);
        Recordings.Remove(recording);
        if (SelectedRecording == recording) SelectedRecording = null;
        return Task.CompletedTask;
    }

    private Task StopPlaybackAsync()
    {
        _player.Stop();
        IsPlaying = false;
        return Task.CompletedTask;
    }

    private Task RefreshPlaybackStatusAsync()
    {
        IsPlaying = _player.IsPlaying;
        return Task.CompletedTask;
    }

    // MARK: - Scan/Split

    private async Task RefreshScanSplitAsync()
    {
        await Scan.RefreshAsync();
        await Split.RefreshAsync();
        await TxSide.RefreshAsync();
        await RepeaterShift.RefreshAsync();
        await ToneType.RefreshAsync();
        await CtcssIndex.RefreshAsync();
        await DcsIndex.RefreshAsync();
        await RefreshSubFrequencyAsync();
    }

    private async Task RefreshSubFrequencyAsync()
    {
        await _radio.RefreshSubFrequencyAsync();
        SubFrequencyHz = _radio.SubFrequencyHz;
    }

    private async Task SetSubFrequencyAsync()
    {
        if (!double.TryParse(SubFrequencyEntryText, out var mhz)) return;
        var hz = (int)Math.Round(mhz * 1_000_000);
        await _radio.SetSubFrequencyAsync(hz);
        SubFrequencyHz = _radio.SubFrequencyHz;
    }

    // MARK: - Presets (property declarations above, near SetSubFrequencyCommand)

    /// Every setting worth snapshotting that's wrapped in an
    /// Int/Toggle/ChoiceSettingViewModel — FUNC Page 1/2 sliders and
    /// toggles, AGC/ANT, and Scan/Split. Frequency/Mode/D-Level/RF Power/
    /// Preamp aren't wrapped in one of those (they're bespoke properties)
    /// and so are captured/applied separately below.
    ///
    /// TxSide is deliberately excluded — confirmed on hardware that issuing
    /// a TX-side Set at all switches the radio's currently-active VFO
    /// (MAIN/SUB), not just which side transmits during split. That's a
    /// much bigger, more visible effect than "restore the split TX-side
    /// setting" is supposed to have, so Presets leaves MAIN/SUB selection
    /// alone entirely rather than risk silently flipping it on every recall.
    private IEnumerable<ISettingBinding> AllPresetSettings => new ISettingBinding[]
    {
        DPeak, DColor, DContrast, DimmerBrightness, DimmerLed, ProcLevel,
        NoiseBlanker, NoiseReduction, MicGain, AmcLevel, VoxGain, VoxDelay,
        DMarker, Attenuator, AutoNotch, MicEq, Tuner, Vox, Txw,
        Agc, Ant,
        MoniLevel, CwSpeed, CwPitch, CwBreakInDelay,
        Keyer, BreakIn, CwSpot,
        Scan, Split, RepeaterShift, ToneType, CtcssIndex, DcsIndex,
    };

    private async Task SavePresetAsync()
    {
        var name = NewPresetName.Trim();
        if (name.Length == 0) return;

        var data = new PresetData
        {
            Name = name,
            FrequencyHz = FrequencyHz,
            ModeCode = _modeCode,
            DLevel = DLevel,
            RfPowerWatts = RfPowerWatts,
            PreampValue = PreampValue,
        };
        foreach (var setting in AllPresetSettings)
        {
            var raw = await setting.CaptureRawAsync();
            if (raw != null) data.Values[setting.Id] = raw;
        }

        _settings.Presets.Add(data);
        _settings.Save();
        Presets.Add(MakePresetViewModel(data));
        NewPresetName = "";
    }

    /// Order matters: mode/frequency first (several other settings — Mic
    /// EQ/Keyer availability, ANT's HF gate, Preamp's band-dependent option
    /// set — depend on them), then the bespoke scalars, then everything
    /// else via ISettingBinding.
    private async Task RecallPresetAsync(PresetData data)
    {
        if (data.ModeCode is char modeCode)
        {
            var mode = RadioModeInfo.All.FirstOrDefault(m => m.CatCode() == modeCode);
            await SetModeAsync(mode);
        }
        if (data.FrequencyHz is long freq)
        {
            await _radio.SetFrequencyAsync((int)freq);
            FrequencyHz = _radio.FrequencyHz;
            UpdateBandHighlight();
            UpdatePreampBandIfNeeded();
        }
        if (data.DLevel is double dLevel)
        {
            await _radio.SetScopeLevelAsync(dLevel);
            DLevel = _radio.ScopeLevelDb;
        }
        if (data.RfPowerWatts is int watts)
        {
            await _radio.SetRfPowerAsync(watts);
            RfPowerWatts = _radio.PowerWatts;
        }
        if (data.PreampValue is int preamp) await SetPreampAsync(preamp);

        foreach (var setting in AllPresetSettings)
        {
            if (data.Values.TryGetValue(setting.Id, out var raw)) await setting.ApplyRawAsync(raw);
        }
    }

    private void DeletePresetData(PresetData data)
    {
        _settings.Presets.Remove(data);
        _settings.Save();
        var match = Presets.FirstOrDefault(p => p.Data == data);
        if (match != null) Presets.Remove(match);
    }

    private PresetViewModel MakePresetViewModel(PresetData data) =>
        new(data, () => RecallPresetAsync(data), () => DeletePresetData(data));

    public ObservableCollection<ModeButtonViewModel> ModeButtons { get; }
    public ObservableCollection<BandButtonViewModel> BandButtons { get; }

    public RelayCommand RefreshPortsCommand { get; }
    public RelayCommand ConnectCommand { get; }
    public RelayCommand DisconnectCommand { get; }
    public RelayCommand SetFrequencyCommand { get; }
    public RelayCommand TogglePttCommand { get; }
    public RelayCommand CycleClarifierCommand { get; }
    public RelayCommand ClarifierOffsetUpCommand { get; }
    public RelayCommand ClarifierOffsetDownCommand { get; }
    public RelayCommand CycleFineTuningCommand { get; }
    public RelayCommand QmbRecallCommand { get; }
    public RelayCommand QmbStoreCommand { get; }
    public RelayCommand VfoStepUpCommand { get; }
    public RelayCommand VfoStepDownCommand { get; }
    public RelayCommand SelectAfCommand { get; }
    public RelayCommand SelectRfCommand { get; }
    public RelayCommand SelectSqlCommand { get; }
    public RelayCommand SubDialUpCommand { get; }
    public RelayCommand SubDialDownCommand { get; }

    private bool CanConnect() =>
        ConnectionState != ConnectionState.Connecting
        && !string.IsNullOrWhiteSpace(SelectedCat1Port)
        && int.TryParse(Cat1Baud, out _);

    /// Every RelayCommand whose CanExecute depends on ConnectionState needs
    /// to be listed here explicitly — WPF's CommandManager auto-requery
    /// (which would otherwise re-evaluate CanExecute on its own) only fires
    /// on certain routed UI events and isn't reliable enough to depend on
    /// (same reasoning as NewPresetName's own explicit RaiseCanExecuteChanged
    /// call). AntTuneCommand was missing from this list — confirmed on
    /// hardware 2026-09-06 that its button just never became clickable
    /// after connecting, since nothing ever told it to re-check CanExecute.
    private void RaiseCanExecuteChanged()
    {
        RefreshPortsCommand.RaiseCanExecuteChanged();
        ConnectCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
        SetFrequencyCommand.RaiseCanExecuteChanged();
        TogglePttCommand.RaiseCanExecuteChanged();
        CycleClarifierCommand.RaiseCanExecuteChanged();
        ClarifierOffsetUpCommand.RaiseCanExecuteChanged();
        ClarifierOffsetDownCommand.RaiseCanExecuteChanged();
        CycleFineTuningCommand.RaiseCanExecuteChanged();
        QmbRecallCommand.RaiseCanExecuteChanged();
        QmbStoreCommand.RaiseCanExecuteChanged();
        VfoStepUpCommand.RaiseCanExecuteChanged();
        VfoStepDownCommand.RaiseCanExecuteChanged();
        SubDialUpCommand.RaiseCanExecuteChanged();
        SubDialDownCommand.RaiseCanExecuteChanged();
        SavePresetCommand.RaiseCanExecuteChanged();
        AntTuneCommand.RaiseCanExecuteChanged();
        DLevelUpCommand.RaiseCanExecuteChanged();
        DLevelDownCommand.RaiseCanExecuteChanged();
        RfPowerUpCommand.RaiseCanExecuteChanged();
        RfPowerDownCommand.RaiseCanExecuteChanged();
        ZeroInCommand.RaiseCanExecuteChanged();
        MessageArmCommand.RaiseCanExecuteChanged();
        RecordCommand.RaiseCanExecuteChanged();
        RefreshRecordingsCommand.RaiseCanExecuteChanged();
        StopPlaybackCommand.RaiseCanExecuteChanged();
        ToggleAudioMonitorCommand.RaiseCanExecuteChanged();
        SetSubFrequencyCommand.RaiseCanExecuteChanged();
    }

    // MARK: - Connection

    private Task RefreshPortsAsync()
    {
        AvailablePorts.Clear();
        foreach (var p in SerialPort.GetPortNames().OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) AvailablePorts.Add(p);
        return Task.CompletedTask;
    }

    private async Task ConnectAsync()
    {
        LastError = null;
        if (!int.TryParse(Cat1Baud, out var baud1))
        {
            LastError = "Bad CAT-1 baud rate";
            return;
        }

        WindowsSerialTransport cat1Transport = new(SelectedCat1Port!, baud1);
        WindowsSerialTransport? cat2Transport = null;
        if (!string.IsNullOrWhiteSpace(SelectedCat2Port) && int.TryParse(Cat2Baud, out var baud2))
        {
            cat2Transport = new WindowsSerialTransport(SelectedCat2Port, baud2);
        }

        try
        {
            await _radio.ConnectAsync(cat1Transport, cat2Transport);
        }
        catch (UnauthorizedAccessException)
        {
            // The Windows analog of the Mac app's TIOCEXCL/EBUSY case — the
            // COM port is already open elsewhere (most likely another copy
            // of this app, a terminal program, or Windows itself still
            // settling right after the device was plugged in).
            MessageBox.Show(
                "Can't connect — the selected COM port is already in use by another program.\n\nClose whatever else might have it open, then try again.",
                "Port Busy",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        catch (Exception)
        {
            // ConnectionState/LastError are already updated via the
            // PropertyChanged forwarding subscription in the constructor.
            return;
        }

        if (_radio.ConnectionState != ConnectionState.Connected) return;

        _settings.Cat1Port = SelectedCat1Port;
        _settings.Cat2Port = SelectedCat2Port;
        _settings.Save();

        await RefreshAllAsync();
        StartPolling();
    }

    private async Task DisconnectAsync()
    {
        StopPolling();
        await _radio.DisconnectAsync();
    }

    private async Task SetFrequencyAsync()
    {
        if (!double.TryParse(DirectEntryText, out var mhz)) return;
        var hz = (int)Math.Round(mhz * 1_000_000);
        await _radio.SetFrequencyAsync(hz);
        FrequencyHz = _radio.FrequencyHz;
        UpdateBandHighlight();
        UpdatePreampBandIfNeeded();
    }

    private async Task TogglePttAsync()
    {
        await _radio.SetTransmitAsync(!IsTransmitting);
        IsTransmitting = _radio.IsTransmitting;
    }

    private async Task SetModeAsync(RadioMode mode)
    {
        await _radio.SetModeAsync(mode);
        _modeCode = _radio.ModeCode;
        ModeDisplayName = _radio.ModeDisplayName;
        UpdateModeHighlight();
        OnModeCodeChanged();
    }

    private async Task SetBandAsync(BandCode band)
    {
        await _radio.SetBandAsync(band);
        // BS is fire-and-forget with no read-back (same as the Mac app) —
        // the next poll tick's frequency read will pick up the radio's new
        // frequency and re-derive the band highlight from it.
    }

    private async Task RefreshAllAsync()
    {
        await RefreshFrequencyAsync();
        await RefreshModeAsync();
        await RefreshPttAsync();
        await RefreshMetersAsync();
        await RefreshClarifierAsync();
        await RefreshFineTuningAsync();
        await RefreshSubDialValuesAsync();
        await RefreshFuncPage1Async();
        await RefreshFuncPage2Async();
        await RefreshScanSplitAsync();
    }

    private async Task RefreshFrequencyAsync()
    {
        await _radio.RefreshFrequencyAsync();
        FrequencyHz = _radio.FrequencyHz;
        UpdateBandHighlight();
        UpdatePreampBandIfNeeded();
    }

    private async Task RefreshModeAsync()
    {
        await _radio.RefreshModeAsync();
        _modeCode = _radio.ModeCode;
        ModeDisplayName = _radio.ModeDisplayName;
        UpdateModeHighlight();
        OnModeCodeChanged();
    }

    private async Task RefreshPttAsync()
    {
        await _radio.RefreshPttAsync();
        IsTransmitting = _radio.IsTransmitting;
    }

    private async Task RefreshMetersAsync()
    {
        if (await _radio.RefreshMeterAsync(MeterKind.SMeterMain) is { } s) SMeterValue = s;
        if (IsTransmitting)
        {
            if (await _radio.RefreshMeterAsync(MeterKind.PowerOutput) is { } po) PowerOutputValue = po;
        }
        else
        {
            PowerOutputValue = 0;
        }
    }

    /// The real radio's mode buttons form a 4x5 grid with specific
    /// positions intentionally blank (row 1: 4 modes + 1 blank; rows 2-3:
    /// 5 modes each; row 4: 3 modes + 2 blanks) — RadioMode's own
    /// declaration order (Models/RadioMode.cs) already groups the 17 modes
    /// into exactly these four row sizes, so padding each group to 5 with
    /// ModeButtonViewModel.Blank() reproduces that layout without
    /// hardcoding which specific modes are missing from any row.
    private IEnumerable<ModeButtonViewModel> BuildModeGrid()
    {
        var modes = RadioModeInfo.All;
        var rowSizes = new[] { 4, 5, 5, 3 };
        var index = 0;
        foreach (var rowSize in rowSizes)
        {
            for (var i = 0; i < rowSize; i++)
                yield return new ModeButtonViewModel(modes[index++], code => _ = SetModeAsync(code));
            for (var pad = rowSize; pad < 5; pad++)
                yield return ModeButtonViewModel.Blank();
        }
    }

    private void UpdateModeHighlight()
    {
        foreach (var b in ModeButtons) b.IsActive = b.Mode?.CatCode() == _modeCode;
    }

    private void UpdateBandHighlight()
    {
        var current = BandCodeInfo.FromFrequency(FrequencyHz);
        foreach (var b in BandButtons) b.IsActive = current.HasValue && b.Band == current.Value;
    }

    /// If a mode change makes the current AF/RF/SQL selection invalid (e.g.
    /// RF selected, then the mode switches to an SQL-only one), fall back
    /// to AF rather than silently leaving an inapplicable target selected.
    private void OnModeCodeChanged()
    {
        OnPropertyChanged(nameof(ModeShowsSquelchNotRF));
        OnPropertyChanged(nameof(RfOrSquelchValue));
        OnPropertyChanged(nameof(RfOrSquelchLabel));
        OnPropertyChanged(nameof(MicEqAvailable));
        OnPropertyChanged(nameof(KeyerAvailable));
        if (!IsSubDialTargetValid(SubDialTarget)) SubDialTarget = SubDialTarget.Af;
    }

    // MARK: - Clarifier (CF)

    private async Task CycleClarifierAsync()
    {
        await _radio.CycleClarifierAsync();
        ClarifierRxOn = _radio.ClarifierRxOn;
        ClarifierTxOn = _radio.ClarifierTxOn;
    }

    private async Task AdjustClarifierOffsetAsync(int delta)
    {
        var target = Math.Clamp(ClarifierOffsetHz + delta, -9999, 9999);
        await _radio.SetClarifierFrequencyAsync(target);
        ClarifierOffsetHz = _radio.ClarifierOffsetHz;
    }

    private async Task RefreshClarifierAsync()
    {
        await _radio.RefreshClarifierAsync();
        ClarifierRxOn = _radio.ClarifierRxOn;
        ClarifierTxOn = _radio.ClarifierTxOn;
        ClarifierOffsetHz = _radio.ClarifierOffsetHz;
    }

    // MARK: - Fine/Fast (FN)

    private async Task CycleFineTuningAsync()
    {
        await _radio.CycleFineTuningAsync();
        FineTuningDisplayName = _radio.FineTuningState.DisplayName();
    }

    private async Task RefreshFineTuningAsync()
    {
        await _radio.RefreshFineTuningAsync();
        FineTuningDisplayName = _radio.FineTuningState.DisplayName();
    }

    // MARK: - QMB (QI/QR) — Set-only, no read-back on either side.

    private async Task QmbRecallAsync()
    {
        await _radio.QmbRecallAsync();
        await RefreshFrequencyAsync();
    }

    private async Task QmbStoreAsync() => await _radio.QmbStoreAsync();

    // MARK: - VFO step size (no CAT command of its own — reuses SetFrequency)

    /// Public, properly-awaitable entry point for the VFO dial (unlike
    /// VfoStepUpCommand/DownCommand — RelayCommand.Execute is
    /// fire-and-forget, so the dial's code-behind handler has no way to
    /// know when one CAT round-trip finishes before starting the next.
    /// Firing steps concurrently against a half-duplex single-outstanding-
    /// command serial link is exactly the kind of thing that would back up
    /// badly enough to look like the whole app had locked up.
    public Task StepFrequencyAsync(int direction) => AdjustFrequencyAsync(direction);

    private async Task AdjustFrequencyAsync(int steps)
    {
        var target = (int)(FrequencyHz + steps * (long)_radio.VfoStepHz);
        await _radio.SetFrequencyAsync(target);
        FrequencyHz = _radio.FrequencyHz;
        UpdateBandHighlight();
        UpdatePreampBandIfNeeded();
    }

    // MARK: - MAIN AF/RF/SQL knob

    private void SelectSubDial(SubDialTarget target)
    {
        if (!IsSubDialTargetValid(target)) return;
        SubDialTarget = target;
    }

    private async Task AdjustSubDialAsync(int delta)
    {
        switch (SubDialTarget)
        {
            case SubDialTarget.Af:
                await _radio.SetAfGainAsync(Math.Clamp(AfGain + delta, 0, 255));
                AfGain = _radio.AfGain;
                break;
            case SubDialTarget.Rf:
                await _radio.SetRfGainAsync(Math.Clamp(RfGain + delta, 0, 255));
                RfGain = _radio.RfGain;
                break;
            case SubDialTarget.Sql:
                await _radio.SetSquelchAsync(Math.Clamp(Squelch + delta, 0, 255));
                Squelch = _radio.SquelchLevel;
                break;
        }
    }

    private async Task RefreshSubDialValuesAsync()
    {
        await _radio.RefreshAfGainAsync();
        AfGain = _radio.AfGain;
        await _radio.RefreshRfGainAsync();
        RfGain = _radio.RfGain;
        await _radio.RefreshSquelchAsync();
        Squelch = _radio.SquelchLevel;
    }

    // MARK: - Polling (250ms meters; every 8th tick = 2s also freq/mode/PTT
    // plus one of four rotating groups: FUNC Page 1 Display, FUNC Page 1
    // TX/Audio core, FUNC Page 2 (CW), Scan/Split — same cadence/rotation
    // idea as RadioController.startPolling() in the Mac app, but driven
    // from here since this app's RadioController deliberately doesn't run
    // its own poll loop.

    private void StartPolling()
    {
        _tickCount = 0;
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _pollTimer.Tick += async (_, _) => await OnPollTickAsync();
        _pollTimer.Start();
    }

    private void StopPolling()
    {
        _pollTimer?.Stop();
        _pollTimer = null;
    }

    private async Task OnPollTickAsync()
    {
        if (ConnectionState != ConnectionState.Connected) return;
        await RefreshMetersAsync();
        _tickCount++;
        if (_tickCount >= 8)
        {
            _tickCount = 0;
            await RefreshFrequencyAsync();
            await RefreshModeAsync();
            await RefreshPttAsync();
            await RefreshClarifierAsync();
            await RefreshFineTuningAsync();
            await RefreshSubDialValuesAsync();

            _funcPage1RotationIndex = (_funcPage1RotationIndex + 1) % 4;
            switch (_funcPage1RotationIndex)
            {
                case 0: await RefreshFuncPage1DisplayGroupAsync(); break;
                case 1: await RefreshFuncPage1TXCoreGroupAsync(); break;
                case 2: await RefreshFuncPage2Async(); break;
                default: await RefreshScanSplitAsync(); break;
            }
        }
    }

    public void Dispose()
    {
        StopPolling();
        _ = _radio.DisconnectAsync();
        _audio.Dispose();
        _player.Dispose();
    }
}

public sealed class ModeButtonViewModel : ObservableObject
{
    public RadioMode? Mode { get; }
    public bool IsPlaceholder => Mode is null;
    public string DisplayName { get; }
    public RelayCommand SelectCommand { get; }

    private bool _isActive;
    public bool IsActive { get => _isActive; set => SetProperty(ref _isActive, value); }

    public ModeButtonViewModel(RadioMode mode, Action<RadioMode> onSelect)
    {
        Mode = mode;
        DisplayName = mode.DisplayName();
        SelectCommand = new RelayCommand(() => onSelect(mode));
    }

    /// A blank grid cell — the Mode grid's shape is a fixed 4x5 layout with
    /// specific positions intentionally empty (matching the real radio's
    /// own mode button layout), not a free-flowing wrap of however many
    /// modes happen to exist. See MainWindow.xaml's Mode UniformGrid.
    private ModeButtonViewModel()
    {
        DisplayName = "";
        SelectCommand = new RelayCommand(() => { }, () => false);
    }

    public static ModeButtonViewModel Blank() => new();
}

public sealed class BandButtonViewModel : ObservableObject
{
    public BandCode Band { get; }
    public string DisplayName { get; }
    public RelayCommand SelectCommand { get; }

    private bool _isActive;
    public bool IsActive { get => _isActive; set => SetProperty(ref _isActive, value); }

    public BandButtonViewModel(BandCode band, Action<BandCode> onSelect)
    {
        Band = band;
        DisplayName = band.DisplayName();
        SelectCommand = new RelayCommand(() => onSelect(band));
    }
}

/// Adapts IPO/Preamp (MainViewModel.PreampOptions — dynamic per-band
/// options, no wrapping ChoiceSettingViewModel since its value/options
/// depend on the current band) to the same Label/CurrentDisplayName/
/// Options shape ChoiceSettingViewModel&lt;T&gt; exposes, so the FUNC-grid
/// choice cell (FuncChoiceCellTemplate) can bind either one uniformly.
public sealed class ChoiceCellViewModel : ObservableObject
{
    public string Label { get; }
    public ObservableCollection<ChoiceOptionViewModel<int>> Options { get; }

    private string? _currentDisplayName;
    public string? CurrentDisplayName { get => _currentDisplayName; set => SetProperty(ref _currentDisplayName, value); }

    public ChoiceCellViewModel(string label, ObservableCollection<ChoiceOptionViewModel<int>> options)
    {
        Label = label;
        Options = options;
    }
}

/// One saved Preset — see MainViewModel's own Presets region for what
/// Save/Recall/Delete actually do.
public sealed class PresetViewModel : ObservableObject
{
    public FTX1WinController.Settings.PresetData Data { get; }
    public string Name => Data.Name;
    public RelayCommand RecallCommand { get; }
    public RelayCommand DeleteCommand { get; }

    public PresetViewModel(FTX1WinController.Settings.PresetData data, Func<Task> onRecall, Action onDelete)
    {
        Data = data;
        RecallCommand = new RelayCommand(async () => await onRecall());
        DeleteCommand = new RelayCommand(onDelete);
    }
}

/// One row in the Recordings list — inert until Windows audio support
/// exists (see this file's RECORD/PLAY/Live Monitor region).
public sealed class RecordingEntryViewModel
{
    public string Id { get; }
    public string DisplayName { get; }
    public RelayCommand PlayCommand { get; }
    public RelayCommand DeleteCommand { get; }

    public RecordingEntryViewModel(string id, DateTimeOffset timestamp, int frequencyHz, string mode, double durationSeconds,
        Action<RecordingEntryViewModel> onPlay, Action<RecordingEntryViewModel>? onDelete)
    {
        Id = id;
        DisplayName = $"{timestamp.LocalDateTime:yyyy-MM-dd HH:mm} — {frequencyHz / 1_000_000.0:F3}MHz {mode} ({durationSeconds:F0}s)";
        PlayCommand = new RelayCommand(() => onPlay(this));
        DeleteCommand = new RelayCommand(() => onDelete?.Invoke(this), () => onDelete != null);
    }
}
