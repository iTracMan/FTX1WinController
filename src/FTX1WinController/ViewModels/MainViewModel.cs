using System.Collections.ObjectModel;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using FTX1WinController.Bridge;
using FTX1WinController.Models;
using FTX1WinController.Settings;

namespace FTX1WinController.ViewModels;

public enum ConnectionState { Disconnected, Connecting, Connected, Failed }

/// MAIN AF/RF/SQL knob's current target — mirrors VFODialPanelView's own
/// `SubDialTarget` in the Mac app.
public enum SubDialTarget { Af, Rf, Sql }

/// The "RadioController" equivalent for this app — except every property
/// here is filled in by asking the Mac-side bridge, never by talking CAT
/// directly. Poll cadence (250ms meters, every 8th tick = 2s for
/// freq/mode/PTT) mirrors RadioController.startPolling() in the Mac app.
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly BridgeClient _bridge = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private DispatcherTimer? _pollTimer;
    private int _tickCount;
    private int _funcPage1RotationIndex;

    public MainViewModel()
    {
        _bridgeHost = _settings.BridgeHost ?? "";
        _selectedCat1Port = _settings.Cat1Port;
        _selectedCat2Port = _settings.Cat2Port;

        ModeButtons = new ObservableCollection<ModeButtonViewModel>(
            RadioModeInfo.All.Select(m => new ModeButtonViewModel(m, code => _ = SetModeAsync(code))));
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
        bool Connected() => ConnectionState == ConnectionState.Connected;
        void OnError(string msg) => LastError = msg;

        DPeak = new IntSettingViewModel(_bridge, "D-Peak", "SCOPEPEAK", 0, 4, 1, Connected, OnError) { Format = v => $"LV{v + 1}" };
        DColor = new IntSettingViewModel(_bridge, "D-Color", "SCOPECOLOR", 0, 10, 1, Connected, OnError) { Format = v => $"COLOR-{v + 1}" };
        DContrast = new IntSettingViewModel(_bridge, "D-Contrast", "DISPLAYCONTRAST", 0, 20, 1, Connected, OnError);
        DimmerBrightness = new IntSettingViewModel(_bridge, "Dimmer (TFT)", "DISPLAYBRIGHTNESS", 0, 20, 1, Connected, OnError);
        DimmerLed = new IntSettingViewModel(_bridge, "Dimmer (LED)", "DISPLAYLED", 0, 20, 1, Connected, OnError);
        ProcLevel = new IntSettingViewModel(_bridge, "Proc Level", "PROCLEVEL", 0, 100, 1, Connected, OnError);
        NoiseBlanker = new IntSettingViewModel(_bridge, "NB", "NB", 0, 10, 1, Connected, OnError) { Format = v => v == 0 ? "OFF" : v.ToString() };
        NoiseReduction = new IntSettingViewModel(_bridge, "DNR", "NR", 0, 10, 1, Connected, OnError) { Format = v => v == 0 ? "OFF" : v.ToString() };
        MicGain = new IntSettingViewModel(_bridge, "Mic Gain", "MICGAIN", 0, 100, 1, Connected, OnError);
        AmcLevel = new IntSettingViewModel(_bridge, "AMC Level", "AMCLEVEL", 1, 100, 1, Connected, OnError);
        VoxGain = new IntSettingViewModel(_bridge, "VOX Gain", "VOXGAIN", 0, 100, 1, Connected, OnError);
        VoxDelay = new IntSettingViewModel(_bridge, "VOX Delay", "VOXDELAY", 0, 33, 1, Connected, OnError) { Format = v => $"{VoxDelayMs(v)}ms" };

        DMarker = new ToggleSettingViewModel(_bridge, "D-Marker", "SCOPEMARKER", Connected, OnError);
        Attenuator = new ToggleSettingViewModel(_bridge, "ATT", "ATT", Connected, OnError);
        AutoNotch = new ToggleSettingViewModel(_bridge, "DNF", "DNF", Connected, OnError);
        MicEq = new ToggleSettingViewModel(_bridge, "Mic EQ", "MICEQ", () => Connected() && MicEqAvailable, OnError);
        Tuner = new ToggleSettingViewModel(_bridge, "Tuner", "TUNER", Connected, OnError);
        Vox = new ToggleSettingViewModel(_bridge, "VOX", "VOX", Connected, OnError);
        // No Split tracking yet (that's a later round) — TXW is left
        // ungated for now; on real hardware it only actually takes effect
        // once Split is on (confirmed 2026-09-03: SET TXW 1 returns OK but
        // reads back OFF again with Split off), matching the Mac app's own
        // UI, which already disables this button unless splitOn.
        Txw = new ToggleSettingViewModel(_bridge, "TXW", "TXW", Connected, OnError);

        Agc = new ChoiceSettingViewModel<int>(
            _bridge, "AGC", "AGC",
            // Mirrors the radio's own on-screen button order (OFF/AUTO/
            // FAST/MID/SLOW), not AGCMode's raw declaration order.
            new[] { (0, "OFF"), (4, "AUTO"), (1, "FAST"), (2, "MID"), (3, "SLOW") },
            v => v.ToString(), s => (int.TryParse(s, out var v), v),
            Connected, OnError);

        Ant = new ChoiceSettingViewModel<int>(
            _bridge, "ANT", "HFANT",
            new[] { (0, "ANT1"), (1, "ANT2") },
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
        MoniLevel = new IntSettingViewModel(_bridge, "Moni Level", "MONITORLEVEL", 0, 100, 1, Connected, OnError);
        CwSpeed = new IntSettingViewModel(_bridge, "CW Speed", "KEYSPEED", 4, 60, 1, Connected, OnError) { Format = v => $"{v}wpm" };
        CwPitch = new IntSettingViewModel(_bridge, "CW Pitch", "KEYPITCH", 300, 1050, 10, Connected, OnError) { Format = v => $"{v}Hz" };
        // Same 0-33 index -> ms table as VOX Delay (see VoxDelayMs) — the
        // manual documents both with identical irregular-then-100ms-step
        // values, confirmed 1:1 against the Mac app's own
        // CATProtocolV3.cwBreakInDelayTable.
        CwBreakInDelay = new IntSettingViewModel(_bridge, "BK-Delay", "CWBREAKINDELAY", 0, 33, 1, Connected, OnError) { Format = v => $"{VoxDelayMs(v)}ms" };

        Keyer = new ToggleSettingViewModel(_bridge, "Keyer", "KEYER", () => Connected() && KeyerAvailable, OnError);
        BreakIn = new ToggleSettingViewModel(_bridge, "BK-IN", "BREAKIN", Connected, OnError);
        CwSpot = new ToggleSettingViewModel(_bridge, "CW Spot", "CWSPOT", Connected, OnError);
        SdRecording = new ToggleSettingViewModel(_bridge, "Record", "SDRECORDING", Connected, OnError);

        ZeroInCommand = new RelayCommand(async () => await ZeroInAsync(), Connected);
        MessageArmCommand = new RelayCommand(ArmMessageRecording, Connected);
        RecordCommand = new RelayCommand(async () => await ToggleRecordAsync(), Connected);
        RefreshRecordingsCommand = new RelayCommand(async () => await RefreshRecordingsAsync(), Connected);
        StopPlaybackCommand = new RelayCommand(async () => await StopPlaybackAsync(), Connected);
        ToggleAudioMonitorCommand = new RelayCommand(async () => await ToggleAudioMonitorAsync(), Connected);

        for (var ch = 1; ch <= 5; ch++)
        {
            var channel = ch;
            MessageChannelOptions.Add(new ChoiceOptionViewModel<int>(channel, channel.ToString(), SelectAndPlayMessageChannel, Connected));
        }
        MessageCell = new ChoiceCellViewModel("MESSAGE", MessageChannelOptions);

        // Scan/Split — ported from OperatingPanelView.swift.
        Scan = new ChoiceSettingViewModel<int>(
            _bridge, "Scan", "SCAN",
            new[] { (0, "Stop"), (1, "Up"), (2, "Down") },
            v => v.ToString(), s => (int.TryParse(s, out var v), v),
            Connected, OnError);
        Split = new ToggleSettingViewModel(_bridge, "Split", "SPLIT", Connected, OnError);
        TxSide = new ChoiceSettingViewModel<bool>(
            _bridge, "TX", "TXSIDE",
            new[] { (true, "MAIN"), (false, "SUB") },
            v => v ? "0" : "1", s => (s == "0" || s == "1", s == "0"),
            Connected, OnError);
        RepeaterShift = new ChoiceSettingViewModel<string>(
            _bridge, "Shift", "REPEATERSHIFT",
            new[] { ("0", "SIMPLEX"), ("1", "+"), ("2", "−"), ("3", "ARS") },
            v => v, s => (true, s),
            Connected, OnError);
        ToneType = new ChoiceSettingViewModel<string>(
            _bridge, "Type", "TONETYPE",
            new[] { ("0", "OFF"), ("1", "ENC"), ("2", "ENC+DEC"), ("3", "DCS") },
            v => v, s => (true, s),
            Connected, OnError);
        CtcssIndex = new IntSettingViewModel(_bridge, "CTCSS Tone", "CTCSSINDEX", 0, ToneTables.CtcssHz.Length - 1, 1, Connected, OnError) { Format = ToneTables.CtcssLabel };
        DcsIndex = new IntSettingViewModel(_bridge, "DCS Code", "DCSINDEX", 0, ToneTables.DcsCodes.Length - 1, 1, Connected, OnError) { Format = ToneTables.DcsLabel };
        SetSubFrequencyCommand = new RelayCommand(async () => await SetSubFrequencyAsync(), Connected);

        SavePresetCommand = new RelayCommand(async () => await SavePresetAsync(), () => Connected() && !string.IsNullOrWhiteSpace(NewPresetName));
        foreach (var data in _settings.Presets) Presets.Add(MakePresetViewModel(data));
    }

    // MARK: - Bridge connection fields (Mac host/port)

    private string _bridgeHost;
    public string BridgeHost { get => _bridgeHost; set => SetProperty(ref _bridgeHost, value); }

    private string _bridgePort = "5150";
    public string BridgePort { get => _bridgePort; set => SetProperty(ref _bridgePort, value); }

    // MARK: - CAT-1/CAT-2 port fields (Mac-side serial device paths, from PORTS)

    public ObservableCollection<string> AvailablePorts { get; } = new();

    private string? _selectedCat1Port;
    public string? SelectedCat1Port { get => _selectedCat1Port; set => SetProperty(ref _selectedCat1Port, value); }

    private string _cat1Baud = "38400";
    public string Cat1Baud { get => _cat1Baud; set => SetProperty(ref _cat1Baud, value); }

    private string? _selectedCat2Port;
    public string? SelectedCat2Port { get => _selectedCat2Port; set => SetProperty(ref _selectedCat2Port, value); }

    private string _cat2Baud = "4800";
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
    /// currently showing (confirmed 2026-09-04: a static "RFG" label
    /// stayed wrong once SQL-mode kicked in).
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

    /// Same hardware-confirmed mode-family rule as the Mac app's own
    /// `modeShowsSquelchNotRF`: FM/FM-N/C4FM-DN/D-FM/D-FM-N/C4FM-VW show
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

    /// Keyer (KR) only stays on in CW-L/CW-U — ported 1:1 from
    /// RadioController.swift's own `keyerAvailable` (mode codes "7"=CW-L,
    /// "3"=CW-U).
    private static readonly HashSet<char> KeyerModeCodes = new() { '7', '3' };
    public bool KeyerAvailable => KeyerModeCodes.Contains(_modeCode);

    // MARK: - MESSAGE MEMORY (LM/PB) — see FTX1Bridge's RadioBridge.swift
    // for the hard-won LM/PB semantics this mirrors: a channel tap plays
    // it back (PB); only MEM arms recording (LM0), with a local 5s
    // countdown mirroring the radio's own arm window (no CAT-readable
    // "armed" flag exists to poll instead, same caveat as the Mac app).

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

    // MARK: - RECORD/PLAY — the Mac app's own RECORD button already does
    // two things at once on every press: toggles the radio's own SD-card
    // recording (LM1/SDRECORDING) AND starts/stops a Mac-local capture of
    // the same received audio via the FTX-1's USB audio interface
    // (FuncPage2PanelView.sdRecordButton). `RecordCommand` mirrors that
    // exact dual action — `SdRecording` alone (below) only covers the
    // radio's own SD card half. PLAY has no Windows-side audio at all:
    // selecting a recording and pressing Play plays it back on the Mac's
    // own speakers (SET PLAY <id>), since only the Mac has a USB audio
    // path to the radio — there is nothing for this laptop to stream.
    public RelayCommand RecordCommand { get; private set; } = null!;
    public RelayCommand RefreshRecordingsCommand { get; private set; } = null!;
    public RelayCommand StopPlaybackCommand { get; private set; } = null!;

    public ObservableCollection<RecordingEntryViewModel> Recordings { get; } = new();

    private RecordingEntryViewModel? _selectedRecording;
    public RecordingEntryViewModel? SelectedRecording { get => _selectedRecording; set => SetProperty(ref _selectedRecording, value); }

    private bool _isPlayingOnMac;
    public bool IsPlayingOnMac { get => _isPlayingOnMac; private set => SetProperty(ref _isPlayingOnMac, value); }

    // MARK: - Live audio monitor — routes the radio's received audio
    // (already arriving on the Mac over its USB-C connection to the radio,
    // same feed RECORD taps) out to the Mac's own speakers/soundbar.
    // Purely a Mac-side playback switch — nothing for this Windows machine
    // to play, same reasoning as PLAY above. Independent of RECORD/PLAY in
    // all combinations (Mac-side confirmed 2026-09-05). Bridge protocol is
    // GET/SET MONITOR START/STOP rather than the generic ToggleSettingViewModel
    // shape (SET <NAME> 0|1), so it's a bespoke pair like ToggleRecordAsync
    // rather than a plain ToggleSettingViewModel instance.
    private bool _audioMonitorOn;
    public bool AudioMonitorOn { get => _audioMonitorOn; private set { if (SetProperty(ref _audioMonitorOn, value)) OnPropertyChanged(nameof(AudioMonitorStatusText)); } }
    public string AudioMonitorStatusText => AudioMonitorOn ? "ON" : "OFF";
    public RelayCommand ToggleAudioMonitorCommand { get; private set; } = null!;

    // MARK: - Scan/Split — ported from OperatingPanelView.swift.
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
    // FTX-1's own [PRESET] button (that's FT8-specific — see the CAT
    // Operation Reference Manual p.51-52 — and unrelated to this). Captures
    // every IBridgeSetting-implementing control plus the handful of
    // bespoke ones (frequency/mode/D-Level/RF Power/Preamp) into a named
    // PresetData, persisted via AppSettings the same way connection fields
    // already are.
    public ObservableCollection<PresetViewModel> Presets { get; } = new();

    private string _newPresetName = "";
    // Explicit RaiseCanExecuteChanged rather than relying on WPF's
    // CommandManager auto-requery — that auto-requery is unreliable for
    // input inside an AllowsTransparency Popup (confirmed 2026-09-04: the
    // Save button stayed disabled for every preset after the first, since
    // typing into the popover's TextBox never triggered a requery).
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
    /// AM-N/FM/FM-N — ported 1:1 from RadioControllerV3.swift's own
    /// `micEQAvailable`.
    private static readonly HashSet<char> MicEqModeCodes = new() { '1', '2', '5', 'D', '4', 'B' };
    public bool MicEqAvailable => MicEqModeCodes.Contains(_modeCode);

    /// Same HF/50 threshold as RadioControllerV2.swift's own `preampBand`
    /// (<60MHz = HF/50), used to gate HF ANT SELECT the same way the Mac
    /// app does — not independently confirmed on hardware for this exact
    /// boundary, same caveat as the Mac app's own comment.
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
    /// currently on (HF/50: IPO/AMP1/AMP2; VHF/UHF: OFF/ON) — ported 1:1
    /// from CATProtocolV2.swift's `PreampBand`. Rebuilt only when the band
    /// actually changes (tracked via `_lastPreampBandCode`), not on every
    /// frequency poll tick, to avoid needlessly recreating the buttons.
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
        try
        {
            var band = CurrentPreampBandCode;
            var reply = await _bridge.SendAsync($"GET PREAMP {band}");
            var parts = reply.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && parts[0] == "PREAMP" && int.TryParse(parts[2], out var v))
            {
                PreampValue = v;
                foreach (var o in PreampOptions) o.IsActive = o.Value == v;
                UpdateIpoCurrentDisplayName();
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private async Task SetPreampAsync(int value)
    {
        try
        {
            var band = CurrentPreampBandCode;
            var reply = await _bridge.SendAsync($"SET PREAMP {band} {value}");
            if (reply == "OK")
            {
                PreampValue = value;
                foreach (var o in PreampOptions) o.IsActive = o.Value == value;
                UpdateIpoCurrentDisplayName();
            }
            else
            {
                LastError = reply;
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private double _dLevel;
    public double DLevel { get => _dLevel; private set => SetProperty(ref _dLevel, value); }

    private async Task RefreshDLevelAsync()
    {
        try
        {
            var reply = await _bridge.SendAsync("GET SCOPELEVEL");
            var parts = reply.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // Invariant culture — the wire protocol always uses "." as the
            // decimal separator regardless of the Windows machine's locale.
            if (parts.Length == 2 && parts[0] == "SCOPELEVEL" && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) DLevel = v;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    /// Direct "set to this value" for the popup slider (FUNC-grid D-Level
    /// cell) — reuses the existing relative-step logic by computing the
    /// equivalent delta, same trick as SetRfPowerDirectAsync below.
    public Task SetDLevelDirectAsync(double target) => AdjustDLevelAsync(target - DLevel);

    private async Task AdjustDLevelAsync(double delta)
    {
        var target = Math.Clamp(DLevel + delta, -30, 30);
        try
        {
            var reply = await _bridge.SendAsync($"SET SCOPELEVEL {target.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}");
            if (reply == "OK") DLevel = target;
            else LastError = reply;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private int _powerAmpP1 = 2;
    private int _rfPowerWatts;
    public int RfPowerWatts { get => _rfPowerWatts; private set { if (SetProperty(ref _rfPowerWatts, value)) OnPropertyChanged(nameof(RfPowerMax)); } }
    public int RfPowerMin => 5;
    public int RfPowerMax => _powerAmpP1 == 2 ? 100 : 10;

    private async Task RefreshRfPowerAsync()
    {
        try
        {
            var reply = await _bridge.SendAsync("GET POWER");
            var parts = reply.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && parts[0] == "POWER" && int.TryParse(parts[1], out var amp) && int.TryParse(parts[2], out var watts))
            {
                _powerAmpP1 = amp;
                OnPropertyChanged(nameof(RfPowerMax));
                RfPowerWatts = watts;
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    /// Direct "set to this value" for the popup slider (FUNC-grid RF Power
    /// cell) — reuses the existing relative-step logic.
    public Task SetRfPowerDirectAsync(int target) => AdjustRfPowerAsync(target - RfPowerWatts);

    private async Task AdjustRfPowerAsync(int delta)
    {
        var target = Math.Clamp(RfPowerWatts + delta, RfPowerMin, RfPowerMax);
        try
        {
            var reply = await _bridge.SendAsync($"SET POWER {target}");
            if (reply == "OK") RfPowerWatts = target;
            else LastError = reply;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private async Task AntTuneAsync()
    {
        try
        {
            var reply = await _bridge.SendAsync("SET TUNER START");
            if (reply != "OK") LastError = reply;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    /// Ported 1:1 from CATProtocolV3.swift's own `voxDelayMs(forIndex:)`.
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
    /// 24-read group landing on a single tick.
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
    /// than waiting out the poll loop's rotation. The rotation itself (see
    /// OnPollTickAsync) refreshes one group at a time instead of calling
    /// this on every slow tick.
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
        try
        {
            var reply = await _bridge.SendAsync("GET VOICEMESSAGECHANNEL");
            var parts = reply.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // channel 0 means "nothing selected" (stopped) — same rule as
            // the Mac app's own refreshVoiceMessage, only apply a real
            // (>0) selection.
            if (parts.Length == 2 && parts[0] == "VOICEMESSAGECHANNEL" && int.TryParse(parts[1], out var ch) && ch > 0)
            {
                SelectedMessageChannel = ch;
                foreach (var o in MessageChannelOptions) o.IsActive = o.Value == ch;
                MessageCell.CurrentDisplayName = ch.ToString();
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private async Task ZeroInAsync()
    {
        try
        {
            var reply = await _bridge.SendAsync("SET ZIN TRIGGER");
            if (reply != "OK") LastError = reply;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    /// A channel tap plays it back (PB) — unless MEM is currently armed,
    /// in which case tapping a different channel re-targets the recording
    /// (LM0 for the new channel) and restarts the countdown instead of
    /// previewing, matching the radio's own re-arm behavior (see
    /// ArmMessageRecording).
    private void SelectAndPlayMessageChannel(int channel)
    {
        SelectedMessageChannel = channel;
        foreach (var o in MessageChannelOptions) o.IsActive = o.Value == channel;
        MessageCell.CurrentDisplayName = channel.ToString();
        if (MessageArmed) ArmMessageRecording();
        else _ = SetMessagePlaybackAsync(channel);
    }

    private async Task SetMessagePlaybackAsync(int channel)
    {
        try
        {
            var reply = await _bridge.SendAsync($"SET MESSAGEPLAYBACK {channel}");
            if (reply != "OK") LastError = reply;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private void ArmMessageRecording()
    {
        _ = SetVoiceMessageChannelAsync(SelectedMessageChannel);
        MessageArmed = true;
        _ = RunMessageArmTimeoutAsync();
    }

    private async Task SetVoiceMessageChannelAsync(int channel)
    {
        try
        {
            var reply = await _bridge.SendAsync($"SET VOICEMESSAGECHANNEL {channel}");
            if (reply != "OK") LastError = reply;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
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

    // MARK: - RECORD/PLAY

    /// Mirrors the Mac app's own sdRecordButton exactly: one press toggles
    /// both the radio's own SD-card recording AND the Mac-local capture
    /// together, in that order — `SdRecording`'s own ToggleCommand handles
    /// the SD-card half (and its IsOn is what this button's style/label
    /// reflect), this just adds the second half on top of it.
    private async Task ToggleRecordAsync()
    {
        var turningOn = !SdRecording.IsOn;
        SdRecording.ToggleCommand.Execute(null);
        try
        {
            var reply = await _bridge.SendAsync(turningOn ? "SET RECORD START" : "SET RECORD STOP");
            if (reply != "OK") LastError = reply;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private async Task ToggleAudioMonitorAsync()
    {
        var turningOn = !AudioMonitorOn;
        try
        {
            var reply = await _bridge.SendAsync(turningOn ? "SET MONITOR START" : "SET MONITOR STOP");
            if (reply == "OK") AudioMonitorOn = turningOn;
            else LastError = reply;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private async Task RefreshAudioMonitorAsync()
    {
        try
        {
            var reply = await _bridge.SendAsync("GET MONITOR");
            var parts = reply.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0] == "MONITOR") AudioMonitorOn = parts[1] == "1";
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private async Task RefreshRecordingsAsync()
    {
        try
        {
            var reply = await _bridge.SendAsync("GET RECORDINGS");
            var parts = reply.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // "RECORDINGS <count> <id>|<epochSeconds>|<freqHz>|<mode>|<durationSeconds> ..."
            if (parts.Length < 2 || parts[0] != "RECORDINGS" || !int.TryParse(parts[1], out var count)) return;

            var selectedId = SelectedRecording?.Id;
            Recordings.Clear();
            for (var i = 0; i < count && i + 2 < parts.Length; i++)
            {
                var fields = parts[i + 2].Split('|');
                if (fields.Length != 5) continue;
                if (!long.TryParse(fields[1], out var epochSeconds)) continue;
                if (!int.TryParse(fields[2], out var freqHz)) continue;
                var mode = fields[3].Replace('_', ' ');
                if (!double.TryParse(fields[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var duration)) continue;
                var timestamp = DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
                Recordings.Add(new RecordingEntryViewModel(fields[0], timestamp, freqHz, mode, duration, r => _ = PlayRecordingAsync(r), r => _ = DeleteRecordingAsync(r)));
            }
            // Restore the selection across a refresh (by id) rather than
            // letting it silently drop to null every ~2s poll tick.
            if (selectedId != null) SelectedRecording = Recordings.FirstOrDefault(r => r.Id == selectedId);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    /// Backs each PLAY LIST entry's own PlayCommand — plays that specific
    /// recording and tracks it as SelectedRecording so Stop still targets
    /// the right one.
    private async Task PlayRecordingAsync(RecordingEntryViewModel recording)
    {
        SelectedRecording = recording;
        try
        {
            var reply = await _bridge.SendAsync($"SET PLAY {recording.Id}");
            if (reply != "OK") LastError = reply;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    /// Backs each PLAY LIST entry's own DeleteCommand — permanently
    /// removes that recording from the Mac (not the radio's own SD card;
    /// RECORD captures locally on the Mac via ReceivedAudioRecorder, same
    /// as the Mac app's own button — see MainViewModel's RECORD/PLAY
    /// comment). Added to the bridge 2026-09-04: "SET DELETE &lt;id&gt;" ->
    /// OK, or "ERR recording not found: &lt;id&gt;" / "ERR bad recording id:
    /// &lt;id&gt;" on failure.
    private async Task DeleteRecordingAsync(RecordingEntryViewModel recording)
    {
        try
        {
            var reply = await _bridge.SendAsync($"SET DELETE {recording.Id}");
            if (reply == "OK")
            {
                Recordings.Remove(recording);
                if (SelectedRecording == recording) SelectedRecording = null;
            }
            else
            {
                LastError = reply;
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private async Task StopPlaybackAsync()
    {
        try
        {
            var reply = await _bridge.SendAsync("SET PLAY STOP");
            if (reply != "OK") LastError = reply;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private async Task RefreshPlaybackStatusAsync()
    {
        try
        {
            var reply = await _bridge.SendAsync("GET PLAY");
            var parts = reply.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && parts[0] == "PLAY") IsPlayingOnMac = parts[2] == "1";
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
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
        try
        {
            var reply = await _bridge.SendAsync("GET SUBFREQ");
            var parts = reply.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0] == "SUBFREQ" && long.TryParse(parts[1], out var hz)) SubFrequencyHz = hz;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private async Task SetSubFrequencyAsync()
    {
        if (!double.TryParse(SubFrequencyEntryText, out var mhz)) return;
        var hz = (long)Math.Round(mhz * 1_000_000);
        try
        {
            var reply = await _bridge.SendAsync($"SET SUBFREQ {hz}");
            if (reply == "OK") SubFrequencyHz = hz;
            else LastError = reply;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    // MARK: - Presets (property declarations above, near SetSubFrequencyCommand)

    /// Every setting worth snapshotting that's wrapped in an
    /// Int/Toggle/ChoiceSettingViewModel — FUNC Page 1/2 sliders and
    /// toggles, AGC/ANT, and Scan/Split. Frequency/Mode/D-Level/RF Power/
    /// Preamp aren't wrapped in one of those (they're bespoke properties)
    /// and so are captured/applied separately below.
    ///
    /// TxSide (bridge name TXSIDE) is deliberately excluded — confirmed on
    /// hardware 2026-09-04 that issuing "SET TXSIDE" at all switches the
    /// radio's currently-active VFO (MAIN/SUB), not just which side
    /// transmits during split. That's a much bigger, more visible effect
    /// than "restore the split TX-side setting" is supposed to have, so
    /// Presets leaves MAIN/SUB selection alone entirely rather than risk
    /// silently flipping it on every recall.
    private IEnumerable<IBridgeSetting> AllPresetSettings => new IBridgeSetting[]
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
            if (raw != null) data.Values[setting.BridgeName] = raw;
        }

        _settings.Presets.Add(data);
        _settings.Save();
        Presets.Add(MakePresetViewModel(data));
        NewPresetName = "";
    }

    /// Order matters: mode/frequency first (several other settings — Mic
    /// EQ/Keyer availability, ANT's HF gate, Preamp's band-dependent option
    /// set — depend on them), then the bespoke scalars, then everything
    /// else via IBridgeSetting.
    private async Task RecallPresetAsync(PresetData data)
    {
        try
        {
            if (data.ModeCode is char modeCode)
            {
                var mode = RadioModeInfo.All.FirstOrDefault(m => m.CatCode() == modeCode);
                await SetModeAsync(mode);
            }
            if (data.FrequencyHz is long freq)
            {
                var reply = await _bridge.SendAsync($"SET FREQ {freq}");
                if (reply == "OK") { FrequencyHz = freq; UpdateBandHighlight(); UpdatePreampBandIfNeeded(); }
                else LastError = reply;
            }
            if (data.DLevel is double dLevel)
            {
                var reply = await _bridge.SendAsync($"SET SCOPELEVEL {dLevel.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}");
                if (reply == "OK") DLevel = dLevel; else LastError = reply;
            }
            if (data.RfPowerWatts is int watts)
            {
                var reply = await _bridge.SendAsync($"SET POWER {watts}");
                if (reply == "OK") RfPowerWatts = watts; else LastError = reply;
            }
            if (data.PreampValue is int preamp) await SetPreampAsync(preamp);

            foreach (var setting in AllPresetSettings)
            {
                if (data.Values.TryGetValue(setting.BridgeName, out var raw)) await setting.ApplyRawAsync(raw);
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
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
        && !string.IsNullOrWhiteSpace(BridgeHost)
        && int.TryParse(BridgePort, out _)
        && !string.IsNullOrWhiteSpace(SelectedCat1Port)
        && int.TryParse(Cat1Baud, out _);

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
    }

    // MARK: - Bridge operations

    private async Task EnsureBridgeOpenAsync()
    {
        if (_bridge.IsOpen) return;
        if (!int.TryParse(BridgePort, out var port)) throw new ArgumentException("Bad bridge port");
        await _bridge.OpenAsync(BridgeHost, port);
    }

    private async Task RefreshPortsAsync()
    {
        try
        {
            await EnsureBridgeOpenAsync();
            var reply = await _bridge.SendAsync("PORTS");
            var parts = reply.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            AvailablePorts.Clear();
            foreach (var p in parts.Skip(1)) AvailablePorts.Add(p);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private async Task ConnectAsync()
    {
        ConnectionState = ConnectionState.Connecting;
        LastError = null;
        try
        {
            await EnsureBridgeOpenAsync();

            var cmd = $"CONNECT {SelectedCat1Port} {Cat1Baud}";
            if (!string.IsNullOrWhiteSpace(SelectedCat2Port) && int.TryParse(Cat2Baud, out _))
            {
                cmd += $" {SelectedCat2Port} {Cat2Baud}";
            }

            var reply = await _bridge.SendAsync(cmd);
            if (reply != "OK")
            {
                ConnectionState = ConnectionState.Failed;
                LastError = reply;

                // Distinguished from other CONNECT failures (bad port, bad
                // baud, permission denied) by a stable "ERR PORT_BUSY:"
                // prefix (Mac-side spec confirmed 2026-09-05) — the radio's
                // serial port can only be held by one process at a time,
                // and FTX1Controller (the Mac GUI app) is the other most
                // likely holder since it duplicates the bridge's own
                // connection logic. A popup here (rather than just the
                // small red LastError text) makes sure this specific,
                // easy-to-miss cause is actually noticed.
                if (reply.StartsWith("ERR PORT_BUSY:", StringComparison.Ordinal))
                {
                    MessageBox.Show(
                        "Can't connect — the radio's serial port is already in use, most likely by FTX1Controller running on the Mac.\n\nDisconnect it there first, then try again.",
                        "Port Busy",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                return;
            }

            ConnectionState = ConnectionState.Connected;

            _settings.BridgeHost = BridgeHost;
            _settings.Cat1Port = SelectedCat1Port;
            _settings.Cat2Port = SelectedCat2Port;
            _settings.Save();

            await RefreshAllAsync();
            StartPolling();
        }
        catch (Exception ex)
        {
            ConnectionState = ConnectionState.Failed;
            LastError = ex.Message;
        }
    }

    private async Task DisconnectAsync()
    {
        StopPolling();
        try
        {
            await _bridge.SendAsync("DISCONNECT");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        finally
        {
            ConnectionState = ConnectionState.Disconnected;
        }
    }

    private async Task SetFrequencyAsync()
    {
        if (!double.TryParse(DirectEntryText, out var mhz)) return;
        var hz = (long)Math.Round(mhz * 1_000_000);
        try
        {
            var reply = await _bridge.SendAsync($"SET FREQ {hz}");
            if (reply == "OK")
            {
                FrequencyHz = hz;
                UpdateBandHighlight();
                UpdatePreampBandIfNeeded();
            }
            else
            {
                LastError = reply;
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private async Task TogglePttAsync()
    {
        try
        {
            var target = !IsTransmitting;
            var reply = await _bridge.SendAsync($"SET PTT {(target ? 1 : 0)}");
            if (reply == "OK") IsTransmitting = target;
            else LastError = reply;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private async Task SetModeAsync(RadioMode mode)
    {
        try
        {
            var reply = await _bridge.SendAsync($"SET MODE {mode.CatCode()}");
            if (reply == "OK")
            {
                _modeCode = mode.CatCode();
                ModeDisplayName = mode.DisplayName();
                UpdateModeHighlight();
                OnModeCodeChanged();
            }
            else
            {
                LastError = reply;
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private async Task SetBandAsync(BandCode band)
    {
        try
        {
            var reply = await _bridge.SendAsync($"SET BAND {band.Code()}");
            if (reply != "OK") LastError = reply;
            // BS is fire-and-forget with no read-back (same as the Mac
            // app) — the next poll tick's frequency read will pick up the
            // radio's new frequency and re-derive the band highlight from
            // it, same as BandCodeInfo.FromFrequency does everywhere else.
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
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
        try
        {
            var reply = await _bridge.SendAsync("GET FREQ");
            // "FREQ <hz>"
            var parts = reply.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0] == "FREQ" && long.TryParse(parts[1], out var hz))
            {
                FrequencyHz = hz;
                UpdateBandHighlight();
                UpdatePreampBandIfNeeded();
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private async Task RefreshModeAsync()
    {
        try
        {
            var reply = await _bridge.SendAsync("GET MODE");
            // "MODE <code> <displayName...>"
            var parts = reply.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && parts[0] == "MODE" && parts[1].Length == 1)
            {
                _modeCode = parts[1][0];
                ModeDisplayName = parts[2];
                UpdateModeHighlight();
                OnModeCodeChanged();
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private async Task RefreshPttAsync()
    {
        try
        {
            var reply = await _bridge.SendAsync("GET PTT");
            var parts = reply.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0] == "PTT")
            {
                IsTransmitting = parts[1] == "1";
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private async Task RefreshMetersAsync()
    {
        try
        {
            var reply = await _bridge.SendAsync("GET METERS");
            // "METERS S<val> PO<val>"
            var parts = reply.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && parts[0] == "METERS")
            {
                if (parts[1].StartsWith("S") && int.TryParse(parts[1].AsSpan(1), out var s)) SMeterValue = s;
                if (parts[2].StartsWith("PO") && int.TryParse(parts[2].AsSpan(2), out var po)) PowerOutputValue = po;
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private void UpdateModeHighlight()
    {
        foreach (var b in ModeButtons) b.IsActive = b.Mode.CatCode() == _modeCode;
    }

    private void UpdateBandHighlight()
    {
        var current = BandCodeInfo.FromFrequency(FrequencyHz);
        foreach (var b in BandButtons) b.IsActive = current.HasValue && b.Band == current.Value;
    }

    /// Same reaction as the Mac app's own `onChange(of: radio.modeCode)` —
    /// if a mode change makes the current AF/RF/SQL selection invalid (e.g.
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
        try
        {
            var reply = await _bridge.SendAsync("SET CLAR CYCLE");
            if (reply == "OK") await RefreshClarifierAsync();
            else LastError = reply;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private async Task AdjustClarifierOffsetAsync(int delta)
    {
        try
        {
            var target = Math.Clamp(ClarifierOffsetHz + delta, -9999, 9999);
            var reply = await _bridge.SendAsync($"SET CLAR OFFSET {target}");
            if (reply == "OK") ClarifierOffsetHz = target;
            else LastError = reply;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private async Task RefreshClarifierAsync()
    {
        try
        {
            var reply = await _bridge.SendAsync("GET CLAR");
            // "CLAR <rx 0/1> <tx 0/1> <offsetHz>"
            var parts = reply.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4 && parts[0] == "CLAR")
            {
                ClarifierRxOn = parts[1] == "1";
                ClarifierTxOn = parts[2] == "1";
                if (int.TryParse(parts[3], out var offset)) ClarifierOffsetHz = offset;
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    // MARK: - Fine/Fast (FN)

    private async Task CycleFineTuningAsync()
    {
        try
        {
            var reply = await _bridge.SendAsync("SET FINE CYCLE");
            if (reply == "OK") await RefreshFineTuningAsync();
            else LastError = reply;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private async Task RefreshFineTuningAsync()
    {
        try
        {
            var reply = await _bridge.SendAsync("GET FINE");
            var parts = reply.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0] == "FINE") FineTuningDisplayName = parts[1];
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    // MARK: - QMB (QI/QR) — Set-only, no read-back on either side.

    private async Task QmbRecallAsync()
    {
        try
        {
            var reply = await _bridge.SendAsync("SET QMB RECALL");
            if (reply == "OK") await RefreshFrequencyAsync();
            else LastError = reply;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private async Task QmbStoreAsync()
    {
        try
        {
            var reply = await _bridge.SendAsync("SET QMB STORE");
            if (reply != "OK") LastError = reply;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    // MARK: - VFO step size (no new bridge command — reuses SET FREQ)

    /// Ported 1:1 from RadioController.swift's own `vfoStepHz` — step size
    /// depends on the current mode's "fine step group" and the FINE/FAST
    /// state, exactly matching the physical VFO knob rather than a fixed
    /// step.
    private static readonly HashSet<char> FineStepGroupA = new() { '1', '2', '3', '7', '8', 'C', '6', '9', 'E' };

    private int VfoStepHz
    {
        get
        {
            var isGroupA = FineStepGroupA.Contains(_modeCode);
            return (isGroupA, FineTuningDisplayName) switch
            {
                (true, "OFF") => 20,
                (true, "FINE") => 1,
                (true, "FAST") => 200,
                (false, "OFF") => 100,
                (false, "FINE") => 10,
                (false, "FAST") => 1000,
                _ => isGroupA ? 20 : 100,
            };
        }
    }

    /// Public, properly-awaitable entry point for the VFO dial (unlike
    /// VfoStepUpCommand/DownCommand — RelayCommand.Execute is
    /// fire-and-forget, so the dial's code-behind handler has no way to
    /// know when one bridge round-trip finishes before starting the next.
    /// Confirmed on hardware 2026-09-04: firing steps concurrently instead
    /// of one-at-a-time backed up the bridge's semaphore badly enough to
    /// look like the whole app had locked up.
    public Task StepFrequencyAsync(int direction) => AdjustFrequencyAsync(direction);

    private async Task AdjustFrequencyAsync(int steps)
    {
        var target = FrequencyHz + steps * VfoStepHz;
        try
        {
            var reply = await _bridge.SendAsync($"SET FREQ {target}");
            if (reply == "OK")
            {
                FrequencyHz = target;
                UpdateBandHighlight();
                UpdatePreampBandIfNeeded();
            }
            else
            {
                LastError = reply;
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    // MARK: - MAIN AF/RF/SQL knob

    private void SelectSubDial(SubDialTarget target)
    {
        if (!IsSubDialTargetValid(target)) return;
        SubDialTarget = target;
    }

    private async Task AdjustSubDialAsync(int delta)
    {
        try
        {
            switch (SubDialTarget)
            {
                case SubDialTarget.Af:
                    var af = Math.Clamp(AfGain + delta, 0, 255);
                    var afReply = await _bridge.SendAsync($"SET AFGAIN {af}");
                    if (afReply == "OK") AfGain = af; else LastError = afReply;
                    break;
                case SubDialTarget.Rf:
                    var rf = Math.Clamp(RfGain + delta, 0, 255);
                    var rfReply = await _bridge.SendAsync($"SET RFGAIN {rf}");
                    if (rfReply == "OK") RfGain = rf; else LastError = rfReply;
                    break;
                case SubDialTarget.Sql:
                    var sq = Math.Clamp(Squelch + delta, 0, 255);
                    var sqReply = await _bridge.SendAsync($"SET SQUELCH {sq}");
                    if (sqReply == "OK") Squelch = sq; else LastError = sqReply;
                    break;
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private async Task RefreshSubDialValuesAsync()
    {
        try
        {
            var afReply = await _bridge.SendAsync("GET AFGAIN");
            var afParts = afReply.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (afParts.Length == 2 && afParts[0] == "AFGAIN" && int.TryParse(afParts[1], out var af)) AfGain = af;

            var rfReply = await _bridge.SendAsync("GET RFGAIN");
            var rfParts = rfReply.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (rfParts.Length == 2 && rfParts[0] == "RFGAIN" && int.TryParse(rfParts[1], out var rf)) RfGain = rf;

            var sqReply = await _bridge.SendAsync("GET SQUELCH");
            var sqParts = sqReply.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (sqParts.Length == 2 && sqParts[0] == "SQUELCH" && int.TryParse(sqParts[1], out var sq)) Squelch = sq;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    // MARK: - Polling (250ms meters; every 8th tick = 2s also freq/mode/PTT
    // plus one of four rotating groups: FUNC Page 1 Display, FUNC Page 1
    // TX/Audio core, FUNC Page 2 (CW), Scan/Split — same cadence/rotation idea
    // as RadioController.startPolling() in the Mac app. FUNC Page 1 used to
    // only be read once, right after connect, so any change made from the
    // radio's own front panel (not via CAT) was never picked back up —
    // confirmed on hardware 2026-09-03 for D-Level/D-Peak/D-Color, which
    // all share this poll loop.)

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
        _ = _bridge.DisposeAsync();
    }
}

public sealed class ModeButtonViewModel : ObservableObject
{
    public RadioMode Mode { get; }
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

/// One row in the Recordings list — a Mac-local audio file, listed via
/// "GET RECORDINGS" (see MainViewModel's own RECORD/PLAY comment). `Id` is
/// the recording's UUID exactly as the bridge reports it, opaque to this
/// app beyond round-tripping it back in "SET PLAY <id>".
public sealed class RecordingEntryViewModel
{
    public string Id { get; }
    public string DisplayName { get; }
    public RelayCommand PlayCommand { get; }

    /// Backed by "SET DELETE &lt;id&gt;" (added to the bridge 2026-09-04).
    /// `onDelete` is nullable/CanExecute-gated rather than assumed non-null
    /// so a caller without a real delete handler still gets a safely
    /// disabled button instead of a NullReferenceException.
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
