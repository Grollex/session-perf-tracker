using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SessionPerfTracker.App.ViewModels;

namespace SessionPerfTracker.App.Views;

public partial class GlobalWatchView : System.Windows.Controls.UserControl
{
    public GlobalWatchView()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void SortGlobalWatchProcess_Click(object sender, RoutedEventArgs e) => ViewModel?.SortGlobalWatchByProcess();

    private void SortGlobalWatchPid_Click(object sender, RoutedEventArgs e) => ViewModel?.SortGlobalWatchByPid();

    private void SortGlobalWatchCpu_Click(object sender, RoutedEventArgs e) => ViewModel?.SortGlobalWatchByCpu();

    private void SortGlobalWatchRam_Click(object sender, RoutedEventArgs e) => ViewModel?.SortGlobalWatchByRam();

    private void SortGlobalWatchDisk_Click(object sender, RoutedEventArgs e) => ViewModel?.SortGlobalWatchByDisk();

    private void SortGlobalWatchProfile_Click(object sender, RoutedEventArgs e) => ViewModel?.SortGlobalWatchByProfile();

    private void SortGlobalWatchHealth_Click(object sender, RoutedEventArgs e) => ViewModel?.SortGlobalWatchByHealth();

    private void ShowProcessInspector()
    {
        if (ViewModel is null) return;
        try
        {
            var parentWindow = Window.GetWindow(this);
            var inspector = new ProcessInspectorWindow(ViewModel)
            {
                Owner = parentWindow
            };
            inspector.ShowDialog();
        }
        catch (Exception error)
        {
            System.Windows.MessageBox.Show(
                Window.GetWindow(this),
                $"Could not open Process Inspector:\n{error.Message}",
                "Process Inspector",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void RefreshGlobalWatch_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.RefreshGlobalWatchAsync();
        }
    }

    private async void MonitorGlobalProcess_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.MonitorSelectedGlobalProcessAsync();
        }
    }

    private async void KillGlobalProcess_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.KillSelectedGlobalProcessAsync();
        }
    }

    private async void KillGlobalProcessTree_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.KillSelectedGlobalProcessTreeOrGroupAsync();
        }
    }

    private async void BanGlobalProcess_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.BanSelectedGlobalProcessAsync();
        }
    }

    private async void BanAndKillGlobalProcess_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.BanAndKillSelectedGlobalProcessAsync();
        }
    }

    private void OpenGlobalProcessInspector_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null && ViewModel.PrepareInspectorFromSelectedGlobalProcess())
        {
            ShowProcessInspector();
        }
    }

    private async void AssignGlobalWatchProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.AssignSelectedGlobalProcessProfileAsync();
        }
    }

    private void OpenRecommendationsForGlobalWatch_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.OpenRecommendationsForSelectedGlobalProcess();
    }

    private async void MarkGlobalWatchSuspicious_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.MarkSelectedGlobalProcessSuspiciousAsync();
        }
    }

    private async void RemoveGlobalWatchSuspicious_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.RemoveSelectedGlobalProcessSuspiciousAsync();
        }
    }

    private void SelectJournalTarget_Click(object sender, RoutedEventArgs e)
    {
        var group = (sender as FrameworkElement)?.DataContext as GlobalWatchJournalGroupViewModel;
        ViewModel?.SelectJournalTargetForOverview(group);
    }

    private void InspectJournalTarget_Click(object sender, RoutedEventArgs e)
    {
        var group = (sender as FrameworkElement)?.DataContext as GlobalWatchJournalGroupViewModel;
        if (ViewModel is not null && ViewModel.SelectJournalTargetForInspector(group))
        {
            ShowProcessInspector();
        }
    }

    private async void MarkJournalSuspicious_Click(object sender, RoutedEventArgs e)
    {
        var group = (sender as FrameworkElement)?.DataContext as GlobalWatchJournalGroupViewModel;
        if (ViewModel is not null)
        {
            await ViewModel.MarkJournalTargetSuspiciousAsync(group);
        }
    }

    private async void BanJournalTarget_Click(object sender, RoutedEventArgs e)
    {
        var group = (sender as FrameworkElement)?.DataContext as GlobalWatchJournalGroupViewModel;
        if (ViewModel is not null)
        {
            await ViewModel.BanJournalTargetAsync(group);
        }
    }

    private async void BanAndKillJournalTarget_Click(object sender, RoutedEventArgs e)
    {
        var group = (sender as FrameworkElement)?.DataContext as GlobalWatchJournalGroupViewModel;
        if (ViewModel is not null)
        {
            await ViewModel.BanJournalTargetAsync(group, killAfterBan: true);
        }
    }

    private async void PromoteRecommendation_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.PromoteSelectedRecommendationAsync();
        }
    }

    private async void PromoteSelectedRecommendations_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var selected = ProfileRecommendationsList.SelectedItems
            .OfType<ProfileRecommendationViewModel>()
            .ToArray();
        await ViewModel.PromoteRecommendationsAsync(selected);
    }

    private async void DenySelectedRecommendations_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var selected = ProfileRecommendationsList.SelectedItems
            .OfType<ProfileRecommendationViewModel>()
            .ToArray();
        await ViewModel.DenyRecommendationsAsync(selected);
    }

    private async void RemoveRecommendationDeny_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.RemoveSelectedRecommendationDenyAsync();
        }
    }

    private void SelectRecommendationTarget_Click(object sender, RoutedEventArgs e)
    {
        var recommendation = (sender as FrameworkElement)?.DataContext as ProfileRecommendationViewModel
            ?? ViewModel?.SelectedProfileRecommendation;
        ViewModel?.SelectRecommendationTargetForOverview(recommendation);
    }

    private void InspectRecommendationTarget_Click(object sender, RoutedEventArgs e)
    {
        var recommendation = (sender as FrameworkElement)?.DataContext as ProfileRecommendationViewModel
            ?? ViewModel?.SelectedProfileRecommendation;
        if (ViewModel is not null && ViewModel.SelectRecommendationTargetForInspector(recommendation))
        {
            ShowProcessInspector();
        }
    }

    private async void MarkRecommendationSuspicious_Click(object sender, RoutedEventArgs e)
    {
        var recommendation = (sender as FrameworkElement)?.DataContext as ProfileRecommendationViewModel
            ?? ViewModel?.SelectedProfileRecommendation;
        if (ViewModel is not null)
        {
            await ViewModel.MarkRecommendationTargetSuspiciousAsync(recommendation);
        }
    }

    private async void BanRecommendationTarget_Click(object sender, RoutedEventArgs e)
    {
        var recommendation = (sender as FrameworkElement)?.DataContext as ProfileRecommendationViewModel
            ?? ViewModel?.SelectedProfileRecommendation;
        if (ViewModel is not null)
        {
            await ViewModel.BanRecommendationTargetAsync(recommendation);
        }
    }

    private async void BanAndKillRecommendationTarget_Click(object sender, RoutedEventArgs e)
    {
        var recommendation = (sender as FrameworkElement)?.DataContext as ProfileRecommendationViewModel
            ?? ViewModel?.SelectedProfileRecommendation;
        if (ViewModel is not null)
        {
            await ViewModel.BanRecommendationTargetAsync(recommendation, killAfterBan: true);
        }
    }

    private async void RemoveSelectedSuspiciousWatchItem_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.RemoveSelectedSuspiciousWatchItemAsync();
        }
    }

    private void InspectSuspiciousWatchItem_Click(object sender, RoutedEventArgs e)
    {
        var item = (sender as FrameworkElement)?.DataContext as SuspiciousWatchItemViewModel
            ?? ViewModel?.SelectedSuspiciousWatchItem;
        if (ViewModel is not null && ViewModel.SelectSuspiciousTargetForInspector(item))
        {
            ShowProcessInspector();
        }
    }

    private async void RemoveSelectedProcessBan_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.RemoveSelectedProcessBanAsync();
        }
    }
}
