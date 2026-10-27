using System.Windows;
using System.Windows.Media;

namespace MadClean.App;

/// <summary>A visual-only ring for a known free-space fraction; the parent supplies accessible text and scope.</summary>
public sealed class StorageRing : FrameworkElement
{
    public static readonly DependencyProperty FreeFractionProperty = DependencyProperty.Register(
        nameof(FreeFraction), typeof(double), typeof(StorageRing),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush BaseBrush = CreateBaseBrush();
    private static readonly Color ArcStart = Color.FromRgb(0x65, 0xCD, 0xFF);
    private static readonly Color ArcEnd = Color.FromRgb(0x16, 0x8A, 0xFF);

    public StorageRing() => IsHitTestVisible = false;

    public double FreeFraction
    {
        get => (double)GetValue(FreeFractionProperty);
        set => SetValue(FreeFractionProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        double width = ActualWidth, height = ActualHeight;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return;

        double diameter = Math.Min(width, height);
        double thickness = Math.Min(24, diameter * 0.08);
        double radius = (diameter - thickness) / 2;
        if (thickness <= 0 || radius <= 0) return;
        var center = new Point(width / 2, height / 2);
        var basePen = new Pen(BaseBrush, thickness);
        basePen.Freeze();
        drawingContext.DrawEllipse(null, basePen, center, radius, radius);

        double fraction = FreeFraction;
        // Do not coerce invalid evidence into an apparently measured empty or full segment.
        if (!double.IsFinite(fraction) || fraction < 0 || fraction > 1 || fraction == 0) return;

        var brush = new LinearGradientBrush(ArcStart, ArcEnd,
            new Point(center.X - radius, center.Y - radius),
            new Point(center.X + radius, center.Y + radius))
        {
            MappingMode = BrushMappingMode.Absolute
        };
        brush.Freeze();
        var arcPen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        arcPen.Freeze();
        if (fraction == 1)
        {
            drawingContext.DrawEllipse(null, arcPen, center, radius, radius);
            return;
        }

        // WPF's y-axis points down: a quarter turn runs from the top to the right.
        double angle = fraction * Math.Tau;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, isFilled: false, isClosed: false);
            context.ArcTo(end, new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise,
                isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        drawingContext.DrawGeometry(null, arcPen, geometry);
    }

    private static Brush CreateBaseBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x26, 0x3E, 0x54));
        brush.Freeze();
        return brush;
    }
}
