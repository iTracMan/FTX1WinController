using System.Collections.ObjectModel;

namespace FTX1WinController.ViewModels;

/// Common shape shared by Int/Toggle/ChoiceSettingViewModel so
/// MainViewModel's Presets feature can capture/replay any of them
/// uniformly — a raw wire-ish string round-tripped verbatim via the
/// delegates each was constructed with, no per-setting-type code needed at
/// the Presets call site. `Id` is just an opaque dictionary key for
/// PresetData.Values now (there's no bridge to name it after).
public interface ISettingBinding
{
    string Label { get; }
    string Id { get; }
    Task<string?> CaptureRawAsync();
    Task<bool> ApplyRawAsync(string raw);
}

/// A single numeric setting backed by a RadioController Set/Refresh method
/// pair (injected as delegates, so this class has no CAT knowledge of its
/// own), with a shared -/+ stepper pattern (bound to RepeatButton in XAML
/// for click-and-hold). Exists so ~15 lines of near-identical property/
/// command/refresh boilerplate isn't repeated by hand for every one of FUNC
/// Page 1's dozen-plus sliders — one implementation to get right and
/// review, instead of many near-duplicates.
public sealed class IntSettingViewModel : ObservableObject, ISettingBinding
{
    private readonly Func<int> _currentValue;
    private readonly Func<Task> _refresh;
    private readonly Func<int, Task> _set;
    private readonly int _min;
    private readonly int _max;
    private readonly Action<string> _onError;

    public string Label { get; }
    public string Id { get; }
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

    public IntSettingViewModel(
        string label, string id, int min, int max, int step,
        Func<int> currentValue, Func<Task> refresh, Func<int, Task> set,
        Func<bool> canEdit, Action<string> onError)
    {
        Label = label;
        Id = id;
        _min = min;
        _max = max;
        _currentValue = currentValue;
        _refresh = refresh;
        _set = set;
        _onError = onError;
        UpCommand = new RelayCommand(async () => await AdjustAsync(step), canEdit);
        DownCommand = new RelayCommand(async () => await AdjustAsync(-step), canEdit);
    }

    public async Task RefreshAsync()
    {
        try
        {
            await _refresh();
            Value = _currentValue();
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
            await _set(target);
            Value = _currentValue();
        }
        catch (Exception ex)
        {
            _onError(ex.Message);
        }
    }

    // MARK: - ISettingBinding (Presets capture/replay — see MainViewModel's
    // own Presets region)

    public async Task<string?> CaptureRawAsync()
    {
        try
        {
            await _refresh();
            return _currentValue().ToString();
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
            await _set(Math.Clamp(target, _min, _max));
            Value = _currentValue();
            return true;
        }
        catch (Exception ex)
        {
            _onError(ex.Message);
            return false;
        }
    }
}

/// A single on/off setting backed by a RadioController Set/Refresh method pair.
public sealed class ToggleSettingViewModel : ObservableObject, ISettingBinding
{
    private readonly Func<bool> _currentValue;
    private readonly Func<Task> _refresh;
    private readonly Func<bool, Task> _set;
    private readonly Action<string> _onError;

    public string Label { get; }
    public string Id { get; }

    private bool _isOn;
    public bool IsOn { get => _isOn; private set => SetProperty(ref _isOn, value); }

    public RelayCommand ToggleCommand { get; }

    public ToggleSettingViewModel(
        string label, string id,
        Func<bool> currentValue, Func<Task> refresh, Func<bool, Task> set,
        Func<bool> canEdit, Action<string> onError)
    {
        Label = label;
        Id = id;
        _currentValue = currentValue;
        _refresh = refresh;
        _set = set;
        _onError = onError;
        ToggleCommand = new RelayCommand(async () => await ToggleAsync(), canEdit);
    }

    public async Task RefreshAsync()
    {
        try
        {
            await _refresh();
            IsOn = _currentValue();
        }
        catch (Exception ex)
        {
            _onError(ex.Message);
        }
    }

    private async Task ToggleAsync()
    {
        try
        {
            await _set(!IsOn);
            IsOn = _currentValue();
        }
        catch (Exception ex)
        {
            _onError(ex.Message);
        }
    }

    // MARK: - ISettingBinding (Presets capture/replay)

    public async Task<string?> CaptureRawAsync()
    {
        try
        {
            await _refresh();
            return _currentValue() ? "1" : "0";
        }
        catch (Exception ex)
        {
            _onError(ex.Message);
            return null;
        }
    }

    public async Task<bool> ApplyRawAsync(string raw)
    {
        try
        {
            await _set(raw == "1");
            IsOn = _currentValue();
            return true;
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

/// A multi-option choice setting (e.g. AGC OFF/AUTO/FAST/MID/SLOW) backed by
/// a RadioController Set/Refresh method pair. `toWire`/`fromWire` convert
/// between T and the string Presets stores it as.
public sealed class ChoiceSettingViewModel<T> : ObservableObject, ISettingBinding where T : notnull
{
    private readonly Func<T> _currentValue;
    private readonly Func<Task> _refresh;
    private readonly Func<T, Task> _set;
    private readonly Func<T, string> _toWire;
    // A "TryParse"-style (bool ok, T value) pair rather than a nullable T?
    // return: T? on an unconstrained-beyond-notnull generic parameter is a
    // real gotcha for value types (Nullable<int> doesn't implicitly narrow
    // to int even after a null check) — this sidesteps it entirely and
    // works the same way for both value and reference types.
    private readonly Func<string, (bool ok, T value)> _fromWire;
    private readonly Action<string> _onError;

    public string Label { get; }
    public string Id { get; }
    public ObservableCollection<ChoiceOptionViewModel<T>> Options { get; }

    public ChoiceSettingViewModel(
        string label, string id,
        IEnumerable<(T value, string displayName)> options,
        Func<T> currentValue, Func<Task> refresh, Func<T, Task> set,
        Func<T, string> toWire, Func<string, (bool ok, T value)> fromWire,
        Func<bool> canEdit, Action<string> onError)
    {
        Label = label;
        Id = id;
        _currentValue = currentValue;
        _refresh = refresh;
        _set = set;
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
            await _refresh();
            UpdateHighlight(_currentValue());
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
            await _set(value);
            UpdateHighlight(_currentValue());
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

    // MARK: - ISettingBinding (Presets capture/replay)

    public async Task<string?> CaptureRawAsync()
    {
        try
        {
            await _refresh();
            return _toWire(_currentValue());
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
            await _set(value);
            UpdateHighlight(_currentValue());
            return true;
        }
        catch (Exception ex)
        {
            _onError(ex.Message);
            return false;
        }
    }
}
