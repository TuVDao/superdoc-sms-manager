using H.NotifyIcon;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Message_T480s.WinUI;

/// <summary>
/// The tray presence that lets the app keep running after its window is closed. Closing the
/// window has to hide it rather than exit, because inbound SMS only reaches a live process -
/// and a hidden window with no tray icon would be unreachable.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly ILogger _logger;
    private readonly Strings _loc;
    private readonly TaskbarIcon? _icon;
    private bool _disposed;

    /// <summary>Raised when the user asks for the window back.</summary>
    public event Action? ShowRequested;

    /// <summary>Raised when the user chooses Exit, meaning a real shutdown.</summary>
    public event Action? ExitRequested;

    /// <summary>Raised when the user asks for a test notification.</summary>
    public event Action? TestNotificationRequested;

    public TrayIconService(ILogger logger, Strings loc)
    {
        _logger = logger;
        _loc = loc;

        try
        {
            var showItem = new MenuFlyoutItem { Text = _loc.TrayOpen };
            showItem.Click += (_, _) => ShowRequested?.Invoke();

            // Lets the user confirm notifications work without waiting for someone to text them
            // while the window happens to be hidden.
            var testItem = new MenuFlyoutItem { Text = _loc.TrayTestNotification };
            testItem.Click += (_, _) => TestNotificationRequested?.Invoke();

            var exitItem = new MenuFlyoutItem { Text = _loc.TrayExit };
            exitItem.Click += (_, _) => ExitRequested?.Invoke();

            var menu = new MenuFlyout();
            menu.Items.Add(showItem);
            menu.Items.Add(testItem);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(exitItem);

            _icon = new TaskbarIcon
            {
                ToolTipText = "SUPERDOC SMS Manager",
                ContextFlyout = menu,
                ContextMenuMode = ContextMenuMode.SecondWindow,
                LeftClickCommand = new DelegateCommand(() => ShowRequested?.Invoke())
            };

            var iconSource = TryLoadIcon();
            if (iconSource is not null)
            {
                _icon.IconSource = iconSource;
            }

            _icon.ForceCreate(enablesEfficiencyMode: false);
            _logger.LogInformation("Tray icon created.");
        }
        catch (Exception ex)
        {
            // Losing the tray icon must not take the app down, but the user needs to know the
            // window can no longer be recovered after closing it.
            _logger.LogError(ex, "Could not create the tray icon; closing the window will exit the app.");
            _icon = null;
        }
    }

    /// <summary>False when the tray is unavailable, so the caller must not hide the window.</summary>
    public bool IsAvailable => _icon is not null;

    /// <summary>
    /// Resolves the icon for both deployment shapes: ms-appx only exists inside a package, so
    /// the unpackaged build reads the asset from beside the executable instead.
    /// </summary>
    /// <remarks>
    /// This must be a real .ico. The tray library converts the source to a
    /// <see cref="System.Drawing.Icon"/>, which rejects a PNG stream with
    /// "Argument 'picture' must be a picture that can be used as a Icon".
    /// </remarks>
    private BitmapImage? TryLoadIcon()
    {
        try
        {
            // The two deployment shapes need different URI schemes, and getting it wrong throws
            // asynchronously inside the tray library rather than here: a packaged app resolves
            // a file:// path through StorageFile.GetFileFromApplicationUriAsync, which only
            // accepts ms-appx / ms-appdata and fails with "Value does not fall within the
            // expected range".
            if (StartupRegistration.IsPackaged)
            {
                return new BitmapImage(new Uri("ms-appx:///Assets/TrayIcon.ico"));
            }

            var local = Path.Combine(AppContext.BaseDirectory, "Assets", "TrayIcon.ico");
            if (!File.Exists(local))
            {
                _logger.LogWarning("Tray icon asset missing at {Path}; using the default icon.", local);
                return null;
            }

            return new BitmapImage(new Uri(local));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Falling back to the default tray icon.");
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _icon?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ignoring failure while removing the tray icon.");
        }
    }
}
