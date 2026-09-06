namespace FTX1WinController.Models;

/// Ported 1:1 from the Mac app's CATProtocol.swift `CATCommands.MeterKind`.
/// Manual's RM P1 table: 1=S(Main) 2=S(Sub) 3=COMP 4=ALC 5=PO 6=SWR
/// 7=IDD(drain current) 8=VDD(drain voltage) — matches the radio's own
/// METER selection screen (PO/COMP/ALC/VDD/ID/SWR) plus S-meter; the
/// screen's 7th option, TEMP, has no RM code at all.
public enum MeterKind
{
    SMeterMain, Comp, Alc, PowerOutput, Swr, Idd, Vdd,
}

public static class MeterKindInfo
{
    public static readonly MeterKind[] All = (MeterKind[])Enum.GetValues(typeof(MeterKind));

    public static char RmCode(this MeterKind kind) => kind switch
    {
        MeterKind.SMeterMain => '1',
        MeterKind.Comp => '3',
        MeterKind.Alc => '4',
        MeterKind.PowerOutput => '5',
        MeterKind.Swr => '6',
        MeterKind.Idd => '7',
        MeterKind.Vdd => '8',
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string DisplayName(this MeterKind kind) => kind switch
    {
        MeterKind.SMeterMain => "S",
        MeterKind.Comp => "COMP",
        MeterKind.Alc => "ALC",
        MeterKind.PowerOutput => "PO",
        MeterKind.Swr => "SWR",
        MeterKind.Idd => "ID",
        MeterKind.Vdd => "VDD",
        _ => "?",
    };

    /// MS (METER SW) uses a completely different code space from RM's P1
    /// (0:PO 1:COMP 2:ALC 3:VDD 4:ID 5:SWR) — this is what actually switches
    /// which meter the radio's own screen displays; RM can read any meter's
    /// value regardless of what MS currently has selected. Null for
    /// SMeterMain, which isn't part of MS at all.
    public static char? MsCode(this MeterKind kind) => kind switch
    {
        MeterKind.PowerOutput => '0',
        MeterKind.Comp => '1',
        MeterKind.Alc => '2',
        MeterKind.Vdd => '3',
        MeterKind.Idd => '4',
        MeterKind.Swr => '5',
        _ => null,
    };

    public static MeterKind? FromMsCode(char code) => code switch
    {
        '0' => MeterKind.PowerOutput,
        '1' => MeterKind.Comp,
        '2' => MeterKind.Alc,
        '3' => MeterKind.Vdd,
        '4' => MeterKind.Idd,
        '5' => MeterKind.Swr,
        _ => null,
    };
}
