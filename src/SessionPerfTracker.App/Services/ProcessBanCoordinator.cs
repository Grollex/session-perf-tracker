using SessionPerfTracker.App.ViewModels;
using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Models;

namespace SessionPerfTracker.App.Services;

public sealed class ProcessBanCoordinator
{
    private readonly IProcessControlService _processControlService;
    private readonly HashSet<string> _runningBannedPaths = new(StringComparer.OrdinalIgnoreCase);

    public ProcessBanCoordinator(IProcessControlService processControlService)
    {
        _processControlService = processControlService;
    }

    public void ClearRunningMatch(string normalizedPath)
    {
        _runningBannedPaths.Remove(normalizedPath);
    }

    public async Task<ProcessBanEnforcementResult> EnforceActiveBansAsync(
        ProcessBanSettings processBans,
        IReadOnlyList<GlobalProcessRowViewModel> rows,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        if (processBans.Active.Count == 0)
        {
            _runningBannedPaths.Clear();
            return new ProcessBanEnforcementResult(false, processBans, 0);
        }

        var active = processBans.Active
            .Where(rule => IsActive(rule, capturedAt))
            .GroupBy(rule => rule.NormalizedPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(rule => rule.CreatedAt)
                .First())
            .ToList();
        var changed = active.Count != processBans.Active.Count;
        var activeByPath = active
            .Where(rule => !string.IsNullOrWhiteSpace(rule.NormalizedPath))
            .ToDictionary(rule => rule.NormalizedPath, StringComparer.OrdinalIgnoreCase);
        var runningByPath = rows
            .Where(row => !string.IsNullOrWhiteSpace(row.NormalizedFullPath)
                && activeByPath.ContainsKey(row.NormalizedFullPath))
            .GroupBy(row => row.NormalizedFullPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var history = processBans.History.ToList();
        var totalTerminated = 0;

        foreach (var rule in active)
        {
            if (!runningByPath.TryGetValue(rule.NormalizedPath, out var matchingRows))
            {
                _runningBannedPaths.Remove(rule.NormalizedPath);
                continue;
            }

            var processIds = matchingRows
                .Select(row => row.ProcessId)
                .Where(processId => processId > 0)
                .Distinct()
                .ToArray();
            if (processIds.Length == 0)
            {
                continue;
            }

            var result = await _processControlService.KillProcessesAsync(
                processIds,
                "active process ban",
                cancellationToken);
            totalTerminated += result.TerminatedCount;

            if (_runningBannedPaths.Add(rule.NormalizedPath)
                || result.TerminatedCount > 0)
            {
                var representative = matchingRows
                    .OrderByDescending(row => row.CpuPercent ?? 0)
                    .First();
                var enforcementWasBlocked = result.TerminatedCount == 0
                    && result.Messages.Any(message => message.StartsWith("Skipped ", StringComparison.OrdinalIgnoreCase));
                history.Insert(0, CreateEvent(
                    rule,
                    enforcementWasBlocked ? "ban_blocked" : "ban_enforced",
                    representative,
                    result.TerminatedCount,
                    result.Messages.Count == 0
                        ? "Active process ban matched a running process."
                        : string.Join(" ", result.Messages.Take(3))));
                changed = true;
            }
        }

        if (!changed)
        {
            return new ProcessBanEnforcementResult(false, processBans, totalTerminated);
        }

        return new ProcessBanEnforcementResult(
            true,
            processBans with
            {
                Active = active,
                History = history
                    .OrderByDescending(entry => entry.Timestamp)
                    .Take(processBans.MaxHistory)
                    .ToList()
            },
            totalTerminated);
    }

    public static bool IsActive(ProcessBanRule rule, DateTimeOffset now) =>
        !string.IsNullOrWhiteSpace(rule.NormalizedPath)
        && (rule.ExpiresAt is null || rule.ExpiresAt > now);

    public ProcessBanEvent CreateEvent(
        ProcessBanRule rule,
        string action,
        GlobalProcessRowViewModel? row,
        int terminatedCount,
        string? details)
    {
        return new ProcessBanEvent
        {
            Id = $"ban_event_{Guid.NewGuid():N}",
            Timestamp = DateTimeOffset.UtcNow,
            NormalizedPath = rule.NormalizedPath,
            ExeName = rule.ExeName,
            ProductName = string.IsNullOrWhiteSpace(row?.ProductName) ? rule.ProductName : row.ProductName,
            CompanyName = string.IsNullOrWhiteSpace(row?.CompanyName) ? rule.CompanyName : row.CompanyName,
            SignerStatus = string.IsNullOrWhiteSpace(row?.SignerStatus) ? rule.SignerStatus : row.SignerStatus,
            Action = action,
            DurationLabel = rule.DurationLabel,
            ProcessId = row?.IsGroup == true ? null : row?.ProcessId,
            ProcessName = row?.DisplayName,
            TerminatedCount = terminatedCount,
            Details = details
        };
    }
}

public sealed record ProcessBanEnforcementResult(
    bool Changed,
    ProcessBanSettings ProcessBans,
    int TotalTerminated);
