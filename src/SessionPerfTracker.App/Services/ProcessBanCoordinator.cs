using SessionPerfTracker.App.ViewModels;
using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Models;
using SessionPerfTracker.Domain.Services;

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
            return new ProcessBanEnforcementResult(false, processBans, 0, []);
        }

        var active = processBans.Active
            .Where(rule => IsActive(rule, capturedAt))
            .GroupBy(rule => rule.NormalizedPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(rule => rule.CreatedAt)
                .First())
            .ToList();
        var changed = active.Count != processBans.Active.Count;
        // A rule matches its primary path and any confirmed copies of the same binary.
        var ruleByPath = new Dictionary<string, ProcessBanRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in active)
        {
            foreach (var path in TrackedExecutableMatcher.AllPaths(rule))
            {
                ruleByPath.TryAdd(path, rule);
            }
        }

        var rowsByRule = rows
            .Where(row => !string.IsNullOrWhiteSpace(row.NormalizedFullPath)
                && ruleByPath.ContainsKey(row.NormalizedFullPath))
            .GroupBy(row => ruleByPath[row.NormalizedFullPath].Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var history = processBans.History.ToList();
        var newEvents = new List<ProcessBanEvent>();
        var totalTerminated = 0;

        foreach (var rule in active)
        {
            if (!rowsByRule.TryGetValue(rule.Id, out var matchingRows))
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
                var enforcementEvent = CreateEvent(
                    rule,
                    enforcementWasBlocked ? "ban_blocked" : "ban_enforced",
                    representative,
                    result.TerminatedCount,
                    result.Messages.Count == 0
                        ? "Active process ban matched a running process."
                        : string.Join(" ", result.Messages.Take(3)));
                history.Insert(0, enforcementEvent);
                newEvents.Add(enforcementEvent);
                changed = true;
            }
        }

        if (!changed)
        {
            return new ProcessBanEnforcementResult(false, processBans, totalTerminated, []);
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
            totalTerminated,
            newEvents);
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
    int TotalTerminated,
    IReadOnlyList<ProcessBanEvent> NewEvents);
