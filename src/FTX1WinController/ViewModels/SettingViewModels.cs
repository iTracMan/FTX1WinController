using System.Collections.ObjectModel;
using FTX1WinController.Bridge;

namespace FTX1WinController.ViewModels;

/// Common shape shared by Int/Toggle/ChoiceSettingViewModel so
/// MainViewModel's Presets feature can capture/replay any of them
/// uniformly — "GET &lt;bridgeName&gt;" / "SET &lt;bridgeName&gt; &lt;raw&gt;" with the
/// raw wire value round-tripped verbatim, no per-setting-type code needed
/// at the call site.
public interface IBridgeSetting
{
    string Label { get; }
    string BridgeName { get; }
    Task<string?> CaptureRawAsync();
    Task<bool> ApplyRawAsync(string raw);
}

/// A single numeric setting synced with the bridge via a "GET <name>" /
/// "SET <name> <value>" pair, with a shared -/+ stepper pattern (bound to
/// RepeatButton in XAML for click-and-hold, same as the VFO/Clarifier
/// steppers from Round 1). Exists so ~15 lines of near-identical property/
/// command/refresh boilerplate isn't repeated by hand for every one of
/// FUNC Page 1's dozen-plus sliders — one implementation to get right and
/// review, instead of many near-duplicates.
public sealed class IntSettingViewModel : ObservableObject, IBridgeSetting
{
    private readonly BridgeClient _bridge;
    private readonly string _bridgeName;
    private readonly int _min;
    private readonly int _max;
    private readonly Action<string> _onError;

    public string Label { get; }
    public string BridgeName => _bridgeName;
    public Func<int, string>? Format { get; set; }

    /// Exposed so the FUNC-grid popup slider (FuncSliderPopupCellTemplate)
    /// can bind Slider.Minimum/Maximum directly instead of duplicating
    /// each setting's range in XAML.
    public int Min => _min;
    public int Max => _max;

    private int _value;
    public int Value { get => _value; private set { if (SetProperty(ref _value, value)) OnPropertyChanged(nameof(DisplayText)); } }

    public string DisplayText => Format?.Invoke(_value) ?? _value.ToString();

    /// Same formatting as DisplayText, but for a value that hasn't been
    /// committed yet — the popup slider's live preview while dragging
    /// (before mouse-up actually sends the SET) uses this instead of
    /// DisplayText so it reflects the drag position, not the last
    /// confirmed value.
    public string FormatValue(double raw) => Format?.Invoke((int)Math.Round(raw)) ?? ((int)Math.Round(raw)).ToString();

    /// Direct "set to this value" — used by the popup slider on drag
    /// release. Unlike UpCommand/DownCommand (relative +/- steps), this
    /// takes an absolute target; relies on the Slider's own Minimum/Maximum
    /// (bound to Min/Max above) to keep it in range rather than clamping
    /// again here.
    public Task<bool> SetValueDirectAsync(int target) => ApplyRawAsync(target.ToString());

    public RelayCommand UpCommand { get; }
    public RelayCommand DownCommand { get; }

    public IntSettingViewModel(BridgeClient bridge, string label, string bridgeName, int min, int max, int step, Func<bool> canEdit, Action<string> onError)
    {
        _bridge = bridge;
        Label = label;
        _bridgeName = bridgeName;
        _min = min;
        _max = max;
        _onError = onError;
        UpCommand = new RelayCommand(async () => await AdjustAsync(step), canEdit);
        DownCommand = new RelayCommand(async () => await AdjustAsync(-step), canEdit);
    }

