using System.Windows;

namespace MicOptimizer.App;

public partial class App : Application
{
    // "Local\" keeps both names per Windows session, so two users on one PC don't block each other.
    private const string MutexName = @"Local\MicOptimizer.SingleInstance";
    private const string ShowEventName = @"Local\MicOptimizer.ShowWindow";

    private Mutex? _singleInstance;
    private EventWaitHandle? _showRequest;
    private MainWindow? _window;
    private TrayController? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, MutexName, out bool isFirst);
        _showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!isFirst)
        {
            // Another copy is already running (probably in the tray): ask it to show its window, then leave.
            _showRequest.Set();
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        // The app lives in the tray, so closing the window must not end the process.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SessionEnding += (_, _) => { if (_window is not null) _window.IsExiting = true; };

        _window = new MainWindow();
        _tray = new TrayController(_window, _window.ViewModel);
        _window.Tray = _tray;

        var listener = new Thread(WaitForShowRequests) { IsBackground = true, Name = "ShowRequestListener" };
        listener.Start();

        if (!e.Args.Contains(StartupRegistration.BackgroundArgument))
            _window.ShowFromTray();
    }

    private void WaitForShowRequests()
    {
        try
        {
            while (_showRequest is { } request && request.WaitOne())
                Dispatcher.BeginInvoke(() => _window?.ShowFromTray());
        }
        catch (ObjectDisposedException)
        {
            // The app is shutting down.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _window?.ViewModel.Dispose();
        _showRequest?.Dispose();
        if (_singleInstance is not null)
        {
            try { _singleInstance.ReleaseMutex(); } catch (ApplicationException) { /* not owned on this thread */ }
            _singleInstance.Dispose();
        }
        base.OnExit(e);
    }
}
