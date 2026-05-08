using System.Windows;
using SessionPerfTracker.App.ViewModels;

namespace SessionPerfTracker.App;

public partial class ProcessInspectorWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public ProcessInspectorWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void Monitor_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.MonitorInspectorTargetAsync();
        Close();
    }

    private async void OpenFileLocation_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.OpenInspectorFileLocationAsync();
    }

    private async void KillProcess_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.KillInspectorProcessAsync();
    }

    private async void KillTreeOrGroup_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.KillInspectorTreeOrGroupAsync();
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.CopyInspectorFullPath();
    }

    private async void MarkSuspicious_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.MarkInspectorSuspiciousAsync();
    }

    private async void RemoveSuspicious_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.RemoveInspectorSuspiciousAsync();
    }

    private async void BanProcess_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.BanInspectorTargetAsync();
    }

    private async void BanAndKillProcess_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.BanAndKillInspectorTargetAsync();
    }

    private void OpenRecommendation_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenRecommendationForInspectorTarget();
        Close();
    }
}