    public async Task RefreshAsync()
    {
        try
        {
            var reply = await _bridge.SendAsync($"GET {_bridgeName}");
            if (reply.StartsWith("ERR")) { _onError(reply); return; }
            var parts = reply.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0] == _bridgeName && int.TryParse(parts[1], out var v)) Value = v;
        }
        catch (Exception ex)
        {
            _onError(ex.Message);
        }
    }

    private async Task AdjustAsync(int delta)
    {
        var target = Math.Clamp(Value + delta, _min, _max);
        try
        {
            var reply = await _bridge.SendAsync($"SET {_bridgeName} {target}");
            if (reply == "OK") Value = target;
            else _onError(reply);
        }
        catch (Exception ex)
        {
            _onError(ex.Message);
        }
    }

    // MARK: - IBridgeSetting (Presets capture/replay — see MainViewModel's
    // own Presets region)

    public async Task<string?> CaptureRawAsync()
    {
        try
        {
            var reply = await _bridge.SendAsync($"GET {_bridgeName}");
            var parts = reply.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 2 && parts[0] == _bridgeName ? parts[1] : null;
        }
        catch (Exception ex)
        {
            _onError(ex.Message);
            return null;
        }
    }

    public async Task<bool> ApplyRawAsync(string raw)
    {
        if (!int.TryParse(raw, out var target)) return false;
        try
        {
            var reply = await _bridge.SendAsync($"SET {_bridgeName} {target}");
            if (reply == "OK") { Value = target; return true; }
            _onError(reply);
            return false;
        }
        catch (Exception ex)
        {
            _onError(ex.Message);
            return false;
        }
    }
}

/// A single on/off setting synced via "GET <name>" / "SET <name> 0|1".
public sealed class ToggleSettingViewModel : ObservableObject, IBridgeSetting
{
    private readonly BridgeClient _bridge;
    private readonly string _bridgeName;
    private readonly Action<string> _onError;

    public string Label { get; }
    public string BridgeName => _bridgeName;

    private bool _isOn;
    public bool IsOn { get => _isOn; private set => SetProperty(ref _isOn, value); }

    public RelayCommand ToggleCommand { get; }

    public ToggleSettingViewModel(BridgeClient bridge, string label, string bridgeName, Func<bool> canEdit, Action<string> onError)
    {
        _bridge = bridge;
        Label = label;
        _bridgeName = bridgeName;
        _onError = onError;
        ToggleCommand = new RelayCommand(async () => await ToggleAsync(), canEdit);
    }

    public async Task RefreshAsync()
    {
        try
        {
            var reply = await _bridge.SendAsync($"GET {_bridgeName}");
            var parts = reply.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0] == _bridgeName) IsOn = parts[1] == "1";
        }
        catch (Exception ex)
        {
            _onError(ex.Message);
        }
    }

    private async Task ToggleAsync()
    {
        var target = !IsOn;
        try
        {
            var reply = await _bridge.SendAsync($"SET {_bridgeName} {(target ? 1 : 0)}");
            if (reply == "OK") IsOn = target;
            else _onError(reply);
        }
        catch (Exception ex)
        {
            _onError(ex.Message);
        }
    }

    // MARK: - IBridgeSetting (Presets capture/replay)

    public async Task<string?> CaptureRawAsync()
    {
        try
        {
            var reply = await _bridge.SendAsync($"GET {_bridgeName}");
            var parts = reply.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 2 && parts[0] == _bridgeName ? parts[1] : null;
        }
        catch (Exception ex)
        {
            _onError(ex.Message);
            return null;
        }
    }

    public async Task<bool> ApplyRawAsync(string raw)
    {
        var target = raw == "1";
        try
        {
            var reply = await _bridge.SendAsync($"SET {_bridgeName} {(target ? 1 : 0)}");
            if (reply == "OK") { IsOn = target; return true; }
            _onError(reply);
            return false;
        }
        catch (Exception ex)
        {
            _onError(ex.Message);
            return false;
        }
    }
}

/// One option within a ChoiceSettingViewModel<T> — a button showing
/// `DisplayName`, highlighted when it's the current selection.
public sealed class ChoiceOptionViewModel<T> : ObservableObject
{
    public T Value { get; }
    public string DisplayName { get; }
    public RelayCommand SelectCommand { get; }

    private bool _isActive;
    public bool IsActive { get => _isActive; set => SetProperty(ref _isActive, value); }

