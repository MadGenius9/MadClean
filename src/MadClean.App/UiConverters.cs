using System.Globalization;
using System.Windows.Data;

namespace MadClean.App;

/// <summary>Presentation-only breakpoints; no provider or data-state changes.</summary>
public sealed class DashboardColumnsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double width && double.IsFinite(width) ? width >= 900 ? 3 : width >= 560 ? 2 : 1 : 1;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
