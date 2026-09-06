using System.Globalization;

namespace FTX1WinController.Cat;

/// v3: coverage for the FUNC-knob press-and-hold quick-menu (the paged
/// on-screen soft-key panel). Ported 1:1 from the Mac app's
/// CATProtocolV3.swift, cross-referenced against the CAT Operation
/// Reference Manual's command table.
public static partial class CatCommands
{
    // MARK: - Spectrum Scope quick items (SS) — D-LEVEL / D-PEAK / D-MARKER / D-COLOR
    // All four share the same 10-char frame: "SS" + P1(0) + P2(sub) + 5-char payload + ";"

    public static string ReadScopePeak() => "SS01;";
    public static string SetScopePeak(int level) => "SS01" + Clamp(level, 0, 4) + "0000;";

    public static int? ParseScopePeak(string reply)
    {
        if (reply.Length != 10 || reply[0] != 'S' || reply[1] != 'S' || reply[3] != '1' || reply[9] != ';') return null;
        return reply[4] - '0';
    }

    public static string ReadScopeMarker() => "SS02;";
    public static string SetScopeMarker(bool on) => "SS02" + (on ? "1" : "0") + "0000;";

    public static bool? ParseScopeMarker(string reply)
    {
        if (reply.Length != 10 || reply[0] != 'S' || reply[1] != 'S' || reply[3] != '2' || reply[9] != ';') return null;
        return reply[4] switch { '0' => false, '1' => true, _ => (bool?)null };
    }

    /// 0..10 representing COLOR-1..COLOR-11 (a single hex digit per the manual).
    public static string ReadScopeColor() => "SS03;";
    public static string SetScopeColor(int index) => "SS03" + Clamp(index, 0, 10).ToString("X") + "0000;";

