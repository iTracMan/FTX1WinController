using FTX1WinController.Models;

namespace FTX1WinController.Cat;

/// v2 command coverage: DSP/audio, RF, and operating (scan/split/repeater/
/// tone) commands, per the official FTX-1 CAT Operation Reference Manual
/// (2508-C). Ported 1:1 from the Mac app's CATProtocolV2.swift.
///
/// Every command here operates on the MAIN-side (P1/side selector fixed to
/// 0) since the whole app only ever drives the MAIN VFO.
public static partial class CatCommands
{
    // MARK: - Noise Reduction level (RL) — "RL0" + 2-digit level (00-10) + ";"

    public static string ReadNoiseReduction() => "RL0;";

    public static string SetNoiseReduction(int level) => "RL0" + Pad(Clamp(level, 0, 10), 2) + ";";

    /// Reply: R L 0 d d ; (6 chars)
    public static int? ParseNoiseReduction(string reply)
    {
        if (reply.Length != 6 || reply[0] != 'R' || reply[1] != 'L' || reply[5] != ';') return null;
        return int.TryParse(reply.Substring(3, 2), out var v) ? v : null;
    }

    // MARK: - Auto Notch (BC) — on/off

    public static string ReadAutoNotch() => "BC0;";
    public static string SetAutoNotch(bool on) => on ? "BC01;" : "BC00;";

    /// Reply: B C 0 d ; (5 chars)
    public static bool? ParseAutoNotch(string reply)
    {
        if (reply.Length != 5 || reply[0] != 'B' || reply[1] != 'C' || reply[4] != ';') return null;
        return reply[3] switch { '0' => false, '1' => true, _ => (bool?)null };
    }

    // MARK: - Manual Notch (BP) — compound: on/off sub-field, or frequency sub-field

    public static string ReadManualNotchState() => "BP00;";
    public static string ReadManualNotchFrequency() => "BP01;";

    public static string SetManualNotch(bool on) => "BP00" + (on ? "001" : "000") + ";";

    /// hz: 10..3200 in 10 Hz steps (the radio's own P3 unit).
    public static string SetManualNotchFrequency(int hz) => "BP01" + Pad(Clamp(hz, 10, 3200) / 10, 3) + ";";

    /// Reply: B P 0 <p2> <p3 x3> ; (8 chars). isFrequency tells the caller
    /// which sub-field this reply is: value is 0/1 for the on/off field, or
    /// Hz for the frequency field.
    public static bool ParseManualNotch(string reply, out bool isFrequency, out int value)
    {
        isFrequency = false;
        value = 0;
        if (reply.Length != 8 || reply[0] != 'B' || reply[1] != 'P' || reply[7] != ';') return false;
        if (!int.TryParse(reply.Substring(4, 3), out var raw)) return false;
        switch (reply[3])
        {
            case '0': isFrequency = false; value = raw; return true;
            case '1': isFrequency = true; value = raw * 10; return true;
            default: return false;
        }
    }

    // MARK: - RF Attenuator (RA) — P1 fixed "0", P2 on/off

    public static string ReadAttenuator() => "RA0;";
    public static string SetAttenuator(bool on) => on ? "RA01;" : "RA00;";

    /// Reply: R A 0 d ; (5 chars)
    public static bool? ParseAttenuator(string reply)
    {
        if (reply.Length != 5 || reply[0] != 'R' || reply[1] != 'A' || reply[4] != ';') return null;
        return reply[3] switch { '0' => false, '1' => true, _ => (bool?)null };
    }

    // MARK: - Preamp / IPO (PA) — P1 selects band, P2 meaning depends on P1

    public enum PreampBand
    {
        Hf50, // P2: 0=IPO, 1=AMP1, 2=AMP2
        Vhf,  // P2: 0=off, 1=on
        Uhf,  // P2: 0=off, 1=on
    }

    private static char PreampBandCode(PreampBand band) => band switch
    {
        PreampBand.Hf50 => '0',
        PreampBand.Vhf => '1',
        PreampBand.Uhf => '2',
        _ => throw new ArgumentOutOfRangeException(nameof(band)),
    };

