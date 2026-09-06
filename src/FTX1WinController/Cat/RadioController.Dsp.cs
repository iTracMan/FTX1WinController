using FTX1WinController.Models;

namespace FTX1WinController.Cat;

/// v2: DSP/audio, RF, and operating (scan/split/repeater/tone) state and
/// methods. Ported from the Mac app's RadioControllerV2.swift (a Swift
/// `extension RadioController`) — here, a partial-class file instead.
public sealed partial class RadioController
{
    // MARK: - Noise Reduction (RL)

    private int _noiseReductionLevel;
    public int NoiseReductionLevel { get => _noiseReductionLevel; private set => SetProperty(ref _noiseReductionLevel, value); }

    public Task SetNoiseReductionAsync(int level) =>
        SendSetAsync(CatCommands.SetNoiseReduction(level), () => NoiseReductionLevel = CatCommands.Clamp(level, 0, 10));

    public Task RefreshNoiseReductionAsync() =>
        FetchAndApplyAsync(CatCommands.ReadNoiseReduction(), CatCommands.ParseNoiseReduction, v => NoiseReductionLevel = v);

    // MARK: - Auto Notch (BC)

    private bool _autoNotchOn;
    public bool AutoNotchOn { get => _autoNotchOn; private set => SetProperty(ref _autoNotchOn, value); }

    public Task SetAutoNotchAsync(bool on) => SendSetAsync(CatCommands.SetAutoNotch(on), () => AutoNotchOn = on);

    public Task RefreshAutoNotchAsync() =>
        FetchAndApplyAsync(CatCommands.ReadAutoNotch(), CatCommands.ParseAutoNotch, v => AutoNotchOn = v);

    // MARK: - Manual Notch (BP) — compound on/off + frequency

    private bool _manualNotchOn;
    public bool ManualNotchOn { get => _manualNotchOn; private set => SetProperty(ref _manualNotchOn, value); }

    private int _manualNotchFrequencyHz = 1000;
    public int ManualNotchFrequencyHz { get => _manualNotchFrequencyHz; private set => SetProperty(ref _manualNotchFrequencyHz, value); }

    public Task SetManualNotchAsync(bool on) => SendSetAsync(CatCommands.SetManualNotch(on), () => ManualNotchOn = on);

    public Task SetManualNotchFrequencyAsync(int hz) =>
        SendSetAsync(CatCommands.SetManualNotchFrequency(hz), () => ManualNotchFrequencyHz = CatCommands.Clamp(hz, 10, 3200) / 10 * 10);

    public async Task RefreshManualNotchAsync()
    {
        string stateReply, freqReply;
        try
        {
            stateReply = await Cat1.SendAsync(CatCommands.ReadManualNotchState());
            freqReply = await Cat1.SendAsync(CatCommands.ReadManualNotchFrequency());
        }
        catch { return; }

        if (CatCommands.ParseManualNotch(stateReply, out var isFrequency1, out var value1) && !isFrequency1)
            ManualNotchOn = value1 != 0;
        if (CatCommands.ParseManualNotch(freqReply, out var isFrequency2, out var value2) && isFrequency2)
            ManualNotchFrequencyHz = value2;
    }

    // MARK: - RF Attenuator (RA)

    private bool _rfAttenuatorOn;
    public bool RfAttenuatorOn { get => _rfAttenuatorOn; private set => SetProperty(ref _rfAttenuatorOn, value); }

    public Task SetRfAttenuatorAsync(bool on) => SendSetAsync(CatCommands.SetAttenuator(on), () => RfAttenuatorOn = on);

    public Task RefreshRfAttenuatorAsync() =>
        FetchAndApplyAsync(CatCommands.ReadAttenuator(), CatCommands.ParseAttenuator, v => RfAttenuatorOn = v);

    // MARK: - Preamp / IPO (PA)

