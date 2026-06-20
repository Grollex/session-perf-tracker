using System;
using System.Windows;
using System.Windows.Controls;
using SessionPerfTracker.App.ViewModels;

namespace SessionPerfTracker.App.Views;

public partial class CompareView : System.Windows.Controls.UserControl
{
    public CompareView()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private async void ExportCurrentCompareHtml_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.ExportCurrentCompareHtmlAsync();
        }
    }

    private async void ExportCurrentCompareCsv_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.ExportCurrentCompareCsvAsync();
        }
    }
}
