using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SessionPerfTracker.App.ViewModels;

namespace SessionPerfTracker.App.Views;

public partial class SettingsView : System.Windows.Controls.UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private async void SaveCaptureSettings_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.SaveCaptureSettingsAsync();
        }
    }

    private async void SaveAntiNoiseSettings_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.SaveAntiNoiseSettingsAsync();
        }
    }

    private async void SaveAppBehaviorSettings_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.SaveAppBehaviorSettingsAsync();
        }
    }

    private async void SaveLanguageSettings_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.SaveLanguageSettingsAsync();
        }
    }

    private async void SaveThresholdSettings_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.SaveThresholdSettingsAsync();
        }
    }

    private async void ResetSelectedProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.ResetSelectedProfileAsync();
        }
    }

    private async void ResetAllThresholdSettings_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.ResetAllThresholdSettingsAsync();
        }
    }

    private async void SaveSelectedProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.SaveSelectedProfileAsync();
        }
    }

    private async void SaveAppProfileAssignment_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.SaveAppProfileAssignmentAsync();
        }
    }

    private async void RemoveAppProfileAssignment_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.RemoveSelectedAppProfileAssignmentAsync();
        }
    }

    private async void SaveRetentionSettings_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.SaveRetentionSettingsAsync();
        }
    }

    private async void DeleteAllSessions_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.DeleteAllSessionsAsync();
        }
    }

    private async void DeleteFilteredSessions_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.DeleteFilteredSessionsAsync();
        }
    }

    private async void DeleteSessionsOlderThan1Day_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.DeleteSessionsOlderThanAsync(1);
        }
    }

    private async void DeleteSessionsOlderThan7Days_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.DeleteSessionsOlderThanAsync(7);
        }
    }

    private async void DeleteSessionsOlderThan30Days_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.DeleteSessionsOlderThanAsync(30);
        }
    }

    private void BrowseExportDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select export folder",
            InitialDirectory = Directory.Exists(ViewModel.ExportDirectoryText)
                ? ViewModel.ExportDirectoryText
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };

        var parentWindow = Window.GetWindow(this);
        if (dialog.ShowDialog(parentWindow) == true)
        {
            ViewModel.ExportDirectoryText = dialog.FolderName;
        }
    }

    private async void SaveExportDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.SaveExportSettingsAsync();
        }
    }

    private async void ResetExportDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.ResetExportDirectoryAsync();
        }
    }

    private async void OpenExportDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.OpenExportDirectoryAsync();
        }
    }

    private async void RefreshExports_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.RefreshExportFilesAsync();
        }
    }

    private async void OpenSelectedExport_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.OpenSelectedExportAsync();
        }
    }

    private async void SaveUpdateSettings_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.SaveUpdateSettingsAsync();
        }
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.CheckForUpdatesAsync();
        }
    }

    private async void DownloadAndLaunchUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.DownloadAndLaunchUpdateAsync();
        }
    }

    private async void SkipUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.SkipAvailableUpdateAsync();
        }
    }
}