    /// Which PA band applies right now, derived from FrequencyHz — PA's
    /// meaning (IPO/AMP1/AMP2 vs. plain on/off) depends on which band the
    /// radio is currently on.
    public CatCommands.PreampBand PreampBandForFrequency
    {
        get
        {
            if (FrequencyHz < 60_000_000) return CatCommands.PreampBand.Hf50;
            if (FrequencyHz < 300_000_000) return CatCommands.PreampBand.Vhf;
            return CatCommands.PreampBand.Uhf;
        }
    }

    private int _preampValue;
    public int PreampValue { get => _preampValue; private set => SetProperty(ref _preampValue, value); }

    public Task SetPreampAsync(int value)
    {
        var band = PreampBandForFrequency;
        return SendSetAsync(CatCommands.SetPreamp(band, value), () => PreampValue = value);
    }

    public async Task RefreshPreampAsync()
    {
        var band = PreampBandForFrequency;
        string reply;
        try { reply = await Cat1.SendAsync(CatCommands.ReadPreamp(band)); }
        catch { return; }
        if (CatCommands.ParsePreamp(reply, out _, out var value) && int.TryParse(value.ToString(), out var v))
            PreampValue = v;
    }

    // MARK: - AGC (GT)

    private AgcMode _agcMode = AgcMode.Off;
    public AgcMode AgcMode { get => _agcMode; private set => SetProperty(ref _agcMode, value); }

    public Task SetAgcAsync(AgcMode mode) => SendSetAsync(CatCommands.SetAgc(mode), () => AgcMode = mode);

    public Task RefreshAgcAsync() =>
        FetchAndApplyAsync(CatCommands.ReadAgc(), CatCommands.ParseAgc, v => AgcMode = v);

    // MARK: - RF Gain (RG)

    private int _rfGain = 255;
    public int RfGain { get => _rfGain; private set => SetProperty(ref _rfGain, value); }

    public Task SetRfGainAsync(int value) =>
        SendSetAsync(CatCommands.SetRfGain(value), () => RfGain = CatCommands.Clamp(value, 0, 255));

    public Task RefreshRfGainAsync() =>
        FetchAndApplyAsync(CatCommands.ReadRfGain(), CatCommands.ParseRfGain, v => RfGain = v);

    // MARK: - Squelch (SQ)

    private int _squelchLevel;
    public int SquelchLevel { get => _squelchLevel; private set => SetProperty(ref _squelchLevel, value); }

    public Task SetSquelchAsync(int value) =>
        SendSetAsync(CatCommands.SetSquelch(value), () => SquelchLevel = CatCommands.Clamp(value, 0, 255));

    public Task RefreshSquelchAsync() =>
        FetchAndApplyAsync(CatCommands.ReadSquelch(), CatCommands.ParseSquelch, v => SquelchLevel = v);

    // MARK: - Power Control (PC) — "ask, don't assume" which amp is fitted

    private int _powerAmpId = 1;
    public int PowerAmpId { get => _powerAmpId; private set => SetProperty(ref _powerAmpId, value); }

    private int _powerWatts = 5;
    public int PowerWatts { get => _powerWatts; private set => SetProperty(ref _powerWatts, value); }

    public Task SetRfPowerAsync(int watts)
    {
        var amp = PowerAmpId;
        var range = amp == 2 ? (lo: 5, hi: 100) : (lo: 5, hi: 10);
        return SendSetAsync(CatCommands.SetPower(amp, watts), () => PowerWatts = CatCommands.Clamp(watts, range.lo, range.hi));
    }

    public async Task RefreshRfPowerAsync()
    {
        string reply;
        try { reply = await Cat1.SendAsync(CatCommands.ReadPower()); }
        catch { return; }
        if (CatCommands.ParsePower(reply, out var amp, out var watts))
        {
            PowerAmpId = amp;
            PowerWatts = watts;
        }
    }

    // MARK: - Scan (SC)

    private CatCommands.ScanState _scanState = CatCommands.ScanState.Off;
    public CatCommands.ScanState ScanState { get => _scanState; private set => SetProperty(ref _scanState, value); }

    public Task SetScanAsync(CatCommands.ScanState state) => SendSetAsync(CatCommands.SetScan(state), () => ScanState = state);

