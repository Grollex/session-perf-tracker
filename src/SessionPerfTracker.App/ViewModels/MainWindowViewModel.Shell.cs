namespace SessionPerfTracker.App.ViewModels;

/// <summary>Left navigation and page headings.</summary>
public sealed partial class MainWindowViewModel
{
    private const int CompareTabIndex = 4;

    public bool IsLiveNavSelected => SelectedTabIndex == LiveTabIndex;
    public bool IsGlobalWatchNavSelected => SelectedTabIndex == GlobalWatchTabIndex;

    // Details is reached from Sessions, so Sessions stays highlighted there.
    public bool IsSessionsNavSelected => SelectedTabIndex is SessionsTabIndex or SessionDetailsTabIndex;
    public bool IsCompareNavSelected => SelectedTabIndex == CompareTabIndex;
    public bool IsSettingsNavSelected => SelectedTabIndex == SettingsTabIndex;

    public string PageTitle => GetText(SelectedTabIndex switch
    {
        GlobalWatchTabIndex => "Ui_PageGlobalWatchTitle",
        SessionsTabIndex => "Ui_PageSessionsTitle",
        SessionDetailsTabIndex => "Ui_PageDetailsTitle",
        CompareTabIndex => "Ui_PageCompareTitle",
        SettingsTabIndex => "Ui_PageSettingsTitle",
        _ => "Ui_PageLiveTitle"
    });

    public string PageSubtitle => GetText(SelectedTabIndex switch
    {
        GlobalWatchTabIndex => "Ui_PageGlobalWatchSubtitle",
        SessionsTabIndex => "Ui_PageSessionsSubtitle",
        SessionDetailsTabIndex => "Ui_PageDetailsSubtitle",
        CompareTabIndex => "Ui_PageCompareSubtitle",
        SettingsTabIndex => "Ui_PageSettingsSubtitle",
        _ => "Ui_PageLiveSubtitle"
    });

    public void Navigate(int tabIndex)
    {
        IsTargetPickerOpen = false;
        SelectedTabIndex = tabIndex;
    }

    private void NotifyShellProperties()
    {
        OnPropertyChanged(nameof(IsLiveNavSelected));
        OnPropertyChanged(nameof(IsGlobalWatchNavSelected));
        OnPropertyChanged(nameof(IsSessionsNavSelected));
        OnPropertyChanged(nameof(IsCompareNavSelected));
        OnPropertyChanged(nameof(IsSettingsNavSelected));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageSubtitle));
    }
}
