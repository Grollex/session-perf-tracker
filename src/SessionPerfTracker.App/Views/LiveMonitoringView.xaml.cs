using System;
using System.Windows;
using System.Windows.Controls;
using SessionPerfTracker.App.ViewModels;

namespace SessionPerfTracker.App.Views;

public partial class LiveMonitoringView : System.Windows.Controls.UserControl
{
    public LiveMonitoringView()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void BrowseExecutable_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            Title = "Select target executable"
        };

        var parentWindow = Window.GetWindow(this);
        if (dialog.ShowDialog(parentWindow) == true)
        {
            ViewModel?.SelectExecutable(dialog.FileName);
        }
    }

    private async void RefreshProcesses_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.RefreshRunningProcessesAsync();
        }
    }

    private async void StartRecording_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.StartRecordingAsync();
        }
    }

    private async void StopRecording_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.StopRecordingAsync();
        }
    }

    private async void AssignCurrentTargetProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.AssignCurrentTargetProfileAsync();
        }
    }

    private async void CaptureRamDiagnostic_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.CaptureRamDiagnosticAsync();
        }
    }

    private async void CaptureSystemContext_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.CaptureSystemContextAsync();
        }
    }
}
