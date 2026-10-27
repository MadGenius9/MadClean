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
    // Original outline geometry keeps the reference's meaning and weight stable across
    // Windows font versions. Remaining decorative glyphs retain the native font fallback.
    private static readonly IReadOnlyDictionary<string, Geometry> Outlines = new Dictionary<string, Geometry>
    {
        ["\uE80F"] = Shape("M4,14 L16,4 L28,14 M7,12 L7,28 L13,28 L13,20 L19,20 L19,28 L25,28 L25,12"),
        ["\uE8B7"] = Shape("M7,9 L8,28 L24,28 L25,9 M5,8 L27,8 M11,8 L11,4 L21,4 L21,8 M13,12 L13,24 M19,12 L19,24"),
        ["\uE83D"] = Shape("M16,3 C20,6 24,7 28,7 L27,18 C26,24 22,28 16,30 C10,28 6,24 5,18 L4,7 C8,7 12,6 16,3 Z"),
        ["\uE768"] = Shape("M7,3 L29,16 L7,29 Z"),
        ["\uE895"] = Shape("M27,10 C24,3 14,1 7,6 C5,8 4,10 4,14 M27,3 L27,10 L20,10 M5,22 C8,29 18,31 25,26 C27,24 28,22 28,18 M5,29 L5,22 L12,22"),
        ["\uE7F1"] = Shape("M7,4 L25,4 L29,22 L29,28 L3,28 L3,22 Z M3,21 L29,21 M22,25 L24,25"),
        ["\uE721"] = Shape("M23,23 L30,30 M25,14 A11,11 0 1 1 3,14 A11,11 0 1 1 25,14"),
        ["\uE72A"] = Shape("M4,16 L28,16 M20,8 L28,16 L20,24")
    };
    private static Geometry Shape(string path)
    {
        var geometry = Geometry.Parse(path); geometry.Freeze(); return geometry;
    }
    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (string.IsNullOrEmpty(Glyph) || Foreground == null || ActualWidth <= 0 || ActualHeight <= 0) return;
        if (Outlines.TryGetValue(Glyph, out var outline))
        {
            double scale = ValidSize / 32;
            var pen = new Pen(Foreground, 1.9) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            drawingContext.PushTransform(new MatrixTransform(scale, 0, 0, scale, (ActualWidth - ValidSize) / 2, (ActualHeight - ValidSize) / 2));
            drawingContext.DrawGeometry(null, pen, outline);
            drawingContext.Pop();
            return;
        }
        var text = new FormattedText(Glyph, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe MDL2 Assets"), ValidSize, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawingContext.DrawText(text, new Point((ActualWidth - text.Width) / 2, (ActualHeight - text.Height) / 2));
    }
}
