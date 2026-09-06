namespace FTX1WinController.Settings;

/// One saved snapshot of the radio's current settings, captured/recalled
/// via the Presets popover (MainViewModel.SavePresetCommand /
/// PresetViewModel.RecallCommand). Frequency/Mode/D-Level/RF Power/Preamp
/// each get their own field since they aren't wrapped in an Int/Toggle/
/// ChoiceSettingViewModel; everything that IS (FUNC Page 1/2 sliders,
/// toggles, AGC/ANT, Scan/Split) lives in `Values`, keyed by that setting's
/// own opaque id (e.g. "SCOPEPEAK", "ATT", "AGC" — see ISettingBinding.Id)
/// so recall can replay each entry through its ApplyRawAsync without
/// needing a hand-maintained field per setting.
public sealed class PresetData
{
    public string Name { get; set; } = "";
    public long? FrequencyHz { get; set; }
    public char? ModeCode { get; set; }
    public double? DLevel { get; set; }
    public int? RfPowerWatts { get; set; }
    public int? PreampValue { get; set; }
    public Dictionary<string, string> Values { get; set; } = new();
}