    public static int? ParseScopeColor(string reply)
    {
        if (reply.Length != 10 || reply[0] != 'S' || reply[1] != 'S' || reply[3] != '3' || reply[9] != ';') return null;
        return int.TryParse(reply[4].ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// -30.0..+30.0 dB, 0.5 dB steps (documented as a fixed 5-char signed field).
    public static string ReadScopeLevel() => "SS04;";

    public static string SetScopeLevel(double dB)
    {
        var clamped = Math.Max(-30, Math.Min(30, dB));
        var sign = clamped < 0 ? "-" : "+";
        return "SS04" + sign + Math.Abs(clamped).ToString("00.0", CultureInfo.InvariantCulture) + ";";
    }

    public static double? ParseScopeLevel(string reply)
    {
        if (reply.Length != 10 || reply[0] != 'S' || reply[1] != 'S' || reply[3] != '4' || reply[9] != ';') return null;
        var payload = reply.Substring(4, 5);
        if (!double.TryParse(payload.Substring(1), NumberStyles.Float, CultureInfo.InvariantCulture, out var mag)) return null;
        return payload[0] == '-' ? -mag : mag;
    }

    // MARK: - Display (DA) — D-CONTRAST / DIMMER. A combined 3-value Set; callers
    // must read-then-merge since there's no way to change just one field.

    public static string ReadDisplay() => "DA;";

    public static string SetDisplay(int contrast, int brightness, int ledBrightness) =>
        "DA00" + Pad(Clamp(contrast, 0, 20), 2) + Pad(Clamp(brightness, 0, 20), 2) + Pad(Clamp(ledBrightness, 0, 20), 2) + ";";

    public static bool ParseDisplay(string reply, out int contrast, out int brightness, out int led)
    {
        contrast = brightness = led = 0;
        if (reply.Length != 11 || reply[0] != 'D' || reply[1] != 'A' || reply[10] != ';') return false;
        if (!int.TryParse(reply.Substring(4, 2), out contrast)) return false;
        if (!int.TryParse(reply.Substring(6, 2), out brightness)) return false;
        if (!int.TryParse(reply.Substring(8, 2), out led)) return false;
        return true;
    }

    // MARK: - Parametric Mic EQ enable (PR, P1=1) — the FUNC panel's "MIC EQ" button.
    //
    // The manual documents P2 as 1="OFF"/2="ON". Three rounds of hardware
    // evidence narrowed this down:
    //  1. Set "2" for the manual's "ON" actually turned it off — so 1/2
    //     are at least swapped from what's printed.
    //  2. The Answer never used "2" at all — off consistently came back as
    //     something the parser needed to accept as "0", not "2".
    //  3. With Set using "2" for off, ON worked but OFF silently did
    //     nothing on the radio — consistent with "2" being an out-of-range
    //     value for a field the radio otherwise treats as 0/1.
    // Conclusion: P2 is almost certainly a plain 0=off/1=on field like
    // nearly every other command in this protocol, and the manual's "1/2"
    // description is simply wrong about the value scheme, not just
    // reversed. Set uses 0/1 to match what the Answer already proved.

    public static string ReadMicEq() => "PR1;";
    public static string SetMicEq(bool on) => on ? "PR11;" : "PR10;";

    /// Still accepts "2" as an alternate off value in case the Set-side 0/1
    /// theory is wrong and some firmware path still emits it — costs
    /// nothing to allow both since "1" unambiguously means on either way.
    public static bool? ParseMicEq(string reply)
    {
        if (reply.Length != 5 || reply[0] != 'P' || reply[1] != 'R' || reply[4] != ';') return null;
        return reply[3] switch { '1' => true, '0' or '2' => false, _ => (bool?)null };
    }

    // MARK: - Speech Processor Level (PL) — "PROC LEVEL"

    public static string ReadProcLevel() => "PL;";
    public static string SetProcLevel(int level) => "PL" + Pad(Clamp(level, 0, 100), 3) + ";";

    public static int? ParseProcLevel(string reply)
    {
        if (reply.Length != 6 || reply[0] != 'P' || reply[1] != 'L' || reply[5] != ';') return null;
        return int.TryParse(reply.Substring(2, 3), out var v) ? v : null;
    }

    // MARK: - Antenna Tuner (AC) — "ANT TUNE" / "TUNER". P1/P2 identify which
    // tuner hardware is fitted (documented values differ by FTX-1 variant); we
    // read the radio's own answer for P1/P2 and only ever change P3 (the on/
    // off/start field) — same "ask, don't assume" pattern used for PC.

    public static string ReadAntennaTuner() => "AC;";
    public static string SetAntennaTuner(char p1, char p2, char p3) => $"AC{p1}{p2}{p3};";

    public static bool ParseAntennaTuner(string reply, out char p1, out char p2, out char p3)
    {
        p1 = p2 = p3 = default;
        if (reply.Length != 6 || reply[0] != 'A' || reply[1] != 'C' || reply[5] != ';') return false;
        p1 = reply[2];
        p2 = reply[3];
        p3 = reply[4];
        return true;
    }

    // MARK: - Noise Blanker Level (NL) — "NB"

    public static string ReadNoiseBlanker() => "NL0;";
    public static string SetNoiseBlanker(int level) => "NL0" + Pad(Clamp(level, 0, 10), 3) + ";";

    public static int? ParseNoiseBlanker(string reply)
    {
        if (reply.Length != 7 || reply[0] != 'N' || reply[1] != 'L' || reply[6] != ';') return null;
        return int.TryParse(reply.Substring(3, 3), out var v) ? v : null;
    }

    // MARK: - VOX (VX on/off, VG gain, VD delay) — "VOX" group

    public static string ReadVox() => "VX;";
    public static string SetVox(bool on) => on ? "VX1;" : "VX0;";

    public static bool? ParseVox(string reply)
    {
        if (reply.Length != 4 || reply[0] != 'V' || reply[1] != 'X' || reply[3] != ';') return null;
        return reply[2] switch { '0' => false, '1' => true, _ => (bool?)null };
    }

    public static string ReadVoxGain() => "VG;";
    public static string SetVoxGain(int gain) => "VG" + Pad(Clamp(gain, 0, 100), 3) + ";";

    public static int? ParseVoxGain(string reply)
    {
        if (reply.Length != 6 || reply[0] != 'V' || reply[1] != 'G' || reply[5] != ';') return null;
        return int.TryParse(reply.Substring(2, 3), out var v) ? v : null;
    }

    /// Index 0..33: 00=30ms, 01=50ms, 02=100ms, 03=150ms, 04=200ms,
    /// 05=250ms, then 06..33 in fixed 100ms steps from 300ms up to 3000ms.
    /// The manual's own printed text says "10 msec multiples" for that
    /// upper range, but that's inconsistent with its own stated endpoints
    /// (06=300ms, 33=3000ms only works out to 300 + (n-6)*100, not *10) —
    /// almost certainly a PDF-extraction OCR error dropping a digit ("100"
    /// -> "10"), not a real 10ms step. Trusting the arithmetic implied by
    /// the two confirmed endpoints over the ambiguous prose.
    public static string ReadVoxDelay() => "VD;";
    public static string SetVoxDelay(int index) => "VD" + Pad(Clamp(index, 0, 33), 2) + ";";

    public static int? ParseVoxDelay(string reply)
    {
        if (reply.Length != 5 || reply[0] != 'V' || reply[1] != 'D' || reply[4] != ';') return null;
        return int.TryParse(reply.Substring(2, 2), out var v) ? v : null;
    }

    public static int VoxDelayMs(int index) => index switch
    {
        0 => 30,
        1 => 50,
        2 => 100,
        3 => 150,
        4 => 200,
        5 => 250,
        _ => 300 + (Clamp(index, 6, 33) - 6) * 100,
    };

    // MARK: - TXW (TS) — command letter and Set/Read/Answer shape confirmed
    // against the CAT Operation Reference Manual's command table ("TS TXW",
    // all four of Set/Read/Answer supported, P1 0="OFF"/1="ON"). The
    // Operation Manual's own SPLIT and FUNC-menu sections describe what it
    // does but not how the physical button behaves — confirmed directly on
    // the radio's own touchscreen: it's a latching on/off toggle, same
    // interaction model as Tuner/Mic EQ, not press-and-hold. It only has an
    // audible effect while Split is active; with Split off the radio still
    // echoes TS1; back correctly over CAT. UI should mirror this as a
    // toggle button, disabled unless Split is on.

    public static string ReadTxw() => "TS;";
    public static string SetTxw(bool on) => on ? "TS1;" : "TS0;";

    public static bool? ParseTxw(string reply)
    {
        if (reply.Length != 4 || reply[0] != 'T' || reply[1] != 'S' || reply[3] != ';') return null;
        return reply[2] switch { '0' => false, '1' => true, _ => (bool?)null };
    }

    // MARK: - Mic Gain (MG)

    public static string ReadMicGain() => "MG;";
    public static string SetMicGain(int gain) => "MG" + Pad(Clamp(gain, 0, 100), 3) + ";";

    public static int? ParseMicGain(string reply)
    {
        if (reply.Length != 6 || reply[0] != 'M' || reply[1] != 'G' || reply[5] != ';') return null;
        return int.TryParse(reply.Substring(2, 3), out var v) ? v : null;
    }

    // MARK: - AMC Output Level (AO) — "AMC LEVEL"

    public static string ReadAmcLevel() => "AO;";
    public static string SetAmcLevel(int level) => "AO" + Pad(Clamp(level, 1, 100), 3) + ";";

    public static int? ParseAmcLevel(string reply)
    {
        if (reply.Length != 6 || reply[0] != 'A' || reply[1] != 'O' || reply[5] != ';') return null;
        return int.TryParse(reply.Substring(2, 3), out var v) ? v : null;
    }

    // MARK: - CW group: KEYER (KR), BK-IN (BI), CW PITCH (KP), CW SPEED (KS), CW DELAY (SD), CW SPOT (CS), ZIN (ZI)

    public static string ReadKeyer() => "KR;";
    public static string SetKeyer(bool on) => on ? "KR1;" : "KR0;";

    public static bool? ParseKeyer(string reply)
    {
        if (reply.Length != 4 || reply[0] != 'K' || reply[1] != 'R' || reply[3] != ';') return null;
        return reply[2] switch { '0' => false, '1' => true, _ => (bool?)null };
    }

    public static string ReadBreakIn() => "BI;";
    public static string SetBreakIn(bool on) => on ? "BI1;" : "BI0;";

    public static bool? ParseBreakIn(string reply)
    {
        if (reply.Length != 4 || reply[0] != 'B' || reply[1] != 'I' || reply[3] != ';') return null;
        return reply[2] switch { '0' => false, '1' => true, _ => (bool?)null };
    }

    /// P1 00-75 maps to 300-1050 Hz in 10 Hz steps (300 + P1*10) — this
    /// endpoint arithmetic is unambiguous in the manual.
    public static string ReadKeyPitch() => "KP;";

    public static string SetKeyPitch(int hz)
    {
        var index = Clamp((hz - 300) / 10, 0, 75);
        return "KP" + Pad(index, 2) + ";";
    }

    public static int? ParseKeyPitch(string reply)
    {
        if (reply.Length != 5 || reply[0] != 'K' || reply[1] != 'P' || reply[4] != ';') return null;
        if (!int.TryParse(reply.Substring(2, 2), out var index)) return null;
        return 300 + index * 10;
    }

    /// P1 004-060 (WPM), 3 digits — no scaling, the raw value is the speed.
    public static string ReadKeySpeed() => "KS;";
    public static string SetKeySpeed(int wpm) => "KS" + Pad(Clamp(wpm, 4, 60), 3) + ";";

    public static int? ParseKeySpeed(string reply)
    {
        if (reply.Length != 6 || reply[0] != 'K' || reply[1] != 'S' || reply[5] != ';') return null;
        return int.TryParse(reply.Substring(2, 3), out var v) ? v : null;
    }

    /// Index 0-33 -> ms via CwBreakInDelayMs, same irregular-then-linear
    /// shape as VoxDelayMs (the manual gives this one an explicit note
    /// confirming the clean 100ms/step for 6-33, unlike VOX Delay's OCR-error case).
    public static string ReadCwBreakInDelay() => "SD;";
    public static string SetCwBreakInDelay(int index) => "SD" + Pad(Clamp(index, 0, 33), 2) + ";";

    public static int? ParseCwBreakInDelay(string reply)
    {
        if (reply.Length != 5 || reply[0] != 'S' || reply[1] != 'D' || reply[4] != ';') return null;
        return int.TryParse(reply.Substring(2, 2), out var v) ? v : null;
    }

    public static int CwBreakInDelayMs(int index) => index switch
    {
        0 => 30,
        1 => 50,
        2 => 100,
        3 => 150,
        4 => 200,
        5 => 250,
        _ => 300 + (Clamp(index, 6, 33) - 6) * 100,
    };

    // MARK: - Load Message (LM) / Play Back (PB) — NOT CW keying memory.
    // LM sits in the FUNC quick-menu but is really two distinct radio
    // features sharing a command letter; PB is a third, separate command
    // entirely. An earlier version of the Mac app got LM's two halves
    // backwards AND assumed (wrongly) that LM alone covered playback too:
    //
    // LM P1=0 (MESSAGE) — voice memory: up to 5 pre-recorded slots of your
    // own voice. P2 0=stop, 1-5=Select CH "N" — confirmed on hardware that
    // selecting a channel this way ARMS recording for it (not "select and
    // play," despite reading like a plain selector).
    //
    // LM P1=1 (RECORD) — an unrelated feature: recording *received* audio
    // to the SD card (a QSO logger), not voice memory. P2 0=stop, 1=start.
    //
    // PB P1=0 (Fixed) — the genuine playback command: P2 0=MESSAGE
    // Playback/Recording Stop, 1-5=MESSAGE CH "N" Playback Start.

    public static string ReadVoiceMessageChannel() => "LM0;";
    public static string SetVoiceMessageChannel(int channel) => $"LM0{Clamp(channel, 0, 5)};";

    public static int? ParseVoiceMessageChannel(string reply)
    {
        if (reply.Length != 5 || reply[0] != 'L' || reply[1] != 'M' || reply[2] != '0' || reply[4] != ';') return null;
        return reply[3] - '0';
    }

    public static string ReadSdRecording() => "LM1;";
    public static string SetSdRecording(bool on) => on ? "LM11;" : "LM10;";

    public static bool? ParseSdRecording(string reply)
    {
        if (reply.Length != 5 || reply[0] != 'L' || reply[1] != 'M' || reply[2] != '1' || reply[4] != ';') return null;
        return reply[3] switch { '0' => false, '1' => true, _ => (bool?)null };
    }

    /// P1 is always "0" (Fixed per the manual) — every call here bakes
    /// that in rather than exposing a meaningless parameter.
    public static string ReadMessagePlayback() => "PB0;";
    public static string SetMessagePlayback(int channel) => $"PB0{Clamp(channel, 0, 5)};";

    public static int? ParseMessagePlayback(string reply)
    {
        if (reply.Length != 5 || reply[0] != 'P' || reply[1] != 'B' || reply[2] != '0' || reply[4] != ';') return null;
        return reply[3] - '0';
    }

    public static string ReadCwSpot() => "CS;";
    public static string SetCwSpot(bool on) => on ? "CS1;" : "CS0;";

    public static bool? ParseCwSpot(string reply)
    {
        if (reply.Length != 4 || reply[0] != 'C' || reply[1] != 'S' || reply[3] != ';') return null;
        return reply[2] switch { '0' => false, '1' => true, _ => (bool?)null };
    }

    // MARK: - Monitor Level (ML) — FUNC page 2's "MONI LEVEL". P1 selects
    // sub-target (0: MONI on/off, 1: MONI Level); only P1=1 (Level,
    // 000-100) is exposed here — there's no separate MONI on/off cell in
    // the FUNC quick-menu grid to wire P1=0 up to.

    public static string ReadMonitorLevel() => "ML1;";
    public static string SetMonitorLevel(int level) => "ML1" + Pad(Clamp(level, 0, 100), 3) + ";";

    /// Reply: M L 1 <p2 x3> ; (7 chars)
    public static int? ParseMonitorLevel(string reply)
    {
        if (reply.Length != 7 || reply[0] != 'M' || reply[1] != 'L' || reply[2] != '1' || reply[6] != ';') return null;
        return int.TryParse(reply.Substring(3, 3), out var v) ? v : null;
    }

    /// Set-only per the command table (no Read/Answer) — a momentary action,
    /// not a persisted toggle.
    public static string TriggerZeroIn() => "ZI0;";
}
