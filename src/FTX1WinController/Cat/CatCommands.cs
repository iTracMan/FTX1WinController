using FTX1WinController.Models;

namespace FTX1WinController.Cat;

/// Pure command builders/parsers for the Yaesu FTX-1 text CAT protocol, per
/// the official "FTX-1 Series CAT Operation Reference Manual" (2508-C).
/// No I/O here — see CatConnection for the serial transport.
///
/// Ported 1:1 from the Mac app's (hardware-tested) CATProtocol.swift, whose
/// V2/V3/V4 extensions are mirrored here as the CatCommands.Dsp.cs /
/// CatCommands.Func.cs / CatCommands.Vfo.cs partial-class files instead of
/// Swift `extension` blocks.
public static partial class CatCommands
{
    // MARK: Frequency (FA — VFO MAIN-side)

    public static string ReadFrequency() => "FA;";

    public static string SetFrequency(int hz)
    {
        var clamped = Clamp(hz, 30_000, 470_000_000);
        return "FA" + Pad(clamped, 9) + ";";
    }

    public static int? ParseFrequency(string reply)
    {
        if (reply.Length != 12 || !reply.StartsWith("FA") || reply[11] != ';') return null;
        return int.TryParse(reply.Substring(2, 9), out var v) ? v : null;
    }

    // MARK: Mode (MD — P1 fixed to 0 = MAIN-side)

    public static string ReadMode() => "MD0;";

    public static string SetMode(RadioMode mode) => $"MD0{mode.CatCode()};";

    /// Sets an arbitrary documented mode code directly, for modes with no
    /// dedicated button (e.g. recalling a preset saved while the radio was
    /// in CW or a DATA mode).
    public static string SetModeCode(char code) => $"MD0{code};";

    public static char? ParseMode(string reply)
    {
        if (reply.Length != 5 || !reply.StartsWith("MD0") || reply[4] != ';') return null;
        return reply[3];
    }

    // MARK: PTT (TX)

    public static string ReadTx() => "TX;";
    public static string SetTx(bool on) => on ? "TX1;" : "TX0;";

    /// TX0 = receive. TX1 = CAT-triggered transmit. TX2 (answer-only) =
    /// transmitting via some other means (mic PTT, footswitch, VOX).
    public static bool? ParseTxState(string reply)
    {
        if (reply.Length != 4 || !reply.StartsWith("TX") || reply[3] != ';') return null;
        return reply[2] switch
        {
            '0' => false,
            '1' or '2' => true,
            _ => (bool?)null,
        };
    }

    // MARK: Meters (RM — read only)

    /// The TX-meter cycle order matching the radio's own METER selection
    /// screen (PO/COMP/ALC/VDD/ID/SWR, left to right, top then bottom row)
    /// — S-meter isn't part of this cycle, it's always shown on its own arc.
    public static readonly MeterKind[] TxMeterCycle =
    {
        MeterKind.PowerOutput, MeterKind.Comp, MeterKind.Alc, MeterKind.Vdd, MeterKind.Idd, MeterKind.Swr,
    };

    public static string ReadMeter(MeterKind kind) => $"RM{kind.RmCode()};";

    /// Answer format is "RM" + P1(1) + P2(3, the 0-255 reading) + P3(3, fixed) + ";" = 10 chars.
    public static int? ParseMeter(string reply, MeterKind expected)
    {
        if (reply.Length != 10 || !reply.StartsWith("RM") || reply[9] != ';') return null;
        if (reply[2] != expected.RmCode()) return null;
        return int.TryParse(reply.Substring(3, 3), out var v) ? v : null;
    }

    // MARK: Meter selection (MS — which meter the radio's own screen shows;
    // P1 = MAIN-side, P2 = SUB-side, same MeterKind code space as above)

    public static string ReadMeterSwitch() => "MS;";

    public static string? SetMeterSwitch(MeterKind main, MeterKind sub)
    {
        var mainCode = main.MsCode();
        var subCode = sub.MsCode();
        if (mainCode is null || subCode is null) return null;
        return $"MS{mainCode}{subCode};";
    }

    /// Answer format is "MS" + P1(1) + P2(1) + ";" = 5 chars.
    public static (MeterKind main, MeterKind sub)? ParseMeterSwitch(string reply)
    {
        if (reply.Length != 5 || !reply.StartsWith("MS") || reply[4] != ';') return null;
        var main = MeterKindInfo.FromMsCode(reply[2]);
        var sub = MeterKindInfo.FromMsCode(reply[3]);
        if (main is null || sub is null) return null;
        return (main.Value, sub.Value);
    }

    // MARK: Identification

    public static string ReadIdentity() => "ID;";

    public static string? ParseIdentity(string reply)
    {
        if (!reply.StartsWith("ID") || !reply.EndsWith(";")) return null;
        return reply.Substring(2, reply.Length - 3);
    }

    // MARK: Small helpers shared by every partial (Dsp/Func/Vfo) — a single
    // shared copy is possible in C# (unlike Swift, where each `extension`
    // file needed its own private copy).

    internal static int Clamp(int v, int lo, int hi) => Math.Max(lo, Math.Min(hi, v));

    internal static string Pad(int v, int digits) => v.ToString("D" + digits);

    /// "-20".."+10" style: sign + 2-digit zero-padded magnitude.
    internal static string SignedPad(int v) => (v < 0 ? "-" : "+") + Math.Abs(v).ToString("D2");

    internal static int? ParseSignedPad(string s)
    {
        if (s.Length != 3 || !int.TryParse(s.Substring(1, 2), out var mag)) return null;
        return s[0] == '-' ? -mag : mag;
    }
}
