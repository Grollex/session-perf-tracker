using System;
using System.Windows;
using System.Windows.Controls;
using SessionPerfTracker.App.ViewModels;

namespace SessionPerfTracker.App.Views;

public partial class SessionDetailsView : System.Windows.Controls.UserControl
{
    public SessionDetailsView()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void BackToSessions_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.BackToSessions();
    }
}
