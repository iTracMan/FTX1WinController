using System.Globalization;
using System.Windows.Data;
using FTX1WinController.ViewModels;

namespace FTX1WinController.Converters;

/// Formats a popup slider's LIVE (uncommitted) value using the bound
/// IntSettingViewModel's own Format function — used by
/// FuncSliderPopupCellTemplate so the value preview updates smoothly while
/// dragging, before the drag-release SET actually commits it (DisplayText
/// only reflects the last confirmed value).
public sealed class SliderPreviewConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 2 && values[0] is double raw && values[1] is IntSettingViewModel vm)
        {
            return vm.FormatValue(raw);
        }
        return values.Length > 0 ? values[0]?.ToString() ?? "" : "";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
