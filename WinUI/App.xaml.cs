using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using SuperDoc.Sms.Logging;
using SuperDoc.Sms.Services;

namespace SuperDoc.Sms.WinUI;

public partial class App : Application
{
    private MainWindow? _window;
    private SingleInstanceGuard? _guard;
    private ILogger? _logger;

    /// <summary>
    /// The single logger factory for the process. Shared with <see cref="MainWindow"/>: two
    /// factories would open two writers on the same log file and clobber each other's output.
    /// </summary>
    internal ILoggerFactory LoggerFactory { get; private set; } = NullLoggerFactory.Instance;

    public App()
    {
        InitializeComponent();

        try
        {
            LoggerFactory = LoggingSetup.CreateLoggerFactory();
            _logger = LoggerFactory.CreateLogger<App>();
        }
        catch (Exception)
        {
            // Logging must never be the reason the app fails to start.
        }

        // A packaged WinUI app that faults simply disappears from the user's point of view.
        // Recording the fault gives the log file something to explain the disappearance.
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            _logger?.LogCritical(e.ExceptionObject as Exception, "Unhandled exception on a background thread.");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _logger?.LogError(e.Exception, "Unobserved task exception.");
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // A demo window is a second, harmless copy: it never touches the modem, so it must not
        // hand over to - or be blocked by - the instance that is doing the real work.
        _guard = SingleInstanceGuard.Acquire(exclusive: !DemoMode.IsEnabled);
        if (!_guard.IsPrimary)
        {
            // Another copy already owns the SMS registration; it has been told to show itself.
            _logger?.LogInformation("Another instance is already running. Exiting.");
            _guard.Dispose();
            Exit();
            return;
        }

        _window = new MainWindow();

        _guard.ShowRequested += () =>
            _window.DispatcherQueue.TryEnqueue(() => _window.ShowFromTray());
        _guard.StartListening();

        // Unpackaged builds have no startup task; keep the Run entry pointing at this exe.
        // A demo run must not repoint it at whatever build produced the screenshots.
        if (!DemoMode.IsEnabled)
        {
            StartupRegistration.Enable(LoggerFactory.CreateLogger(nameof(StartupRegistration)));
        }

        // Started automatically at sign-in: come up in the tray so the app is receiving without
        // putting a window in the user's face.
        if (WasStartedAutomatically() && _window.TrayAvailable)
        {
            _logger?.LogInformation("Auto-started at sign-in; starting hidden in the tray.");
            _window.Activate();
            _window.HideToTray();
        }
        else
        {
            _window.Activate();
        }
    }

    /// <summary>True when Windows launched the app at sign-in rather than the user launching it.</summary>
    /// <remarks>
    /// Two mechanisms: the MSIX startup task reports itself through the activation args, while
    /// the unpackaged build is launched from the HKCU Run key with an --autostart argument.
    /// </remarks>
    private bool WasStartedAutomatically()
    {
        if (Environment.GetCommandLineArgs().Any(a =>
                string.Equals(a, "--autostart", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        try
        {
            return AppInstance.GetCurrent().GetActivatedEventArgs()?.Kind == ExtendedActivationKind.StartupTask;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Could not read activation kind; treating this as a manual launch.");
            return false;
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        _logger?.LogCritical(e.Exception, "Unhandled UI exception: {Message}", e.Message);

        // Keep the window alive so the user can see the current state and read the log path,
        // rather than having the process torn down mid-conversation.
        e.Handled = true;
    }
}