    public ChoiceOptionViewModel(T value, string displayName, Action<T> onSelect, Func<bool> canEdit)
    {
        Value = value;
        DisplayName = displayName;
        SelectCommand = new RelayCommand(() => onSelect(value), canEdit);
    }
}

/// A multi-option choice setting (e.g. AGC OFF/AUTO/FAST/MID/SLOW) synced
/// via "GET <name>" / "SET <name> <rawValue>". `toWire`/`fromWire` convert
/// between T and the wire representation the bridge expects/returns.
public sealed class ChoiceSettingViewModel<T> : ObservableObject, IBridgeSetting where T : notnull
{
    private readonly BridgeClient _bridge;
    private readonly string _bridgeName;
    private readonly Func<T, string> _toWire;
    public string BridgeName => _bridgeName;
    // A "TryParse"-style (bool ok, T value) pair rather than a nullable T?
    // return: T? on an unconstrained-beyond-notnull generic parameter is a
    // real gotcha for value types (Nullable<int> doesn't implicitly narrow
    // to int even after a null check) — this sidesteps it entirely and
    // works the same way for both value and reference types.
    private readonly Func<string, (bool ok, T value)> _fromWire;
    private readonly Action<string> _onError;

    public string Label { get; }
    public ObservableCollection<ChoiceOptionViewModel<T>> Options { get; }

    public ChoiceSettingViewModel(
        BridgeClient bridge, string label, string bridgeName,
        IEnumerable<(T value, string displayName)> options,
        Func<T, string> toWire, Func<string, (bool ok, T value)> fromWire,
        Func<bool> canEdit, Action<string> onError)
    {
        _bridge = bridge;
        Label = label;
        _bridgeName = bridgeName;
        _toWire = toWire;
        _fromWire = fromWire;
        _onError = onError;
        Options = new ObservableCollection<ChoiceOptionViewModel<T>>(
            options.Select(o => new ChoiceOptionViewModel<T>(o.value, o.displayName, v => _ = SelectAsync(v), canEdit)));
    }

    public async Task RefreshAsync()
    {
        try
        {
            var reply = await _bridge.SendAsync($"GET {_bridgeName}");
            var parts = reply.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0] == _bridgeName)
            {
                var (ok, value) = _fromWire(parts[1]);
                if (ok) UpdateHighlight(value);
            }
        }
        catch (Exception ex)
        {
            _onError(ex.Message);
        }
    }

    private async Task SelectAsync(T value)
    {
        try
        {
            var reply = await _bridge.SendAsync($"SET {_bridgeName} {_toWire(value)}");
            if (reply == "OK") UpdateHighlight(value);
            else _onError(reply);
        }
        catch (Exception ex)
        {
            _onError(ex.Message);
        }
    }

    private void UpdateHighlight(T value)
    {
        foreach (var o in Options) o.IsActive = EqualityComparer<T>.Default.Equals(o.Value, value);
        OnPropertyChanged(nameof(CurrentDisplayName));
    }

    /// The currently-selected option's display name — what the FUNC-grid
    /// choice cell (FuncChoiceCellTemplate) shows as its "value" line.
    public string? CurrentDisplayName => Options.FirstOrDefault(o => o.IsActive)?.DisplayName;

    // MARK: - IBridgeSetting (Presets capture/replay)

    public async Task<string?> CaptureRawAsync()
    {
        try
        {
            var reply = await _bridge.SendAsync($"GET {_bridgeName}");
            var parts = reply.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && parts[0] == _bridgeName ? parts[1] : null;
        }
        catch (Exception ex)
        {
            _onError(ex.Message);
            return null;
        }
    }

    public async Task<bool> ApplyRawAsync(string raw)
    {
        var (ok, value) = _fromWire(raw);
        if (!ok) return false;
        try
        {
            var reply = await _bridge.SendAsync($"SET {_bridgeName} {_toWire(value)}");
            if (reply == "OK") { UpdateHighlight(value); return true; }
            _onError(reply);
            return false;
        }
        catch (Exception ex)
        {
            _onError(ex.Message);
            return false;
        }
    }
}
