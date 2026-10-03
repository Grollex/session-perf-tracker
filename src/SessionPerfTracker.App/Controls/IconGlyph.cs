using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;

namespace SessionPerfTracker.App.Controls;

/// <summary>
/// Draws a 24x24 outline icon geometry scaled to the element size. Cheaper than a Viewbox/Path pair
/// and keeps the stroke weight proportional, like icon fonts.
/// </summary>
public sealed class IconGlyph : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(IconGlyph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(
        nameof(Brush), typeof(Brush), typeof(IconGlyph),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeWeightProperty = DependencyProperty.Register(
        nameof(StrokeWeight), typeof(double), typeof(IconGlyph),
        new FrameworkPropertyMetadata(1.8, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsFilledProperty = DependencyProperty.Register(
        nameof(IsFilled), typeof(bool), typeof(IconGlyph),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    static IconGlyph()
    {
        WidthProperty.OverrideMetadata(typeof(IconGlyph), new FrameworkPropertyMetadata(18d));
        HeightProperty.OverrideMetadata(typeof(IconGlyph), new FrameworkPropertyMetadata(18d));
        SnapsToDevicePixelsProperty.OverrideMetadata(typeof(IconGlyph), new FrameworkPropertyMetadata(true));
    }

    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public Brush Brush
    {
        get => (Brush)GetValue(BrushProperty);
        set => SetValue(BrushProperty, value);
    }

    /// <summary>Stroke width in the 24-unit icon space.</summary>
    public double StrokeWeight
    {
        get => (double)GetValue(StrokeWeightProperty);
        set => SetValue(StrokeWeightProperty, value);
    }

    public bool IsFilled
    {
        get => (bool)GetValue(IsFilledProperty);
        set => SetValue(IsFilledProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var data = Data;
        if (data is null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var scale = Math.Min(ActualWidth, ActualHeight) / 24d;
        drawingContext.PushTransform(new TranslateTransform(
            (ActualWidth - 24 * scale) / 2,
            (ActualHeight - 24 * scale) / 2));
        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        var pen = new Pen(Brush, StrokeWeight)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        drawingContext.DrawGeometry(IsFilled ? Brush : null, IsFilled ? null : pen, data);
        drawingContext.Pop();
        drawingContext.Pop();
    }
}
