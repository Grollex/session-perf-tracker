using SessionPerfTracker.Domain.Models;
using SessionPerfTracker.Infrastructure.Gpu;
using SessionPerfTracker.Infrastructure.Native;
using SessionPerfTracker.Infrastructure.Runner;

namespace SessionPerfTracker.UnitTests;

public sealed class ProcessTrackingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Children_keep_being_tracked_after_the_launcher_exits()
    {
        using var tracker = new ProcessTreeTracker(100, Ticks(0), includeChildren: true, ownHandles: false);
        tracker.Update(Snapshot(Process(100, 1, 0), Process(200, 100, 5)), T0.AddSeconds(6));
        Assert.Equal([100, 200], tracker.AliveProcessIds.Order());

        // Launcher exits, game keeps running and spawns a helper.
        tracker.Update(Snapshot(Process(200, 100, 5), Process(300, 200, 20)), T0.AddSeconds(21));

        Assert.False(tracker.RootAlive);
        Assert.Equal([200, 300], tracker.AliveProcessIds.Order());
    }

    [Fact]
    public void Reused_pid_of_a_dead_parent_does_not_pull_unrelated_processes_in()
    {
        using var tracker = new ProcessTreeTracker(100, Ticks(0), includeChildren: true, ownHandles: false);
        tracker.Update(Snapshot(Process(100, 1, 0)), T0.AddSeconds(1));
        tracker.Update(Snapshot(), T0.AddSeconds(10));

        // PID 100 reused by an unrelated program later; its child must not be tracked.
        tracker.Update(Snapshot(Process(100, 1, 60), Process(500, 100, 61)), T0.AddSeconds(62));

        Assert.Empty(tracker.AliveProcessIds);
    }

    [Fact]
    public void Root_only_scope_ignores_children()
    {
        using var tracker = new ProcessTreeTracker(100, Ticks(0), includeChildren: false, ownHandles: false);
        tracker.Update(Snapshot(Process(100, 1, 0), Process(200, 100, 5)), T0.AddSeconds(6));

        Assert.Equal([100], tracker.AliveProcessIds);
    }

    [Theory]
    [InlineData(0, SessionExitKind.ExternalClose)]
    [InlineData(unchecked((int)0xC000013A), SessionExitKind.ExternalClose)]
    [InlineData(unchecked((int)0xC0000005), SessionExitKind.CrashLikeExit)]
    [InlineData(unchecked((int)0xE0434352), SessionExitKind.CrashLikeExit)]
    [InlineData(1, SessionExitKind.UnexpectedExit)]
    [InlineData(-1, SessionExitKind.UnexpectedExit)]
    public void Exit_codes_are_classified_without_calling_every_non_zero_a_crash(int exitCode, SessionExitKind expected)
    {
        Assert.Equal(expected, ProcessSessionRunner.ClassifyExit(exitCode, hungAtExit: false).Kind);
    }

    [Fact]
    public void Missing_exit_code_and_hang_at_exit_are_classified()
    {
        Assert.Equal(SessionExitKind.UnexpectedExit, ProcessSessionRunner.ClassifyExit(null, hungAtExit: false).Kind);
        Assert.Equal(SessionExitKind.CrashLikeExit, ProcessSessionRunner.ClassifyExit(0, hungAtExit: true).Kind);
    }

    [Theory]
    [InlineData("pid_1234_luid_0x00000000_0x0000D1B4_phys_0_eng_3_engtype_3D", 1234, "0x00000000_0x0000d1b4", 3, "3D")]
    [InlineData("pid_88_luid_0x00000000_0x00010A76_phys_0_eng_7_engtype_Compute_1", 88, "0x00000000_0x00010a76", 7, "Compute_1")]
    public void Gpu_engine_instance_names_are_parsed(string name, int pid, string luid, int engine, string engineType)
    {
        Assert.True(GpuCounterInstanceName.TryParse(name, out var parsed));
        Assert.Equal(pid, parsed.ProcessId);
        Assert.Equal(luid, parsed.AdapterLuid);
        Assert.Equal(engine, parsed.EngineIndex);
        Assert.Equal(engineType, parsed.EngineType);
    }

    [Fact]
    public void Gpu_adapter_memory_instance_without_pid_is_parsed()
    {
        Assert.True(GpuCounterInstanceName.TryParse("luid_0x00000000_0x00010a76_phys_0", out var parsed));
        Assert.Null(parsed.ProcessId);
        Assert.True(GpuCounterInstanceName.TryParseLuid(parsed.AdapterLuid, out var low, out var high));
        Assert.Equal(0x00010a76u, low);
        Assert.Equal(0, high);
    }

    [Fact]
    public void Gpu_group_usage_is_the_busiest_shared_engine_not_a_sum_of_maxima()
    {
        var sample = new GpuSample(
            T0,
            IsAvailable: true,
            HasUtilization: true,
            new Dictionary<int, GpuProcessUsage>
            {
                [1] = new(40, 100, 0),
                [2] = new(30, 50, 0)
            },
            new Dictionary<int, IReadOnlyDictionary<string, double>>
            {
                [1] = new Dictionary<string, double> { ["3d"] = 40 },
                [2] = new Dictionary<string, double> { ["3d"] = 25, ["video"] = 30 }
            },
            []);

        var usage = sample.Aggregate([1, 2]);

        Assert.NotNull(usage);
        Assert.Equal(65, usage.UtilizationPercent, 3);
        Assert.Equal(150, usage.DedicatedMb, 3);
    }

    [Fact]
    public void System_snapshot_contains_this_process_with_sane_counters()
    {
        var snapshot = SystemProcessSnapshotReader.Shared.Capture();

        Assert.True(snapshot.TryGet(Environment.ProcessId, out var self));
        Assert.True(self.PrivateWorkingSetBytes > 0);
        Assert.True(self.CpuTime100Ns > 0);
        Assert.NotNull(self.StartedAtUtc);
        Assert.True(snapshot.Processes.Count > 10);
    }

    private static long Ticks(int seconds) => T0.AddSeconds(seconds).ToFileTime();

    private static ProcessRawInfo Process(int pid, int parent, int startedSecondsAfterT0) =>
        new(pid, parent, $"p{pid}.exe", Ticks(startedSecondsAfterT0), 0, 1024, 2048, 1024, 0, 0, 1, 1, 1);

    private static IReadOnlyDictionary<int, ProcessRawInfo> Snapshot(params ProcessRawInfo[] processes) =>
        processes.ToDictionary(process => process.ProcessId);
}
