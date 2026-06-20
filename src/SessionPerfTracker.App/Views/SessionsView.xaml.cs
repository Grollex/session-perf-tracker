using System;
using System.Windows;
using System.Windows.Controls;
using SessionPerfTracker.App.ViewModels;

namespace SessionPerfTracker.App.Views;

public partial class SessionsView : System.Windows.Controls.UserControl
{
    public SessionsView()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void OpenSessionDetails_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.OpenSelectedSessionDetails();
    }

    private async void RefreshSessions_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.ReloadSessionsAsync();
        }
    }

    private async void ExportSelectedSessionHtml_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.ExportSelectedSessionHtmlAsync();
        }
    }

    private async void ExportSelectedSessionCsv_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.ExportSelectedSessionCsvAsync();
        }
    }
}