    public Task RefreshScanAsync() =>
        FetchAndApplyAsync(CatCommands.ReadScan(), CatCommands.ParseScan, v => ScanState = v);

    // MARK: - Split (ST)

    private bool _splitOn;
    public bool SplitOn { get => _splitOn; private set => SetProperty(ref _splitOn, value); }

    public Task SetSplitAsync(bool on) => SendSetAsync(CatCommands.SetSplit(on), () => SplitOn = on);

    public Task RefreshSplitAsync() =>
        FetchAndApplyAsync(CatCommands.ReadSplit(), CatCommands.ParseSplit, v => SplitOn = v);

    // MARK: - VFO-B / sub frequency (FB)

    private int _subFrequencyHz = 14_250_000;
    public int SubFrequencyHz { get => _subFrequencyHz; private set => SetProperty(ref _subFrequencyHz, value); }

    public Task SetSubFrequencyAsync(int hz) =>
        SendSetAsync(CatCommands.SetSubFrequency(hz), () => SubFrequencyHz = CatCommands.Clamp(hz, 30_000, 470_000_000));

    public Task RefreshSubFrequencyAsync() =>
        FetchAndApplyAsync(CatCommands.ReadSubFrequency(), CatCommands.ParseSubFrequency, v => SubFrequencyHz = v);

    // MARK: - TX side (FT)

    private bool _txSideIsMain = true;
    public bool TxSideIsMain { get => _txSideIsMain; private set => SetProperty(ref _txSideIsMain, value); }

    public Task SetTxSideAsync(bool mainSide) => SendSetAsync(CatCommands.SetTxSide(mainSide), () => TxSideIsMain = mainSide);

    public Task RefreshTxSideAsync() =>
        FetchAndApplyAsync(CatCommands.ReadTxSide(), CatCommands.ParseTxSide, v => TxSideIsMain = v);

    // MARK: - Repeater shift (OS)

    private RepeaterShift _repeaterShift = RepeaterShift.Simplex;
    public RepeaterShift RepeaterShift { get => _repeaterShift; private set => SetProperty(ref _repeaterShift, value); }

    public Task SetRepeaterShiftAsync(RepeaterShift shift) => SendSetAsync(CatCommands.SetRepeaterShift(shift), () => RepeaterShift = shift);

    public Task RefreshRepeaterShiftAsync() =>
        FetchAndApplyAsync(CatCommands.ReadRepeaterShift(), CatCommands.ParseRepeaterShift, v => RepeaterShift = v);

    // MARK: - Squelch/tone type (CT) + CTCSS/DCS index (CN)

    private ToneType _toneType = ToneType.Off;
    public ToneType ToneType { get => _toneType; private set => SetProperty(ref _toneType, value); }

    public Task SetToneTypeAsync(ToneType type) => SendSetAsync(CatCommands.SetToneType(type), () => ToneType = type);

    public Task RefreshToneTypeAsync() =>
        FetchAndApplyAsync(CatCommands.ReadToneType(), CatCommands.ParseToneType, v => ToneType = v);

    private int _ctcssIndex = 12;
    public int CtcssIndex { get => _ctcssIndex; private set => SetProperty(ref _ctcssIndex, value); }

    private int _dcsIndex;
    public int DcsIndex { get => _dcsIndex; private set => SetProperty(ref _dcsIndex, value); }

    public Task SetCtcssIndexAsync(int index) =>
        SendSetAsync(CatCommands.SetCtcssNumber(index), () => CtcssIndex = CatCommands.Clamp(index, 0, 49));

    public Task SetDcsIndexAsync(int index) =>
        SendSetAsync(CatCommands.SetDcsNumber(index), () => DcsIndex = CatCommands.Clamp(index, 0, 103));

