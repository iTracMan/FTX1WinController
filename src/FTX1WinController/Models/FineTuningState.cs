namespace FTX1WinController.Models;

/// Ported 1:1 from the Mac app's CATProtocolV4.swift `CATCommands.FineTuningState` (FN command).
public enum FineTuningState
{
    Off, Fine, Fast,
}

public static class FineTuningStateInfo
{
    public static readonly FineTuningState[] All = (FineTuningState[])Enum.GetValues(typeof(FineTuningState));

    public static char CatCode(this FineTuningState state) => state switch
    {
        FineTuningState.Off => '0',
        FineTuningState.Fine => '1',
        FineTuningState.Fast => '2',
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static string DisplayName(this FineTuningState state) => state switch
    {
        FineTuningState.Off => "OFF",
        FineTuningState.Fine => "FINE",
        FineTuningState.Fast => "FAST",
        _ => "?",
    };

    public static FineTuningState? FromCatCode(char code) => code switch
    {
        '0' => FineTuningState.Off,
        '1' => FineTuningState.Fine,
        '2' => FineTuningState.Fast,
        _ => null,
    };
}
