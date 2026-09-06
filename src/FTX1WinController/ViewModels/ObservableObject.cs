using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FTX1WinController.ViewModels;

/// Minimal hand-written INotifyPropertyChanged base — deliberately not
/// CommunityToolkit.Mvvm's source-generator attributes. A source-generator
/// failure is one extra layer of indirection to debug without an AI in the
/// loop; a plain property with an explicit backing field points a compiler
/// error at exactly the right line instead.
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
