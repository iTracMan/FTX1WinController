using System.Windows.Input;

namespace FTX1WinController.ViewModels;

/// Minimal hand-written ICommand — no third-party MVVM package needed for
/// something this small.
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    /// WPF's CommandManager re-evaluates CanExecute automatically around
    /// most UI events (focus change, clicks elsewhere, etc.), but a
    /// property change driven purely from async code (e.g. ConnectionState
    /// flipping after a bridge reply) isn't one of them — call this right
    /// after such a change so button enabled-state updates immediately
    /// instead of on the next incidental UI event.
    public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
}
