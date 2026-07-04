using SessionPerfTracker.Domain.Models;
using SessionPerfTracker.Infrastructure.Export;

namespace SessionPerfTracker.UnitTests;

public sealed class ProcessActionAuditExportTests
{
    [Fact]
    public async Task Export_process_action_audit_writes_html_and_csv_reports()
    {
        var directory = Path.Combine(Path.GetTempPath(), "spt-audit-export-tests", Guid.NewGuid().ToString("N"));
        var service = new HtmlCsvExportService(directory);
        var events = new[]
        {
            new ProcessBanEvent
            {
                Id = "audit_1",
                Timestamp = DateTimeOffset.Parse("2026-07-04T12:00:00Z"),
                NormalizedPath = @"c:\apps\demo.exe",
                ExeName = "demo.exe",
                ProductName = "Demo",
                CompanyName = "Example",
                SignerStatus = "Signed",
                Action = "kill_confirmed",
                DurationLabel = "n/a",
                ProcessId = 1234,
                ProcessName = "demo",
                TerminatedCount = 0,
                Details = "User confirmed hard kill."
            },
            new ProcessBanEvent
            {
                Id = "audit_2",
                Timestamp = DateTimeOffset.Parse("2026-07-04T12:00:03Z"),
                NormalizedPath = @"c:\apps\demo.exe",
                ExeName = "demo.exe",
                Action = "kill_executed",
                DurationLabel = "n/a",
                ProcessId = 1234,
                ProcessName = "demo",
                TerminatedCount = 1,
                Details = "Terminated PID 1234."
            }
        };

        var htmlPath = await service.ExportProcessActionAuditAsync(events, "html");
        var csvPath = await service.ExportProcessActionAuditAsync(events, "csv");

        var html = await File.ReadAllTextAsync(htmlPath);
        var csv = await File.ReadAllTextAsync(csvPath);
        Assert.Contains("Process action audit", html);
        Assert.Contains("Confirmation context", html);
        Assert.Contains("User confirmed", html);
        Assert.Contains("timestamp,action,target,path,reason_details,result,confirmation_context", csv);
        Assert.Contains("kill_executed", csv);
        Assert.Contains("1 terminated", csv);
    }
}
