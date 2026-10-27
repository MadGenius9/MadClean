using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MadClean.App;

/// <summary>Presentation-only breakpoints; no provider or data-state changes.</summary>
public sealed class CompactOverviewConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double width && double.IsFinite(width) && width < 1160;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class OverviewSideWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new GridLength(value is double width && double.IsFinite(width) && width >= 1160 ? 440 : 0);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
