using SessionPerfTracker.App.Services;
using SessionPerfTracker.Domain.Models;

namespace SessionPerfTracker.UnitTests;

public sealed class FeedbackReportServiceTests
{
    [Fact]
    public async Task Save_feedback_report_prunes_markdown_reports_by_count()
    {
        var directory = Path.Combine(Path.GetTempPath(), "spt-feedback-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            for (var index = 0; index < 3; index++)
            {
                var oldPath = Path.Combine(directory, $"old_{index}.md");
                await File.WriteAllTextAsync(oldPath, "old feedback");
                File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddMinutes(-10 - index));
            }

            var service = new FeedbackReportService(feedbackDirectory: directory);
            var savedPath = await service.SaveAsync(
                "bug-report",
                "Something went wrong.",
                screenshotPath: null,
                CreateContext(),
                includeLatestSession: false,
                new FeedbackSettings { MaxLocalReports = 2, MaxLocalReportAgeDays = 90 },
                CancellationToken.None);

            var reports = Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly).ToArray();
            Assert.NotNull(savedPath);
            Assert.Equal(2, reports.Length);
            Assert.Contains(reports, report => string.Equals(report, savedPath, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static FeedbackReportContext CreateContext() => new(
        CurrentVersionText: "1.0.0-test",
        StoragePath: "test-storage",
        SelectedTabIndex: 0,
        IsRecording: false,
        LiveSessionTargetText: "none",
        LiveSessionStateText: "idle",
        LiveConfigCollectorsText: "cpu, ram",
        ActiveThresholdSourceText: "test",
        StorageStatusText: "ok",
        ExportStatusText: "ok",
        UpdateStatusText: "ok",
        LiveWarningText: string.Empty,
        LatestSession: null);
}
