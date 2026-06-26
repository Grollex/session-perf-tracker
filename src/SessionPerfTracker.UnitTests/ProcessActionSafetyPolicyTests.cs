using SessionPerfTracker.Domain.Services;

public sealed class ProcessActionSafetyPolicyTests
{
    private readonly ProcessActionSafetyPolicy _policy = new(
        @"C:\Windows",
        @"C:\Tools\SessionPerfTracker\SessionPerfTracker.App.exe",
        currentProcessId: 42);

    [Fact]
    public void Assess_blocks_current_process_id()
    {
        var assessment = _policy.Assess(42, "SessionPerfTracker.App.exe", @"C:\Tools\SessionPerfTracker\SessionPerfTracker.App.exe");

        Assert.False(assessment.IsAllowed);
        Assert.Contains("itself", assessment.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("csrss")]
    [InlineData("csrss.exe")]
    [InlineData("System")]
    [InlineData("winlogon.exe")]
    public void Assess_blocks_critical_windows_process_names(string executableName)
    {
        var assessment = _policy.Assess(100, executableName, null);

        Assert.False(assessment.IsAllowed);
        Assert.Contains("critical", assessment.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Assess_blocks_own_executable_path()
    {
        var assessment = _policy.Assess(100, "SessionPerfTracker.App.exe", @"C:\Tools\SessionPerfTracker\SessionPerfTracker.App.exe");

        Assert.False(assessment.IsAllowed);
        Assert.Contains("own executable", assessment.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Assess_blocks_executable_under_windows_directory()
    {
        var assessment = _policy.Assess(100, "notepad.exe", @"C:\Windows\System32\notepad.exe");

        Assert.False(assessment.IsAllowed);
        Assert.Contains("Windows directory", assessment.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Assess_allows_normal_user_executable()
    {
        var assessment = _policy.Assess(100, "game.exe", @"D:\Games\Example\game.exe");

        Assert.True(assessment.IsAllowed);
    }
}
