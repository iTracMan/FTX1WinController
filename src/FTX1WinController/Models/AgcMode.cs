namespace FTX1WinController.Models;

/// Ported 1:1 from the Mac app's CATProtocolV2.swift `CATCommands.AGCMode`.
/// GT's Set/Read P2 is this 5-value mode (0-4, confirmed against the FTX-1
/// CAT Operation Reference Manual); the Answer's P3 is a *different*
/// 7-value field (0-6) where 4/5/6 all collapse back to Auto (the radio's
/// own AUTO-FAST/AUTO-MID/AUTO-SLOW sub-tiers, which this app has no use
/// for distinguishing) — see Cat.CatCommands.ParseAgc.
public enum AgcMode
{
    Off = 0, Fast = 1, Mid = 2, Slow = 3, Auto = 4,
}

public static class AgcModeInfo
{
    public static readonly AgcMode[] All = (AgcMode[])Enum.GetValues(typeof(AgcMode));

    public static string DisplayName(this AgcMode mode) => mode switch
    {
        AgcMode.Off => "OFF",
        AgcMode.Fast => "FAST",
        AgcMode.Mid => "MID",
        AgcMode.Slow => "SLOW",
        AgcMode.Auto => "AUTO",
        _ => "?",
    };
}
