using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using FontFamily = System.Windows.Media.FontFamily;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace SessionPerfTracker.App.Controls;

/// <summary>
/// Live performance timeline: CPU/GPU/RAM share the left 0-100 % axis, disk throughput uses the
/// right axis. Events are drawn as markers; hovering shows the exact values at that moment.
/// Rendering reads at most one point per pixel from <see cref="TimelineHistory"/>.
/// </summary>
public sealed class TimelineChart : FrameworkElement
{
    public static readonly DependencyProperty HistoryProperty = Register(nameof(History), typeof(TimelineHistory), null);
    public static readonly DependencyProperty VersionProperty = Register(nameof(Version), typeof(int), 0);
    public static readonly DependencyProperty WindowSecondsProperty = Register(nameof(WindowSeconds), typeof(double), 600d);
    public static readonly DependencyProperty EmptyTextProperty = Register(nameof(EmptyText), typeof(string), string.Empty);
    public static readonly DependencyProperty CpuLabelProperty = Register(nameof(CpuLabel), typeof(string), "CPU");
    public static readonly DependencyProperty GpuLabelProperty = Register(nameof(GpuLabel), typeof(string), "GPU");
    public static readonly DependencyProperty RamLabelProperty = Register(nameof(RamLabel), typeof(string), "RAM");
    public static readonly DependencyProperty DiskLabelProperty = Register(nameof(DiskLabel), typeof(string), "Disk");

    private const double LeftMargin = 34;
    private const double RightMargin = 46;
    private const double TopMargin = 20;
    private const double BottomMargin = 22;
    private static readonly Typeface LabelFace = new("Segoe UI");
    private static readonly Typeface StrongFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private Point? _hover;

    public TimelineChart()
    {
        ClipToBounds = true;
        MinHeight = 120;
    }

    public TimelineHistory? History
    {
        get => (TimelineHistory?)GetValue(HistoryProperty);
        set => SetValue(HistoryProperty, value);
    }

    /// <summary>Bump to redraw after new samples were appended.</summary>
    public int Version
    {
        get => (int)GetValue(VersionProperty);
        set => SetValue(VersionProperty, value);
    }

    /// <summary>Visible range in seconds; 0 shows the whole history.</summary>
    public double WindowSeconds
    {
        get => (double)GetValue(WindowSecondsProperty);
        set => SetValue(WindowSecondsProperty, value);
    }

    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    public string CpuLabel { get => (string)GetValue(CpuLabelProperty); set => SetValue(CpuLabelProperty, value); }
    public string GpuLabel { get => (string)GetValue(GpuLabelProperty); set => SetValue(GpuLabelProperty, value); }
    public string RamLabel { get => (string)GetValue(RamLabelProperty); set => SetValue(RamLabelProperty, value); }
    public string DiskLabel { get => (string)GetValue(DiskLabelProperty); set => SetValue(DiskLabelProperty, value); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _hover = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        // Transparent fill keeps mouse events over empty areas.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var plot = new Rect(
            LeftMargin,
            TopMargin,
            Math.Max(10, ActualWidth - LeftMargin - RightMargin),
            Math.Max(10, ActualHeight - TopMargin - BottomMargin));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var muted = Find("MutedBrush", Brushes.Gray);
        var gridPen = Frozen(new Pen(Find("BorderBrushSoft", Brushes.DimGray), 1));

        // Left axis 0..100 %.
        for (var step = 0; step <= 5; step++)
        {
            var y = plot.Bottom - plot.Height * step / 5;
            dc.DrawLine(gridPen, new Point(plot.Left, Snap(y)), new Point(plot.Right, Snap(y)));
            DrawText(dc, (step * 20).ToString(CultureInfo.CurrentCulture), new Point(plot.Left - 6, y), muted, dpi, alignRight: true);
        }

        var window = History?.GetWindow(WindowSeconds, Math.Max(60, (int)plot.Width));
        if (window is null || window.Points.Count < 2)
        {
            if (!string.IsNullOrWhiteSpace(EmptyText))
            {
                DrawText(dc, EmptyText, new Point(plot.Left + plot.Width / 2, plot.Top + plot.Height / 2), muted, dpi, center: true);
            }

            return;
        }

        var span = Math.Max(1, window.EndSeconds - window.StartSeconds);
        double X(double seconds) => plot.Left + (seconds - window.StartSeconds) / span * plot.Width;

        // Right axis: disk MB/s with a rounded maximum.
        var diskPeak = window.Points.Select(point => point.Disk).Where(double.IsFinite).DefaultIfEmpty(0).Max();
        var diskMax = NiceCeiling(Math.Max(1, diskPeak));
        for (var step = 0; step <= 5; step += 5)
        {
            var y = plot.Bottom - plot.Height * step / 5;
            DrawText(dc, $"{diskMax * step / 5:0.#}", new Point(plot.Right + 6, y), muted, dpi);
        }

        DrawText(dc, "MB/s", new Point(plot.Right + 6, plot.Top + plot.Height / 2), muted, dpi);
        DrawTimeAxis(dc, plot, window, span, muted, dpi);

