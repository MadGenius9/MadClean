using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace MadClean.App;

/// <summary>Decorative font glyph; the containing control supplies the accessible text/name.</summary>
public sealed class SymbolIcon : FrameworkElement
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(SymbolIcon), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(SymbolIcon), new FrameworkPropertyMetadata(Brushes.LightSteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(nameof(FontSize), typeof(double), typeof(SymbolIcon), new FrameworkPropertyMetadata(18d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public SymbolIcon() => IsHitTestVisible = false;
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    protected override Size MeasureOverride(Size availableSize) => new(ValidSize + 4, ValidSize + 4);
    private double ValidSize => double.IsFinite(FontSize) && FontSize > 0 ? Math.Min(FontSize, 128) : 18;
    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (string.IsNullOrEmpty(Glyph) || Foreground == null || ActualWidth <= 0 || ActualHeight <= 0) return;
        var text = new FormattedText(Glyph, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe MDL2 Assets"), ValidSize, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawingContext.DrawText(text, new Point((ActualWidth - text.Width) / 2, (ActualHeight - text.Height) / 2));
    }
}
