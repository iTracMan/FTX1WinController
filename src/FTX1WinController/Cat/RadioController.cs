using FTX1WinController.Models;
using FTX1WinController.ViewModels;

namespace FTX1WinController.Cat;

/// Owns the two CAT serial links (CAT-1 for frequency/mode, CAT-2 for PTT)
/// and publishes the radio's state for a ViewModel to bind against.
/// Frequency/mode range validation and PTT default state (always receive on
/// connect/disconnect) follow the documented FA/MD/TX commands directly.
///
/// Ported from the Mac app's RadioController.swift (a @MainActor
/// ObservableObject) + its V2/V3/V4 extensions, mirrored here as
/// RadioController.Dsp.cs / .Func.cs / .Vfo.cs partial-class files.
///
/// Deliberately does NOT run its own polling loop, unlike the Mac version:
/// this app's MainViewModel already owns a DispatcherTimer-based poll loop
/// (inherited from the bridge-based FTX1ControllerWin) with the same
/// rotating-group refresh idea. Wiring MainViewModel onto this class (a
/// later step) reuses that loop instead of running two competing ones.
public sealed partial class RadioController : ObservableObject
{
    // MARK: - Connection

    internal readonly CatConnection Cat1 = new();
    internal readonly CatConnection Cat2 = new();

    private ConnectionState _connectionState = ConnectionState.Disconnected;
    public ConnectionState ConnectionState { get => _connectionState; private set => SetProperty(ref _connectionState, value); }

    public bool IsConnected => ConnectionState == ConnectionState.Connected;

    private string? _lastError;
    public string? LastError { get => _lastError; internal set => SetProperty(ref _lastError, value); }

    private string? _identity;
    public string? Identity { get => _identity; private set => SetProperty(ref _identity, value); }

    public async Task ConnectAsync(ISerialTransport cat1Transport, ISerialTransport? cat2Transport)
    {
        if (ConnectionState == ConnectionState.Connecting) return;
        ConnectionState = ConnectionState.Connecting;
        LastError = null;

        try
        {
            Cat1.Connect(cat1Transport);
            if (cat2Transport != null) Cat2.Connect(cat2Transport);
            else Cat2.Disconnect();

            var idReply = await Cat1.SendAsync(CatCommands.ReadIdentity());
            Identity = CatCommands.ParseIdentity(idReply);

            // Always come up safely in receive, regardless of what the
            // radio's front-panel state happens to be.
            await SendPttAsync(false);

            ConnectionState = ConnectionState.Connected;
            await RefreshAllAsync();
        }
        catch (Exception ex)
        {
            ConnectionState = ConnectionState.Failed;
            LastError = ex.Message;
            Cat1.Disconnect();
            Cat2.Disconnect();
        }
    }

    public async Task DisconnectAsync()
    {
        try { await SendPttAsync(false); }
        catch { /* best effort */ }
        Cat1.Disconnect();
        Cat2.Disconnect();
        ConnectionState = ConnectionState.Disconnected;
        Identity = null;
    }

    // MARK: - Frequency

    private int _frequencyHz = 14_250_000;
    public int FrequencyHz { get => _frequencyHz; private set => SetProperty(ref _frequencyHz, value); }

    /// True right before a QMB recall/store send completes, so the next
    /// RefreshFrequencyAsync() poll tick doesn't mistake that expected jump
    /// for an out-of-band VFO-knob change and immediately stomp the QMB
    /// label QmbRecallAsync() just set — see RadioController.Vfo.cs.
    internal bool SuppressNextQmbAutoReset;

    private string _qmbChannelLabel = "VFO";
    public string QmbChannelLabel { get => _qmbChannelLabel; internal set => SetProperty(ref _qmbChannelLabel, value); }