    public static string ReadPreamp(PreampBand band) => $"PA{PreampBandCode(band)};";
    public static string SetPreamp(PreampBand band, int value) => $"PA{PreampBandCode(band)}{value};";

    /// Reply: P A <band> <value> ; (5 chars)
    public static bool ParsePreamp(string reply, out char band, out char value)
    {
        band = default;
        value = default;
        if (reply.Length != 5 || reply[0] != 'P' || reply[1] != 'A' || reply[4] != ';') return false;
        band = reply[2];
        value = reply[3];
        return true;
    }

    // MARK: - AGC (GT) — P1 fixed "0" (MAIN-side; this app has no SUB-VFO
    // of its own to select the other option for). Set/Read's P2 is the
    // 5-value mode the user actually chooses (OFF/FAST/MID/SLOW/AUTO,
    // 0-4); the Answer's P3 is a *different*, 7-value field (0-6) — AUTO
    // expands into which of AUTO-FAST/AUTO-MID/AUTO-SLOW the radio
    // resolved it to.

    public static string ReadAgc() => "GT0;";
    public static string SetAgc(AgcMode mode) => $"GT0{(int)mode};";

    /// Reply: G T 0 P3 ; (5 chars). P3 collapses AUTO-FAST/AUTO-MID/
    /// AUTO-SLOW (4/5/6) down to Auto — this app has no use for which
    /// auto-tier is currently active, only whether AUTO is selected.
    public static AgcMode? ParseAgc(string reply)
    {
        if (reply.Length != 5 || reply[0] != 'G' || reply[1] != 'T' || reply[4] != ';') return null;
        return reply[3] switch
        {
            '0' => AgcMode.Off,
            '1' => AgcMode.Fast,
            '2' => AgcMode.Mid,
            '3' => AgcMode.Slow,
            '4' or '5' or '6' => AgcMode.Auto,
            _ => (AgcMode?)null,
        };
    }

    // MARK: - RF Gain (RG) — P1 fixed "0", P2 000-255

    public static string ReadRfGain() => "RG0;";
    public static string SetRfGain(int value) => "RG0" + Pad(Clamp(value, 0, 255), 3) + ";";

    /// Reply: R G 0 ddd ; (7 chars)
    public static int? ParseRfGain(string reply)
    {
        if (reply.Length != 7 || reply[0] != 'R' || reply[1] != 'G' || reply[6] != ';') return null;
        return int.TryParse(reply.Substring(3, 3), out var v) ? v : null;
    }

    // MARK: - Squelch (SQ) — P1 fixed "0", P2 000-255

    public static string ReadSquelch() => "SQ0;";
    public static string SetSquelch(int value) => "SQ0" + Pad(Clamp(value, 0, 255), 3) + ";";

    /// Reply: S Q 0 ddd ; (7 chars)
    public static int? ParseSquelch(string reply)
    {
        if (reply.Length != 7 || reply[0] != 'S' || reply[1] != 'Q' || reply[6] != ';') return null;
        return int.TryParse(reply.Substring(3, 3), out var v) ? v : null;
    }

    // MARK: - Power Control (PC) — P1 identifies which amp (1=field head
    // 5-10W, 2=SPA-1 5-100W). Read is deliberately bare ("PC;") with no P1
    // — the radio reports which amp (P1) is active in its Answer, so we
    // discover it rather than assume it ("ask, don't assume").

    public static string ReadPower() => "PC;";

    public static string SetPower(int amp, int watts)
    {
        var (lo, hi) = amp == 2 ? (5, 100) : (5, 10);
        return $"PC{amp}" + Pad(Clamp(watts, lo, hi), 3) + ";";
    }

    /// Reply: P C <amp> ddd ; (7 chars)
    public static bool ParsePower(string reply, out int amp, out int watts)
    {
        amp = 0;
        watts = 0;
        if (reply.Length != 7 || reply[0] != 'P' || reply[1] != 'C' || reply[6] != ';') return false;
        if (!int.TryParse(reply.Substring(2, 1), out amp)) return false;
        if (!int.TryParse(reply.Substring(3, 3), out watts)) return false;
        return true;
    }

