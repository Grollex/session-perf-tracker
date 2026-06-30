using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

    private async void SaveBugReport_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.SaveBugReportAsync(CaptureFeedbackScreenshot("bug-report"));
        }
    }

    private async void SaveFeatureFeedback_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.SaveFeatureFeedbackAsync(CaptureFeedbackScreenshot("feature-idea"));
        }
    }

    private async void OpenFeedbackDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.OpenFeedbackDirectoryAsync();
        }
    }

    private async void OpenLatestFeedbackReport_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.OpenLatestFeedbackReportAsync();
        }
    }

    private async void CopyLatestFeedbackReport_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.CopyLatestFeedbackReportAsync();
        }
    }

    private string? CaptureFeedbackScreenshot(string kind)
    {
        if (ViewModel?.IncludeScreenshotInFeedback != true)
        {
            return null;
        }

        var window = Window.GetWindow(this);
        if (window is null || window.ActualWidth <= 0 || window.ActualHeight <= 0)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(ViewModel.FeedbackDirectoryText);
            var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
            var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);

            var path = Path.Combine(
                ViewModel.FeedbackDirectoryText,
                $"{DateTimeOffset.Now:yyyyMMdd_HHmmss}_{kind}_screenshot.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
            return path;
        }
        catch
        {
            return null;
        }
    }
}