        double PercentY(double value) => plot.Bottom - Math.Clamp(value, 0, 100) / 100 * plot.Height;
        double DiskY(double value) => plot.Bottom - Math.Clamp(value / diskMax, 0, 1) * plot.Height;

        var diskBrush = Find("DiskAccentBrush", Brushes.LimeGreen);
        var ramBrush = Find("RamAccentBrush", Brushes.Cyan);
        var cpuBrush = Find("CpuAccentBrush", Brushes.DodgerBlue);
        var gpuBrush = Find("GpuAccentBrush", Brushes.MediumPurple);

        DrawSeries(dc, window.Points, point => point.Disk, X, DiskY, diskBrush, plot, fill: false, thickness: 1.2);
        DrawSeries(dc, window.Points, point => point.RamPercent, X, PercentY, ramBrush, plot, fill: false, thickness: 1.6);
        DrawSeries(dc, window.Points, point => point.Cpu, X, PercentY, cpuBrush, plot, fill: true, thickness: 1.6);
        DrawSeries(dc, window.Points, point => point.Gpu, X, PercentY, gpuBrush, plot, fill: true, thickness: 1.6);

        DrawMarkers(dc, window.Markers, X, plot, dpi);
        DrawHover(dc, window, plot, span, dpi, cpuBrush, gpuBrush, ramBrush, diskBrush);
    }

    private void DrawTimeAxis(DrawingContext dc, Rect plot, TimelineWindow window, double span, Brush muted, double dpi)
    {
        var steps = new[] { 5d, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 14400 };
        var targetTicks = Math.Max(2, plot.Width / 110);
        var tick = steps.FirstOrDefault(step => span / step <= targetTicks, steps[^1]);
        var format = tick < 60 || span <= 180 ? "HH:mm:ss" : "HH:mm";
        var first = Math.Ceiling(window.StartSeconds / tick) * tick;
        for (var seconds = first; seconds <= window.EndSeconds; seconds += tick)
        {
            var x = plot.Left + (seconds - window.StartSeconds) / span * plot.Width;
            var label = window.Origin.AddSeconds(seconds).ToLocalTime().ToString(format, CultureInfo.CurrentCulture);
            DrawText(dc, label, new Point(x, plot.Bottom + 11), muted, dpi, center: true);
        }
    }

    private static void DrawSeries(
        DrawingContext dc,
        IReadOnlyList<TimelinePoint> points,
        Func<TimelinePoint, double> value,
        Func<double, double> x,
        Func<double, double> y,
        Brush brush,
        Rect plot,
        bool fill,
        double thickness)
    {
        var line = new StreamGeometry();
        var area = fill ? new StreamGeometry() : null;
        var any = false;
        using (var lineContext = line.Open())
        using (var areaContext = area?.Open())
        {
            var open = false;
            var segmentStartX = 0d;
            var lastX = 0d;
            foreach (var point in points)
            {
                var number = value(point);
                if (!double.IsFinite(number))
                {
                    if (open && areaContext is not null)
                    {
                        areaContext.LineTo(new Point(lastX, plot.Bottom), false, false);
                        areaContext.LineTo(new Point(segmentStartX, plot.Bottom), false, false);
                    }

                    open = false;
                    continue;
                }

                var position = new Point(x(point.Seconds), y(number));
                if (!open)
                {
                    lineContext.BeginFigure(position, false, false);
                    areaContext?.BeginFigure(new Point(position.X, plot.Bottom), true, true);
                    areaContext?.LineTo(position, false, false);
                    segmentStartX = position.X;
                    open = true;
                }
                else
                {
                    lineContext.LineTo(position, true, true);
                    areaContext?.LineTo(position, false, false);
                }

                lastX = position.X;
                any = true;
            }

            if (open && areaContext is not null)
            {
                areaContext.LineTo(new Point(lastX, plot.Bottom), false, false);
                areaContext.LineTo(new Point(segmentStartX, plot.Bottom), false, false);
            }
        }

        if (!any)
        {
            return;
        }

        line.Freeze();
        if (area is not null && brush is SolidColorBrush solid)
        {
            area.Freeze();
            var gradient = new LinearGradientBrush(
                Color.FromArgb(70, solid.Color.R, solid.Color.G, solid.Color.B),
                Color.FromArgb(0, solid.Color.R, solid.Color.G, solid.Color.B),
                90);
            gradient.Freeze();
            dc.DrawGeometry(gradient, null, area);
        }

        dc.DrawGeometry(null, Frozen(new Pen(brush, thickness) { LineJoin = PenLineJoin.Round }), line);
    }

    private void DrawMarkers(DrawingContext dc, IReadOnlyList<TimelineMarker> markers, Func<double, double> x, Rect plot, double dpi)
    {
        if (markers.Count == 0)
        {
            return;
        }

        var lastX = double.NegativeInfinity;
        foreach (var marker in markers.OrderBy(item => item.Seconds))
        {
            var position = x(marker.Seconds);
            if (position < plot.Left || position > plot.Right)
            {
                continue;
            }

            var brush = marker.Severity switch
            {
                2 => Find("BadBrush", Brushes.Red),
                1 => Find("WarnBrush", Brushes.Orange),
                _ => Find("AccentBrush", Brushes.DodgerBlue)
            };
            var pen = Frozen(new Pen(brush, 1) { DashStyle = DashStyles.Dash });
            dc.DrawLine(pen, new Point(Snap(position), plot.Top), new Point(Snap(position), plot.Bottom));

            // Neighbouring markers share one badge so dense event bursts stay readable.
            if (position - lastX < 14)
            {
                continue;
            }

            lastX = position;
            dc.DrawEllipse(brush, null, new Point(position, plot.Top - 9), 7, 7);
            DrawText(dc, marker.Severity == 0 ? "i" : "!", new Point(position, plot.Top - 9), Brushes.White, dpi, center: true, bold: true, size: 10);
        }
    }

    private void DrawHover(
        DrawingContext dc,
        TimelineWindow window,
        Rect plot,
        double span,
        double dpi,
        Brush cpu,
        Brush gpu,
        Brush ram,
        Brush disk)
    {
        if (_hover is not { } hover || hover.X < plot.Left || hover.X > plot.Right)
        {
            return;
        }

        var seconds = window.StartSeconds + (hover.X - plot.Left) / plot.Width * span;
        var nearest = window.Points.MinBy(point => Math.Abs(point.Seconds - seconds));
        var x = plot.Left + (nearest.Seconds - window.StartSeconds) / span * plot.Width;
        dc.DrawLine(Frozen(new Pen(Find("MutedBrush", Brushes.Gray), 1)), new Point(Snap(x), plot.Top), new Point(Snap(x), plot.Bottom));

        var lines = new List<(string Text, Brush? Dot)>
        {
            (window.Origin.AddSeconds(nearest.Seconds).ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture), null),
            ($"{CpuLabel}: {Format(nearest.Cpu, "0.0'%'")}", cpu),
            ($"{GpuLabel}: {Format(nearest.Gpu, "0.0'%'")}", gpu),
            ($"{RamLabel}: {FormatMemory(nearest.RamMb)} ({Format(nearest.RamPercent, "0.0'%'")})", ram),
            ($"{DiskLabel}: {Format(nearest.Disk, "0.0' MB/s'")}", disk)
        };
        var texts = lines.Select(line => Text(line.Text, Find("TextBrush", Brushes.White), dpi, 11, bold: line.Dot is null)).ToArray();
        var width = texts.Max(text => text.Width) + 30;
        var height = texts.Sum(text => text.Height + 2) + 12;
        var left = x + 12 + width > plot.Right ? x - 12 - width : x + 12;
        var box = new Rect(left, plot.Top + 6, width, height);
        dc.DrawRoundedRectangle(Find("PanelStrongBrush", Brushes.Black), Frozen(new Pen(Find("BorderBrushSoft", Brushes.Gray), 1)), box, 6, 6);
        var top = box.Top + 6;
        for (var index = 0; index < texts.Length; index++)
        {
            var dot = lines[index].Dot;
            if (dot is not null)
            {
                dc.DrawEllipse(dot, null, new Point(box.Left + 12, top + texts[index].Height / 2), 3.5, 3.5);
            }

            dc.DrawText(texts[index], new Point(box.Left + (dot is null ? 10 : 20), top));
            top += texts[index].Height + 2;
        }
    }

    private static string Format(double value, string format) =>
        double.IsFinite(value) ? value.ToString(format, CultureInfo.CurrentCulture) : "—";

    private static string FormatMemory(double megabytes) =>
        !double.IsFinite(megabytes) ? "—" : megabytes >= 1024 ? $"{megabytes / 1024:0.00} GB" : $"{megabytes:0} MB";

    private static double NiceCeiling(double value)
    {
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (var factor in new[] { 1d, 2, 2.5, 5, 10 })
        {
            if (factor * magnitude >= value)
            {
                return factor * magnitude;
            }
        }

        return 10 * magnitude;
    }

    private void DrawText(
        DrawingContext dc,
        string text,
        Point anchor,
        Brush brush,
        double dpi,
        bool alignRight = false,
        bool center = false,
        bool bold = false,
        double size = 11)
    {
        var formatted = Text(text, brush, dpi, size, bold);
        var x = alignRight ? anchor.X - formatted.Width : center ? anchor.X - formatted.Width / 2 : anchor.X;
        dc.DrawText(formatted, new Point(x, anchor.Y - formatted.Height / 2));
    }

    private static FormattedText Text(string text, Brush brush, double dpi, double size, bool bold = false) =>
        new(text, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, bold ? StrongFace : LabelFace, size, brush, dpi);

    private Brush Find(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private static Pen Frozen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }

    private static double Snap(double value) => Math.Round(value) + 0.5;

    private static DependencyProperty Register(string name, Type type, object? defaultValue) =>
        DependencyProperty.Register(name, type, typeof(TimelineChart),
            new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));
}
