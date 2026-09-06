namespace FTX1WinController.Models;

/// Ported 1:1 from the Mac app's Models/RadioMode.swift — codes are from the
/// FTX-1 CAT Operation Reference Manual (MD command), hardware-confirmed.
/// The manual documents exactly two digital-voice codes (C4FM-DN, C4FM-VW),
/// no plain "C4FM"/"VW" code, and AMS has no MD code at all (0/G/J reserved)
/// — see RadioModeInfo.DisplayNameForCode for how the "0" sentinel the radio
/// actually sends while AMS is auto-selecting gets displayed.
public enum RadioMode
{
    Lsb, Usb, CwL, CwU,
    Am, AmN, Fm, FmN, C4fmDN,
    DataL, DataU, DFm, DFmN, C4fmVW,
    RttyL, RttyU, Psk,
}

public static class RadioModeInfo
{
    public static readonly RadioMode[] All = (RadioMode[])Enum.GetValues(typeof(RadioMode));

    public static char CatCode(this RadioMode mode) => mode switch
    {
        RadioMode.Lsb => '1',
        RadioMode.Usb => '2',
        RadioMode.CwU => '3',
        RadioMode.Fm => '4',
        RadioMode.Am => '5',
        RadioMode.RttyL => '6',
        RadioMode.CwL => '7',
        RadioMode.DataL => '8',
        RadioMode.RttyU => '9',
        RadioMode.DFm => 'A',
        RadioMode.FmN => 'B',
        RadioMode.DataU => 'C',
        RadioMode.AmN => 'D',
        RadioMode.Psk => 'E',
        RadioMode.DFmN => 'F',
        RadioMode.C4fmDN => 'H',
        RadioMode.C4fmVW => 'I',
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// Reverse of CatCode — used to interpret a raw `MD` reply code, which
    /// may not correspond to any RadioMode case at all (see DisplayNameForCode).
    public static bool TryFromCatCode(char code, out RadioMode mode)
    {
        foreach (var candidate in All)
        {
            if (candidate.CatCode() == char.ToUpperInvariant(code))
            {
                mode = candidate;
                return true;
            }
        }
        mode = default;
        return false;
    }

    /// Displays a raw `MD` reply code directly, handling the one code with
    /// no corresponding RadioMode case: the manual documents "0" as
    /// reserved/undefined, and hardware testing confirmed it's what the
    /// radio actually sends for MD's P2 whenever AMS has auto-selected the
    /// mode — CAT has no way to read back which real mode AMS landed on, it
    /// just reports this sentinel instead. Not a parse failure.
    public static string DisplayNameForCode(char code)
    {
        if (code == '0') return "AMS (auto)";
        return TryFromCatCode(code, out var mode) ? mode.DisplayName() : $"Unknown ({code})";
    }

    public static string DisplayName(this RadioMode mode) => mode switch
    {
        RadioMode.Lsb => "LSB",
        RadioMode.Usb => "USB",
        RadioMode.CwL => "CW-L",
        RadioMode.CwU => "CW-U",
        RadioMode.Fm => "FM",
        RadioMode.FmN => "FM-N",
        RadioMode.Am => "AM",
        RadioMode.AmN => "AM-N",
        RadioMode.RttyL => "RTTY-L",
        RadioMode.RttyU => "RTTY-U",
        RadioMode.DataL => "DATA-L",
        RadioMode.DataU => "DATA-U",
        RadioMode.DFm => "D-FM",
        RadioMode.DFmN => "D-FM-N",
        RadioMode.Psk => "PSK",
        RadioMode.C4fmDN => "C4FM",
        RadioMode.C4fmVW => "VW",
        _ => "?",
    };
}
