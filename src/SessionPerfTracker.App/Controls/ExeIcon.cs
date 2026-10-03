using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using Image = System.Windows.Controls.Image;

namespace SessionPerfTracker.App.Controls;

/// <summary>
/// Shows the shell icon of an executable. Icons are extracted once per path on a single
/// low-priority background thread and shared by every row that shows the same file.
/// </summary>
public sealed class ExeIcon : Image
{
    public static readonly DependencyProperty PathProperty = DependencyProperty.Register(
        nameof(Path), typeof(string), typeof(ExeIcon),
        new PropertyMetadata(null, OnPathChanged));

    private const int MaxCachedIcons = 1024;
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>());
    private static readonly Dictionary<string, List<ExeIcon>> Waiters = new(StringComparer.OrdinalIgnoreCase);
    private static int _workerStarted;

    static ExeIcon()
    {
        WidthProperty.OverrideMetadata(typeof(ExeIcon), new FrameworkPropertyMetadata(18d));
        HeightProperty.OverrideMetadata(typeof(ExeIcon), new FrameworkPropertyMetadata(18d));
        StretchProperty.OverrideMetadata(typeof(ExeIcon), new FrameworkPropertyMetadata(Stretch.Uniform));
    }

    public ExeIcon()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
    }

    public string? Path
    {
        get => (string?)GetValue(PathProperty);
        set => SetValue(PathProperty, value);
    }

    private static void OnPathChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var icon = (ExeIcon)sender;
        var path = args.NewValue as string;
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathRooted(path))
        {
            icon.Source = null;
            return;
        }

        if (Cache.TryGetValue(path, out var cached))
        {
            icon.Source = cached;
            return;
        }

        icon.Source = null;
        bool first;
        lock (Waiters)
        {
            first = !Waiters.TryGetValue(path, out var list);
            if (first)
            {
                list = [];
                Waiters[path] = list;
            }

            list!.Add(icon);
        }

        if (first)
        {
            Queue.Add(path);
            EnsureWorker();
        }
    }

    private static void EnsureWorker()
    {
        if (Interlocked.Exchange(ref _workerStarted, 1) != 0)
        {
            return;
        }

        var worker = new Thread(Work)
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "ExeIconLoader"
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    private static void Work()
    {
        foreach (var path in Queue.GetConsumingEnumerable())
        {
            if (Cache.Count >= MaxCachedIcons)
            {
                Cache.Clear();
            }

            var source = Load(path);
            Cache[path] = source;
            List<ExeIcon>? waiting;
            lock (Waiters)
            {
                Waiters.Remove(path, out waiting);
            }

            if (waiting is null || waiting.Count == 0 || source is null)
            {
                continue;
            }

            // One dispatch per file; recycled rows may already show another path.
            waiting[0].Dispatcher.BeginInvoke(() =>
            {
                foreach (var icon in waiting)
                {
                    if (string.Equals(icon.Path, path, StringComparison.OrdinalIgnoreCase))
                    {
                        icon.Source = source;
                    }
                }
            });
        }
    }

    private static ImageSource? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var icon = Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null)
            {
                return null;
            }

            var source = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(32, 32));
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }
}
