using SessionPerfTracker.App.Controls;

namespace SessionPerfTracker.UnitTests;

public sealed class TimelineHistoryTests
{
    [Fact]
    public void Young_history_stretches_to_at_least_one_minute()
    {
        var history = new TimelineHistory();
        history.Reset(DateTimeOffset.UnixEpoch);
        for (var second = 0; second < 20; second++)
        {
            history.Add(Point(second, cpu: second));
        }

        var window = history.GetWindow(600, 1000);

        Assert.Equal(0, window.StartSeconds);
        Assert.Equal(60, window.EndSeconds);
        Assert.Equal(20, window.Points.Count);
    }

    [Fact]
    public void Full_history_shows_only_the_last_range_and_keeps_spikes_when_reduced()
    {
        var history = new TimelineHistory();
        for (var second = 0; second < 3600; second++)
        {
            history.Add(Point(second, cpu: second == 3500 ? 99 : 5));
        }

        history.AddMarker(new TimelineMarker(100, 2, "old"));
        history.AddMarker(new TimelineMarker(3550, 1, "recent"));

        var window = history.GetWindow(600, 100);

        Assert.Equal(2999, window.StartSeconds);
        Assert.Equal(3599, window.EndSeconds);
        Assert.True(window.Points.Count <= 100);
        Assert.Contains(window.Points, point => point.Cpu == 99);
        Assert.Equal("recent", Assert.Single(window.Markers).Title);
    }

    [Fact]
    public void Long_sessions_are_compacted_instead_of_growing_forever()
    {
        var history = new TimelineHistory();
        for (var second = 0; second < 50_000; second++)
        {
            history.Add(Point(second, cpu: 1));
        }

        Assert.True(history.Count <= 20_000);
        var all = history.GetWindow(0, 500);
        Assert.Equal(0, all.StartSeconds, precision: 0);
        Assert.Equal(49_999, all.EndSeconds);
    }

    private static TimelinePoint Point(double seconds, double cpu) =>
        new(seconds, cpu, double.NaN, 10, 100, 0);
}
