using FTX1WinController.Models;

namespace FTX1WinController.Cat;

/// v4 additions: the VFO knob's surrounding physical controls — CLAR,
/// FINE/FAST, QMB, and AF Gain (the third leg of the MAIN-side AF/RF/SQL
/// knob, alongside the already-implemented RF Gain and Squelch). Ported 1:1
/// from the Mac app's CATProtocolV4.swift. DISP, BACK, and the BUSY/TX
/// SUB-MAIN slider have no CAT command anywhere in the reference (checked,
/// not assumed) — pure front-panel/menu-navigation controls with nothing to
/// mirror here.
public static partial class CatCommands
{
    // MARK: - Clarifier (CF) — P1 fixed to 0 = MAIN-side, P2 fixed to 0.
    // P3 selects which of two independent sub-values a Set/Read touches:
    // 0 = RX/TX CLAR on-off, 1 = CLAR frequency offset. Two separate reads
    // are needed to get both, same "P1-selects-the-answer" shape as RM/MS.

    public static string ReadClarifierState() => "CF000;";
    public static string SetClarifierState(bool rxOn, bool txOn) => "CF000" + (rxOn ? "1" : "0") + (txOn ? "1" : "0") + "000;";

    /// Reply: C F P1 P2 P3 P4 P5 P6 P7 P8 ; (11 chars). P3 (index 4) == "0"
    /// confirms this is the on/off answer, not the frequency one.
    public static bool ParseClarifierState(string reply, out bool rx, out bool tx)
    {
        rx = tx = false;
        if (reply.Length != 11 || reply[0] != 'C' || reply[1] != 'F' || reply[10] != ';' || reply[4] != '0') return false;
        rx = reply[5] == '1';
        tx = reply[6] == '1';
        return true;
    }

    public static string ReadClarifierFrequency() => "CF001;";

    public static string SetClarifierFrequency(int hz)
    {
        var clamped = Clamp(hz, -9999, 9999);
        var sign = clamped < 0 ? "-" : "+";
        return "CF001" + sign + Pad(Math.Abs(clamped), 4) + ";";
    }

    public static int? ParseClarifierFrequency(string reply)
    {
        if (reply.Length != 11 || reply[0] != 'C' || reply[1] != 'F' || reply[10] != ';' || reply[4] != '1') return null;
        if (!int.TryParse(reply.Substring(6, 4), out var magnitude)) return null;
        return reply[5] == '-' ? -magnitude : magnitude;
    }

    // MARK: - Fine/Fast Tuning (FN)

    public static string ReadFineTuning() => "FN;";
    public static string SetFineTuning(FineTuningState state) => $"FN{state.CatCode()};";

    public static FineTuningState? ParseFineTuning(string reply)
    {
        if (reply.Length != 4 || reply[0] != 'F' || reply[1] != 'N' || reply[3] != ';') return null;
        return FineTuningStateInfo.FromCatCode(reply[2]);
    }

    // MARK: - QMB (QI store / QR recall) — Set-only, no Read/Answer per the
    // manual's own command table, so these are pure fire-and-forget actions
    // like Ant Tune, not a value to read back.

    public static string QmbStore() => "QI;";
    public static string QmbRecall() => "QR;";

    // MARK: - AF Gain (AG) — the third leg of the MAIN-side AF/RF/SQL knob;
    // RF Gain (RG) and Squelch (SQ) are implemented in CatCommands.Dsp.cs.

    public static string ReadAfGain() => "AG0;";
    public static string SetAfGain(int value) => "AG0" + Pad(Clamp(value, 0, 255), 3) + ";";

    public static int? ParseAfGain(string reply)
    {
        if (reply.Length != 7 || reply[0] != 'A' || reply[1] != 'G' || reply[6] != ';') return null;
        return int.TryParse(reply.Substring(3, 3), out var v) ? v : null;
    }
}