    // MARK: - Scan (SC) — P1 fixed "0", P2 0=off/1=up/2=down

    public enum ScanState { Off, Up, Down }

    private static char ScanStateCode(ScanState state) => state switch
    {
        ScanState.Off => '0',
        ScanState.Up => '1',
        ScanState.Down => '2',
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static string ReadScan() => "SC;";
    public static string SetScan(ScanState state) => $"SC0{ScanStateCode(state)};";

    /// Reply: S C <p1> <p2> ; (5 chars)
    public static ScanState? ParseScan(string reply)
    {
        if (reply.Length != 5 || reply[0] != 'S' || reply[1] != 'C' || reply[4] != ';') return null;
        return reply[3] switch { '0' => ScanState.Off, '1' => ScanState.Up, '2' => ScanState.Down, _ => (ScanState?)null };
    }

    // MARK: - Split (ST) — on/off

    public static string ReadSplit() => "ST;";
    public static string SetSplit(bool on) => on ? "ST1;" : "ST0;";

    /// Reply: S T d ; (4 chars)
    public static bool? ParseSplit(string reply)
    {
        if (reply.Length != 4 || reply[0] != 'S' || reply[1] != 'T' || reply[3] != ';') return null;
        return reply[2] switch { '0' => false, '1' => true, _ => (bool?)null };
    }

    // MARK: - VFO-B frequency (FB) — same 9-digit Hz shape as FA

    public static string ReadSubFrequency() => "FB;";

    public static string SetSubFrequency(int hz) => "FB" + Pad(Clamp(hz, 30_000, 470_000_000), 9) + ";";

    public static int? ParseSubFrequency(string reply)
    {
        if (reply.Length != 12 || reply[0] != 'F' || reply[1] != 'B' || reply[11] != ';') return null;
        return int.TryParse(reply.Substring(2, 9), out var v) ? v : null;
    }

    // MARK: - Function TX (FT) — which side transmits

    public static string ReadTxSide() => "FT;";
    public static string SetTxSide(bool mainSide) => mainSide ? "FT0;" : "FT1;";

    /// Reply: F T d ; (4 chars). true = MAIN-side transmits.
    public static bool? ParseTxSide(string reply)
    {
        if (reply.Length != 4 || reply[0] != 'F' || reply[1] != 'T' || reply[3] != ';') return null;
        return reply[2] switch { '0' => true, '1' => false, _ => (bool?)null };
    }

    // MARK: - Repeater offset direction (OS) — P1 fixed "0"

    public static string ReadRepeaterShift() => "OS0;";
    public static string SetRepeaterShift(RepeaterShift shift) => $"OS0{shift.CatCode()};";

    /// Reply: O S <p1> <p2> ; (5 chars)
    public static RepeaterShift? ParseRepeaterShift(string reply)
    {
        if (reply.Length != 5 || reply[0] != 'O' || reply[1] != 'S' || reply[4] != ';') return null;
        return RepeaterShiftInfo.FromCatCode(reply[3]);
    }

    // MARK: - Squelch/tone type (CT) — P1 fixed "0"

    public static string ReadToneType() => "CT0;";
    public static string SetToneType(ToneType type) => $"CT0{type.CatCode()};";

    /// Reply: C T <p1> <p2> ; (5 chars)
    public static ToneType? ParseToneType(string reply)
    {
        if (reply.Length != 5 || reply[0] != 'C' || reply[1] != 'T' || reply[4] != ';') return null;
        return ToneTypeInfo.FromCatCode(reply[3]);
    }

    // MARK: - CTCSS tone / DCS code number (CN) — P1 fixed "0"

    public static string ReadCtcssNumber() => "CN00;";
    public static string ReadDcsNumber() => "CN01;";

    public static string SetCtcssNumber(int index) => "CN00" + Pad(Clamp(index, 0, 49), 3) + ";";
    public static string SetDcsNumber(int index) => "CN01" + Pad(Clamp(index, 0, 103), 3) + ";";

    /// Reply: C N 0 <p2> <p3 x3> ; (8 chars).
    public static bool ParseToneNumber(string reply, out bool isDcs, out int index)
    {
        isDcs = false;
        index = 0;
        if (reply.Length != 8 || reply[0] != 'C' || reply[1] != 'N' || reply[7] != ';') return false;
        if (!int.TryParse(reply.Substring(4, 3), out index)) return false;
        switch (reply[3])
        {
            case '0': isDcs = false; return true;
            case '1': isDcs = true; return true;
            default: return false;
        }
    }

    // MARK: - Menu (EX) — used only for AF Treble/Mid/Bass (P1=01 RADIO SETTING)

    /// P2 selects the mode category the audio setting applies to.
    public enum ExAudioModeCategory { Ssb, Am, Fm, Data, Rtty }
    public enum ExAudioParam { Treble, Mid, Bass }

    private static string ExAudioModeCategoryCode(ExAudioModeCategory category) => category switch
    {
        ExAudioModeCategory.Ssb => "01",
        ExAudioModeCategory.Am => "02",
        ExAudioModeCategory.Fm => "03",
        ExAudioModeCategory.Data => "04", // also covers PSK, per Table 3's "17 PSK TONE" living in this same category
        ExAudioModeCategory.Rtty => "05",
        _ => throw new ArgumentOutOfRangeException(nameof(category)),
    };

    private static string ExAudioParamCode(ExAudioParam param) => param switch
    {
        ExAudioParam.Treble => "01",
        ExAudioParam.Mid => "02",
        ExAudioParam.Bass => "03",
        _ => throw new ArgumentOutOfRangeException(nameof(param)),
    };

    public static string ReadExAudio(ExAudioModeCategory category, ExAudioParam param) =>
        $"EX01{ExAudioModeCategoryCode(category)}{ExAudioParamCode(param)};";

    public static string SetExAudio(ExAudioModeCategory category, ExAudioParam param, int value) =>
        $"EX01{ExAudioModeCategoryCode(category)}{ExAudioParamCode(param)}" + SignedPad(Clamp(value, -20, 10)) + ";";

    /// Reply: E X 0 1 <p2 x2> <p3 x2> <p4 sign+2> ; (12 chars)
    public static int? ParseExAudio(string reply)
    {
        if (reply.Length != 12 || reply[0] != 'E' || reply[1] != 'X' || reply[11] != ';') return null;
        return ParseSignedPad(reply.Substring(8, 3));
    }

    // MARK: - HF Antenna Select (EX 03-07-04) — which physical rear-panel
    // ANT port (1 or 2) the radio uses on HF/6M (Table 3: P1=03 OPERATION
    // SETTING, P2=07 OPTION, P3=04 HF ANT SELECT, P4 1 digit: 0=ANT1,
    // 1=ANT2).

    public static string ReadHfAntSelect() => "EX030704;";
    public static string SetHfAntSelect(int port) => $"EX030704{Clamp(port, 0, 1)};";

    /// Reply: E X 0 3 0 7 0 4 <p4> ; (10 chars)
    public static int? ParseHfAntSelect(string reply)
    {
        if (reply.Length != 10 || reply[0] != 'E' || reply[1] != 'X' || reply[9] != ';') return null;
        return int.TryParse(reply.Substring(8, 1), out var v) ? v : null;
    }

    // MARK: - Band Select (BS) — P1 fixed "0" (MAIN-side); P2 a 2-digit
    // band code. Set-only per the manual's own command table (no
    // Read/Answer bytes listed) — same "fire and forget" shape as QMB.
    // Uses the project's existing Models.BandCode (already ported 1:1 from
    // the Mac app's CATProtocolV2.swift BandCode enum in an earlier session).

    public static string SetBand(BandCode code) => $"BS0{code.Code()};";
}
