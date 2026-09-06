namespace FTX1WinController.Models;

/// Ported 1:1 from the Mac app's CATProtocolV2.swift `CATCommands.SquelchToneType` (CT command).
public enum ToneType
{
    Off, CtcssEncOnly, CtcssEncDec, Dcs, PrFreq, RevTone,
}

public static class ToneTypeInfo
{
    public static readonly ToneType[] All = (ToneType[])Enum.GetValues(typeof(ToneType));

    public static char CatCode(this ToneType type) => type switch
    {
        ToneType.Off => '0',
        ToneType.CtcssEncOnly => '1',
        ToneType.CtcssEncDec => '2',
        ToneType.Dcs => '3',
        ToneType.PrFreq => '4',
        ToneType.RevTone => '5',
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static ToneType? FromCatCode(char code) => code switch
    {
        '0' => ToneType.Off,
        '1' => ToneType.CtcssEncOnly,
        '2' => ToneType.CtcssEncDec,
        '3' => ToneType.Dcs,
        '4' => ToneType.PrFreq,
        '5' => ToneType.RevTone,
        _ => null,
    };
}
