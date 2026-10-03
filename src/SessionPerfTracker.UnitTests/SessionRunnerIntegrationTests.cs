using System.Diagnostics;
using SessionPerfTracker.Domain.Models;
using SessionPerfTracker.Domain.Services;
using SessionPerfTracker.Infrastructure.Collectors;
using SessionPerfTracker.Infrastructure.Runner;
using SessionPerfTracker.Infrastructure.Storage;

namespace SessionPerfTracker.UnitTests;

/// <summary>End-to-end recordings of real short-lived processes (Windows only).</summary>
public sealed class SessionRunnerIntegrationTests
{
    private static readonly string PowerShell = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe");

    [Fact]
    public async Task Recording_follows_child_process_after_launcher_exits()
    {
        var (runner, store) = CreateRunner();
        using var launcher = StartPowerShell(
            "Start-Sleep -Milliseconds 1500; Start-Process ping -ArgumentList '-n','4','127.0.0.1' -WindowStyle Hidden");
        var handle = await runner.AttachAsync(Target(launcher.Id), Sampling());

        var session = await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("Root process exited", session.Notes ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(SessionStatus.ExternalExit, session.Status);
        Assert.Contains(session.Samples, sample => sample.ProcessCount >= 1);
        // The session lasted beyond the launcher (~1.5 s) because the child kept running (~3 s).
        Assert.True(session.Summary.Duration > TimeSpan.FromSeconds(3), $"Duration was {session.Summary.Duration}.");
        var saved = await store.GetSessionAsync(session.Id);
        Assert.Equal(session.Samples.Count, saved!.Samples.Count);
    }

    [Fact]
    public async Task Crash_status_exit_code_is_classified_as_crash_like()
    {
        var (runner, _) = CreateRunner();
        using var process = StartPowerShell("Start-Sleep -Milliseconds 1200; exit -1073741819");
        var handle = await runner.AttachAsync(Target(process.Id, includeChildren: false), Sampling());

        var session = await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(SessionStatus.CrashLikeExit, session.Status);
        Assert.Contains("access violation", session.Summary.ExitReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Manual_stop_saves_a_stopped_session()
    {
        var (runner, store) = CreateRunner();
        using var process = StartPowerShell("Start-Sleep -Seconds 20");
        try
        {
            var handle = await runner.AttachAsync(Target(process.Id), Sampling());
            await Task.Delay(1800);
            await handle.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var session = await handle.Completion;

            Assert.Equal(SessionStatus.Stopped, session.Status);
            Assert.True(session.Samples.Count >= 2);
            Assert.All(session.Samples, sample => Assert.True(sample.Values.ContainsKey(MetricKey.CpuPercent)));
            Assert.Equal(SessionStatus.Stopped, (await store.GetSessionAsync(session.Id))!.Status);
        }
        finally
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }
    }

    private static (ProcessSessionRunner Runner, SqliteSessionStore Store) CreateRunner()
    {
        var directory = Path.Combine(Path.GetTempPath(), "spt-runner-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var summary = new SessionSummaryService();
        var store = new SqliteSessionStore(Path.Combine(directory, "sessionperftracker.db"), summary);
        return (new ProcessSessionRunner(new ProcessCpuMemoryCollector(), store, summary), store);
    }

    private static Process StartPowerShell(string command) =>
        Process.Start(new ProcessStartInfo
        {
            FileName = PowerShell,
            Arguments = $"-NoProfile -NonInteractive -Command \"{command}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

    private static TargetDescriptor Target(int processId, bool includeChildren = true) => new()
    {
        Id = $"process_{processId}",
        Kind = TargetSelectionKind.Process,
        LifecycleMode = TargetLifecycleMode.AttachToRunning,
        DisplayName = $"powershell ({processId})",
        ProcessId = processId,
        IncludeChildProcesses = includeChildren,
        ScopeMode = includeChildren ? ProcessScopeMode.IncludeChildProcesses : ProcessScopeMode.RootOnly
    };

    private static SamplingSettings Sampling() => new()
    {
        IntervalMs = 500,
        StorageBatchSize = 3
    };
}