    public async Task RefreshToneNumberAsync()
    {
        string ctcssReply, dcsReply;
        try
        {
            ctcssReply = await Cat1.SendAsync(CatCommands.ReadCtcssNumber());
            dcsReply = await Cat1.SendAsync(CatCommands.ReadDcsNumber());
        }
        catch { return; }

        if (CatCommands.ParseToneNumber(ctcssReply, out var isDcs1, out var index1) && !isDcs1) CtcssIndex = index1;
        if (CatCommands.ParseToneNumber(dcsReply, out var isDcs2, out var index2) && isDcs2) DcsIndex = index2;
    }

    // MARK: - AF Treble/Mid/Bass (EX 01) — mode-category-gated

    /// Which EX audio-menu category applies to the current mode, or null
    /// for modes with no EQ menu entry (e.g. C4FM) — callers must guard on
    /// this before sending EX01...
    public CatCommands.ExAudioModeCategory? ExAudioCategoryForMode => ModeCode switch
    {
        '1' or '2' => CatCommands.ExAudioModeCategory.Ssb, // LSB, USB
        '5' => CatCommands.ExAudioModeCategory.Am,
        '4' => CatCommands.ExAudioModeCategory.Fm,
        '6' or '9' => CatCommands.ExAudioModeCategory.Rtty, // RTTY-L, RTTY-U
        'E' => CatCommands.ExAudioModeCategory.Data, // PSK lives under DATA per Table 3
        _ => null, // C4FM-DN/VW and others: not exposed
    };

    private int _afTreble;
    public int AfTreble { get => _afTreble; private set => SetProperty(ref _afTreble, value); }

    private int _afMid;
    public int AfMid { get => _afMid; private set => SetProperty(ref _afMid, value); }

    private int _afBass;
    public int AfBass { get => _afBass; private set => SetProperty(ref _afBass, value); }

    public Task SetAfEqAsync(CatCommands.ExAudioParam param, int value)
    {
        if (ExAudioCategoryForMode is not { } category) return Task.CompletedTask;
        var command = CatCommands.SetExAudio(category, param, value);
        var clamped = CatCommands.Clamp(value, -20, 10);
        return SendSetAsync(command, () =>
        {
            switch (param)
            {
                case CatCommands.ExAudioParam.Treble: AfTreble = clamped; break;
                case CatCommands.ExAudioParam.Mid: AfMid = clamped; break;
                case CatCommands.ExAudioParam.Bass: AfBass = clamped; break;
            }
        });
    }

    public async Task RefreshAfEqAsync()
    {
        if (ExAudioCategoryForMode is not { } category) return;
        await FetchAndApplyAsync(CatCommands.ReadExAudio(category, CatCommands.ExAudioParam.Treble), CatCommands.ParseExAudio, v => AfTreble = v);
        await FetchAndApplyAsync(CatCommands.ReadExAudio(category, CatCommands.ExAudioParam.Mid), CatCommands.ParseExAudio, v => AfMid = v);
        await FetchAndApplyAsync(CatCommands.ReadExAudio(category, CatCommands.ExAudioParam.Bass), CatCommands.ParseExAudio, v => AfBass = v);
    }

    // MARK: - HF Antenna Select (EX 03-07-04)

    private int _hfAntSelect;
    public int HfAntSelect { get => _hfAntSelect; private set => SetProperty(ref _hfAntSelect, value); }

    public Task SetHfAntSelectAsync(int port) =>
        SendSetAsync(CatCommands.SetHfAntSelect(port), () => HfAntSelect = CatCommands.Clamp(port, 0, 1));

    public Task RefreshHfAntSelectAsync() =>
        FetchAndApplyAsync(CatCommands.ReadHfAntSelect(), CatCommands.ParseHfAntSelect, v => HfAntSelect = v);

    // MARK: - Band Select (BS) — Set-only; "current band" is inferred from
    // frequency via the already-ported Models.BandCode.FromFrequency, never
    // read back from the radio.

    public BandCode? CurrentBandCode => BandCodeInfo.FromFrequency(FrequencyHz);

    public Task SetBandAsync(BandCode code) => Cat1.IsConnected
        ? Cat1.SendAsync(CatCommands.SetBand(code), expectsReply: false)
        : Task.CompletedTask;
}
