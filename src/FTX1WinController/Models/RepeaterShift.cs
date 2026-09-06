namespace FTX1WinController.Models;

/// Ported 1:1 from the Mac app's CATProtocolV2.swift `CATCommands.RepeaterShift` (OS command).
public enum RepeaterShift
{
    Simplex, Plus, Minus, Ars,
}

public static class RepeaterShiftInfo
{
    public static readonly RepeaterShift[] All = (RepeaterShift[])Enum.GetValues(typeof(RepeaterShift));

    public static char CatCode(this RepeaterShift shift) => shift switch
    {
        RepeaterShift.Simplex => '0',
        RepeaterShift.Plus => '1',
        RepeaterShift.Minus => '2',
        RepeaterShift.Ars => '3',
        _ => throw new ArgumentOutOfRangeException(nameof(shift)),
    };

    public static RepeaterShift? FromCatCode(char code) => code switch
    {
        '0' => RepeaterShift.Simplex,
        '1' => RepeaterShift.Plus,
        '2' => RepeaterShift.Minus,
        '3' => RepeaterShift.Ars,
        _ => null,
    };
}
