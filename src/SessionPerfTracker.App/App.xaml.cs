using System.Windows;
using System.Threading;

namespace SessionPerfTracker.App;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = "Local\\SessionPerfTracker.SingleInstance";
    private const string ShowWindowEventName = "Local\\SessionPerfTracker.ShowMainWindow";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showWindowEvent;
    private RegisteredWaitHandle? _showWindowWaitHandle;
    private bool _ownsSingleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        _ownsSingleInstanceMutex = isFirstInstance;

        if (!isFirstInstance)
        {
            try
            {
                _showWindowEvent.Set();
            }
            catch
            {
            }

            Shutdown();
            return;
        }

        _showWindowWaitHandle = ThreadPool.RegisterWaitForSingleObject(
            _showWindowEvent,
            (_, _) => Dispatcher.BeginInvoke(ShowExistingMainWindow),
            null,
            Timeout.InfiniteTimeSpan,
            executeOnlyOnce: false);

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWindowWaitHandle?.Unregister(null);
        _showWindowEvent?.Dispose();
        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void ShowExistingMainWindow()
    {
        if (MainWindow is not { } window)
        {
            return;
        }

        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        window.Focus();
    }
}
