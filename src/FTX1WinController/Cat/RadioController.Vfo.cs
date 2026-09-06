using FineTuningStateEnum = FTX1WinController.Models.FineTuningState;

namespace FTX1WinController.Cat;

/// v4: the VFO knob's surrounding physical controls — CLAR, FINE/FAST, QMB,
/// AF Gain. Ported from the Mac app's RadioControllerV4.swift.
public sealed partial class RadioController
{
    // MARK: - Clarifier (CF) — a 4-state cycle (RX -> TX -> RX/TX -> OFF ->
    // RX...), matching a single press of the physical CLAR button, NOT a
    // plain on/off toggle.

    private bool _clarifierRxOn;
    public bool ClarifierRxOn { get => _clarifierRxOn; private set => SetProperty(ref _clarifierRxOn, value); }

    private bool _clarifierTxOn;
    public bool ClarifierTxOn { get => _clarifierTxOn; private set => SetProperty(ref _clarifierTxOn, value); }

    private int _clarifierOffsetHz;
    public int ClarifierOffsetHz { get => _clarifierOffsetHz; private set => SetProperty(ref _clarifierOffsetHz, value); }

    public Task SetClarifierAsync(bool rxOn, bool txOn) =>
        SendSetAsync(CatCommands.SetClarifierState(rxOn, txOn), () =>
        {
            ClarifierRxOn = rxOn;
            ClarifierTxOn = txOn;
        });

    /// Cycles RX -> TX -> RX/TX -> OFF -> RX ..., matching a single press of
    /// the physical CLAR button on the radio (confirmed on hardware) — not
    /// a plain on/off toggle.
    public Task CycleClarifierAsync()
    {
        var (rx, tx) = (ClarifierRxOn, ClarifierTxOn) switch
        {
            (false, false) => (true, false),  // OFF -> RX
            (true, false) => (false, true),   // RX -> TX
            (false, true) => (true, true),    // TX -> RX/TX
            (true, true) => (false, false),   // RX/TX -> OFF
        };
        return SetClarifierAsync(rx, tx);
    }

    public Task SetClarifierFrequencyAsync(int hz) =>
        SendSetAsync(CatCommands.SetClarifierFrequency(hz), () => ClarifierOffsetHz = CatCommands.Clamp(hz, -9999, 9999));

    public async Task RefreshClarifierAsync()
    {
        string stateReply, freqReply;
        try
        {
            stateReply = await Cat1.SendAsync(CatCommands.ReadClarifierState());
            freqReply = await Cat1.SendAsync(CatCommands.ReadClarifierFrequency());
        }
        catch { return; }

        if (CatCommands.ParseClarifierState(stateReply, out var rx, out var tx))
        {
            ClarifierRxOn = rx;
            ClarifierTxOn = tx;
        }
        if (CatCommands.ParseClarifierFrequency(freqReply) is { } offset) ClarifierOffsetHz = offset;
    }

    // MARK: - Fine/Fast Tuning (FN)

    private FineTuningStateEnum _fineTuningState = FineTuningStateEnum.Off;
    public FineTuningStateEnum FineTuningState { get => _fineTuningState; private set => SetProperty(ref _fineTuningState, value); }

    public Task SetFineTuningAsync(FineTuningStateEnum state) => SendSetAsync(CatCommands.SetFineTuning(state), () => FineTuningState = state);

    /// Cycles OFF -> FINE -> FAST -> OFF, matching the physical FINE/FAST button.
    public Task CycleFineTuningAsync()
    {
        var next = FineTuningState switch
        {
            FineTuningStateEnum.Off => FineTuningStateEnum.Fine,
            FineTuningStateEnum.Fine => FineTuningStateEnum.Fast,
            _ => FineTuningStateEnum.Off,
        };
        return SetFineTuningAsync(next);
    }

    public Task RefreshFineTuningAsync() =>
        FetchAndApplyAsync(CatCommands.ReadFineTuning(), CatCommands.ParseFineTuning, v => FineTuningState = v);

    // MARK: - QMB (QI store / QR recall) — Set-only, no Read/Answer at all;
    // state is tracked purely optimistically by the app's own tap count,
    // matching the radio's own front-panel counter. Assumes the confirmed
    // default of 5 channels; the radio also supports a 10-channel mode this
    // app doesn't read, so the two counters could drift if that's not at
    // its default. A *physical* QMB press on the radio can't be
    // detected/mirrored at all, since there's no CAT command to poll it.

    public Task QmbStoreAsync() => SendSetAsync(CatCommands.QmbStore(), () => { });

    /// Repeated taps step VFO -> QMB1 -> QMB2 -> QMB3 -> QMB4 -> QMB5 ->
    /// VFO, matching the physical [QMB] key. Written by hand instead of via
    /// SendSetAsync because SuppressNextQmbAutoReset must be set *before*
    /// the send completes — it guards against RefreshFrequencyAsync running
    /// concurrently during the awaited send and mistaking this recall for
    /// ordinary VFO tuning, which SendSetAsync's apply-after-success timing
    /// would be too late to prevent.
    public async Task QmbRecallAsync()
    {
        if (!IsConnected) return;
        try
        {
            SuppressNextQmbAutoReset = true;
            await Cat1.SendAsync(CatCommands.QmbRecall(), expectsReply: false);
            if (QmbChannelLabel == "VFO")
            {
                QmbChannelLabel = "QMB1";
            }
            else if (QmbChannelLabel.StartsWith("QMB") && int.TryParse(QmbChannelLabel.Substring(3), out var n) && n < 5)
            {
                QmbChannelLabel = $"QMB{n + 1}";
            }
            else
            {
                QmbChannelLabel = "VFO";
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    // MARK: - AF Gain (AG) — the third leg of the MAIN-side AF/RF/SQL knob;
    // RF Gain and Squelch are in RadioController.Dsp.cs.

    private int _afGain = 255;
    public int AfGain { get => _afGain; private set => SetProperty(ref _afGain, value); }

    public Task SetAfGainAsync(int value) =>
        SendSetAsync(CatCommands.SetAfGain(value), () => AfGain = CatCommands.Clamp(value, 0, 255));

    public Task RefreshAfGainAsync() =>
        FetchAndApplyAsync(CatCommands.ReadAfGain(), CatCommands.ParseAfGain, v => AfGain = v);
}