    public async Task SetFrequencyAsync(int hz)
    {
        if (!IsConnected) return;
        try
        {
            await Cat1.SendAsync(CatCommands.SetFrequency(hz), expectsReply: false);
            FrequencyHz = hz;
            // Confirmed on hardware: tuning away from a QMB channel (VFO
            // dial or direct entry) reverts the radio's own QMB1-5 badge
            // back to "VFO". Every frequency change this app makes goes
            // through this one method, so mirroring that reversion here
            // keeps the optimistic counter in sync without polling anything.
            QmbChannelLabel = "VFO";
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    public async Task RefreshFrequencyAsync()
    {
        string reply;
        try { reply = await Cat1.SendAsync(CatCommands.ReadFrequency()); }
        catch { return; }
        var hz = CatCommands.ParseFrequency(reply);
        if (hz is not { } value) return;

        // A frequency change this poll didn't already know about (i.e. not
        // one of the app's own SetFrequencyAsync calls, which update
        // FrequencyHz immediately) means the radio's own VFO knob was
        // turned — which reverts a QMB channel back to VFO on real
        // hardware. Mirror that here too. SuppressNextQmbAutoReset guards
        // the one case this heuristic can't tell apart from real VFO
        // tuning: a QMB recall also changes frequency, so without it this
        // would immediately overwrite the "QMBn" label QmbRecallAsync()
        // just set for an app-initiated tap. A *physical* QMB press on the
        // radio still can't be distinguished this way (no read-back exists
        // either way) — a known limitation, same as the Mac app.
        if (value != FrequencyHz)
        {
            if (SuppressNextQmbAutoReset) SuppressNextQmbAutoReset = false;
            else QmbChannelLabel = "VFO";
        }
        FrequencyHz = value;
    }

    /// Modes documented with 1Hz FINE / 20Hz normal / 200Hz FAST steps use
    /// this step group; every other mode uses 10Hz FINE / 100Hz normal /
    /// 1kHz FAST. C4FM-VW isn't listed in either row of the manual's table;
    /// grouped with the second (AM/FM/C4FM/DATA-FM) group as the closest
    /// match, not independently confirmed.
    private static readonly HashSet<char> FineStepGroupA = new() { '1', '2', '3', '7', '8', 'C', '6', '9', 'E' };

    /// The VFO dial's step size in Hz — driven by the real FINE/FAST
    /// setting (FN) and the current mode, exactly like the physical VFO knob.
    public int VfoStepHz
    {
        get
        {
            var isGroupA = FineStepGroupA.Contains(ModeCode);
            return (isGroupA, FineTuningState) switch
            {
                (true, Models.FineTuningState.Off) => 20,
                (true, Models.FineTuningState.Fine) => 1,
                (true, Models.FineTuningState.Fast) => 200,
                (false, Models.FineTuningState.Off) => 100,
                (false, Models.FineTuningState.Fine) => 10,
                (false, Models.FineTuningState.Fast) => 1000,
                _ => 100,
            };
        }
    }

    // MARK: - Mode

    private char _modeCode = '2';
    public char ModeCode { get => _modeCode; private set => SetProperty(ref _modeCode, value); }

    public string ModeDisplayName => RadioModeInfo.DisplayNameForCode(ModeCode);

    /// Keyer (KR) only stays on in CW-L/CW-U — confirmed on hardware. Mode
    /// codes per RadioMode.CatCode: "7"=CW-L, "3"=CW-U.
    public bool KeyerAvailable => ModeCode is '7' or '3';

    /// FM, FM-N, C4FM, D-FM, D-FM-N show SQL instead of RF on both the
    /// meter and the MAIN AF/RF/SQL knob — confirmed on hardware. "I"
    /// (C4FM-VW) is grouped with the C4FM family (confirmed); "7" (CW-L) is
    /// grouped with CW-U (RF) for the same reason — neither independently confirmed.
    private static readonly HashSet<char> SquelchModeCodes = new() { '4', 'B', 'H', 'A', 'F', 'I' };

    public bool ModeShowsSquelchNotRf => SquelchModeCodes.Contains(ModeCode);

    public async Task SetModeAsync(RadioMode mode)
    {
        if (!IsConnected) return;
        try
        {
            await Cat1.SendAsync(CatCommands.SetMode(mode), expectsReply: false);
            ModeCode = mode.CatCode();
            // Confirmed on hardware: the radio itself turns Keyer off the
            // instant you leave CW-L/CW-U — mirrored immediately rather
            // than waiting for the next poll to pick up the radio's own change.
            if (!KeyerAvailable) KeyerOn = false;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    /// Sets an arbitrary documented mode code directly (e.g. recalling a
    /// preset saved while the radio was in a mode with no dedicated button).
    public async Task SetModeCodeAsync(char code)
    {
        if (!IsConnected) return;
        try
        {
            await Cat1.SendAsync(CatCommands.SetModeCode(code), expectsReply: false);
            ModeCode = code;
            if (!KeyerAvailable) KeyerOn = false;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    public async Task RefreshModeAsync()
    {
        string reply;
        try { reply = await Cat1.SendAsync(CatCommands.ReadMode()); }
        catch { return; }
        var code = CatCommands.ParseMode(reply);
        if (code is { } value) ModeCode = value;
    }

    // MARK: - PTT

    private bool _isTransmitting;
    public bool IsTransmitting { get => _isTransmitting; private set => SetProperty(ref _isTransmitting, value); }

    public async Task SetTransmitAsync(bool on)
    {
        if (!IsConnected) return;
        try { await SendPttAsync(on); }
        catch (Exception ex) { LastError = ex.Message; }
    }

    private async Task SendPttAsync(bool on)
    {
        var link = Cat2.IsConnected ? Cat2 : Cat1;
        // TX0;/TX1; is a Set command (fire-and-forget, no Answer) — trust
        // what was commanded and let RefreshPttAsync's periodic TX; Read
        // reconcile it against the radio's actual state shortly after.
        await link.SendAsync(CatCommands.SetTx(on), expectsReply: false);
        IsTransmitting = on;
    }

    public async Task RefreshPttAsync()
    {
        var link = Cat2.IsConnected ? Cat2 : Cat1;
        string reply;
        try { reply = await link.SendAsync(CatCommands.ReadTx()); }
        catch { return; }
        var state = CatCommands.ParseTxState(reply);
        if (state is { } value) IsTransmitting = value;
    }

    // MARK: - Meters

    private int _sMeter;
    public int SMeter { get => _sMeter; internal set => SetProperty(ref _sMeter, value); }

    private int _powerOutput;
    public int PowerOutput { get => _powerOutput; internal set => SetProperty(ref _powerOutput, value); }

    private int _swr;
    public int Swr { get => _swr; internal set => SetProperty(ref _swr, value); }

    public async Task<int?> RefreshMeterAsync(MeterKind kind)
    {
        string reply;
        try { reply = await Cat1.SendAsync(CatCommands.ReadMeter(kind)); }
        catch { return null; }
        return CatCommands.ParseMeter(reply, kind);
    }

    public async Task RefreshAllAsync()
    {
        await RefreshFrequencyAsync();
        await RefreshModeAsync();
        await RefreshPttAsync();
    }

    // MARK: - Small helpers shared by the Dsp/Func/Vfo partials — a plain
    // send-and-apply / fetch-and-apply pair, standing in for the Mac app's
    // sendSet/fetchAndApply generics (which also did structured console
    // logging; that's left for a later pass, not core protocol behavior).

    internal async Task SendSetAsync(string command, Action apply)
    {
        if (!IsConnected) return;
        try
        {
            await Cat1.SendAsync(command, expectsReply: false);
            apply();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    internal async Task FetchAndApplyAsync<T>(string readCommand, Func<string, T?> parse, Action<T> apply)
        where T : struct
    {
        string reply;
        try { reply = await Cat1.SendAsync(readCommand); }
        catch { return; }
        if (parse(reply) is { } value) apply(value);
    }
}
