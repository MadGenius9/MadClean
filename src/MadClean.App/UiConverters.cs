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

public sealed class ShortcutStateTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        MadClean.Core.ShortcutTargetState.TargetExists => "Local target exists",
        MadClean.Core.ShortcutTargetState.LocalTargetMissing => "Local target missing · review",
        MadClean.Core.ShortcutTargetState.Unsupported => "Unsupported target context",
        MadClean.Core.ShortcutTargetState.UnresolvedMetadata => "Unresolved metadata",
        _ => "Unknown / not verified"
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
