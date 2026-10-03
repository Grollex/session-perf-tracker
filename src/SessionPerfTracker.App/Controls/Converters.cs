using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Binding = System.Windows.Data.Binding;

namespace SessionPerfTracker.App.Controls;

/// <summary>
/// Tints a table cell by load: CPU/GPU red, RAM blue, disk green. Brushes are bucketed and frozen,
/// so scrolling a large table allocates nothing.
/// </summary>
public sealed class HeatBrushConverter : IValueConverter
{
    private const int Levels = 8;
    private static readonly Dictionary<string, Brush[]> Palettes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cpu"] = CreatePalette(Color.FromRgb(0xEF, 0x44, 0x44)),
        ["gpu"] = CreatePalette(Color.FromRgb(0xC0, 0x3A, 0x5C)),
        ["ram"] = CreatePalette(Color.FromRgb(0x2F, 0x6F, 0xE0)),
        ["disk"] = CreatePalette(Color.FromRgb(0x16, 0xA3, 0x4A))
    };

    // Value that paints the strongest tint for each metric.
    private static readonly Dictionary<string, double> FullScale = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cpu"] = 40,
        ["gpu"] = 60,
        ["ram"] = 3072,
        ["disk"] = 25
    };

    // Below this the cell stays plain so idle processes do not paint the whole table.
    private static readonly Dictionary<string, double> Floor = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cpu"] = 1,
        ["gpu"] = 2,
        ["ram"] = 200,
        ["disk"] = 0.5
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var kind = parameter as string ?? "cpu";
        if (value is not double number || !double.IsFinite(number)
            || !Palettes.TryGetValue(kind, out var palette)
            || number < Floor[kind])
        {
            return Brushes.Transparent;
        }

        var ratio = Math.Clamp(number / FullScale[kind], 0, 1);
        return palette[(int)Math.Round(ratio * (Levels - 1))];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;

    private static Brush[] CreatePalette(Color color)
    {
        var brushes = new Brush[Levels];
        for (var index = 0; index < Levels; index++)
        {
            var alpha = (byte)(40 + index * 22);
            var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
            brush.Freeze();
            brushes[index] = brush;
        }

        return brushes;
    }
}

/// <summary>ItemsControl.AlternationIndex (0-based) to a 1-based row number.</summary>
public sealed class RowNumberConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int index ? (index + 1).ToString(culture) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Empty or whitespace text collapses the element.</summary>
public sealed class TextToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
