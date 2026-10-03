using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace SessionPerfTracker.App.Controls;

/// <summary>
/// Minimal line chart for live KPI tiles. Draws one frozen StreamGeometry per update;
/// no layout children, templates, animations or per-point visuals.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values),
        typeof(IReadOnlyList<double>),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum),
        typeof(double),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke),
        typeof(Brush),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill),
        typeof(Brush),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
        nameof(Capacity),
        typeof(int),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(60, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>Fixed top of the scale (e.g. 100 for percentages); 0 means auto-scale.</summary>
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>Number of points the full width represents, so the line scrolls in from the right.</summary>
    public int Capacity
    {
        get => (int)GetValue(CapacityProperty);
        set => SetValue(CapacityProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var values = Values;
        var width = ActualWidth;
        var height = ActualHeight;
        if (values is null || values.Count < 2 || width <= 2 || height <= 2)
        {
            return;
        }

        var max = Maximum > 0 ? Maximum : 0;
        if (max <= 0)
        {
            foreach (var value in values)
            {
                if (double.IsFinite(value) && value > max)
                {
                    max = value;
                }
            }

            max = max <= 0 ? 1 : max * 1.15;
        }

        var capacity = Math.Max(values.Count, Math.Max(2, Capacity));
        var step = width / (capacity - 1);
        var startX = width - step * (values.Count - 1);
        var line = new StreamGeometry();
        using (var context = line.Open())
        {
            for (var index = 0; index < values.Count; index++)
            {
                var value = double.IsFinite(values[index]) ? values[index] : 0;
                var point = new Point(
                    startX + step * index,
                    height - 1 - Math.Clamp(value / max, 0, 1) * (height - 2));
                if (index == 0)
                {
                    context.BeginFigure(point, isFilled: false, isClosed: false);
                }
                else
                {
                    context.LineTo(point, isStroked: true, isSmoothJoin: false);
                }
            }
        }

        line.Freeze();
        if (Fill is { } fill)
        {
            var area = new StreamGeometry();
            using (var context = area.Open())
            {
                context.BeginFigure(new Point(startX, height), isFilled: true, isClosed: true);
                for (var index = 0; index < values.Count; index++)
                {
                    var value = double.IsFinite(values[index]) ? values[index] : 0;
                    context.LineTo(
                        new Point(startX + step * index, height - 1 - Math.Clamp(value / max, 0, 1) * (height - 2)),
                        isStroked: false,
                        isSmoothJoin: false);
                }

                context.LineTo(new Point(width, height), isStroked: false, isSmoothJoin: false);
            }

            area.Freeze();
            drawingContext.DrawGeometry(fill, null, area);
        }

        var pen = new Pen(Stroke ?? Brushes.SteelBlue, 1.5);
        pen.Freeze();
        drawingContext.DrawGeometry(null, pen, line);
    }
}
