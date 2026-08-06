using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SuperDoc.Sms.Models;
using SuperDoc.Sms.Services;
using SuperDoc.Sms.Storage;
using Windows.Graphics;

namespace SuperDoc.Sms.WinUI;

public sealed partial class MainWindow : Window
{
    private ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private ILogger _logger = NullLogger.Instance;
    private SmsRepository? _repo;
    private SmsManager? _smsManager;
    private MainViewModel? _viewModel;
    private DispatcherTimer? _timer;
    private TrayIconService? _tray;
    private ToastNotifier? _toasts;

    /// <summary>Set once the user really wants to quit, so closing stops hiding to the tray.</summary>
    private bool _exiting;

    public MainWindow()
    {
        InitializeComponent();
        ResizeToComfortableDefault();

        try
        {
            // Owned by App; this window borrows it and must not dispose it.
            _loggerFactory = (Application.Current as App)?.LoggerFactory ?? NullLoggerFactory.Instance;
            _logger = _loggerFactory.CreateLogger<MainWindow>();

            // After the logger exists, so a missing icon actually gets reported.
            ApplyWindowIcon();

            _repo = new SmsRepository(logger: _loggerFactory.CreateLogger<SmsRepository>());
            _smsManager = new SmsManager(
                _repo,
                _loggerFactory.CreateLogger<SmsManager>(),
                _loggerFactory.CreateLogger<SmsQueueProcessor>());

            _viewModel = new MainViewModel(
                _smsManager,
                _loggerFactory.CreateLogger<MainViewModel>(),
                DispatcherQueue);

            if (Content is FrameworkElement fe)
            {
                fe.DataContext = _viewModel;
            }

            _viewModel.Conversations.ConfirmAsync = ConfirmDestructiveAsync;

            // Arabic and other right-to-left languages need the whole layout mirrored, not just
            // translated text: panels, bubble alignment and scroll bars all flip with this.
            ApplyFlowDirection();
            _viewModel.Loc.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(ApplyFlowDirection);
            _viewModel.Conversations.ThreadChanged += ScrollThreadToBottom;

            _toasts = new ToastNotifier(_loggerFactory.CreateLogger<ToastNotifier>(), _viewModel.Loc);
            _smsManager.MessageReceived += OnSmsReceived;

            SetUpTray();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _timer.Tick += OnTimerTick;
            _timer.Start();
        }
        catch (Exception ex)
        {
            // Without this the window would come up blank (or the process would vanish) with
            // no hint that, say, the SQLite native library or the log directory was the problem.
            _logger.LogCritical(ex, "Startup failed.");
            ShowStartupFailure(ex);
        }

        Closed += OnClosed;
    }

    /// <summary>True when the window can be hidden and recovered again.</summary>
    public bool TrayAvailable => _tray?.IsAvailable == true;

    private void SetUpTray()
    {
        _tray = new TrayIconService(_loggerFactory.CreateLogger<TrayIconService>(), _viewModel!.Loc);
        if (!_tray.IsAvailable)
        {
            return;
        }

        _tray.ShowRequested += () => DispatcherQueue.TryEnqueue(ShowFromTray);
        _tray.ExitRequested += () => DispatcherQueue.TryEnqueue(ExitApplication);
        _tray.TestNotificationRequested += () => DispatcherQueue.TryEnqueue(() => _toasts?.ShowTest());

        // Closing the window would end the process, and with it the SMS receive registration.
        AppWindow.Closing += OnAppWindowClosing;
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exiting || _tray?.IsAvailable != true)
        {
            return;
        }

        args.Cancel = true;
        HideToTray();
    }

    public void HideToTray()
    {
        try
        {
            AppWindow.Hide();

            // Anything arriving from here on is unread, however long the thread stays selected.
            SetThreadVisibility(false);
            _logger.LogInformation("Window hidden to tray; still receiving SMS.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not hide the window to the tray.");
        }
    }

    public void ShowFromTray()
    {
        try
        {
            AppWindow.Show();
            Activate();
            SetThreadVisibility(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not restore the window from the tray.");
        }
    }

    /// <summary>
    /// Tells the thread view whether anyone can actually see it, which is what separates a
    /// message having arrived from it having been read.
    /// </summary>
    private void SetThreadVisibility(bool visible)
    {
        if (_viewModel is not null)
        {
            _viewModel.Conversations.WindowIsVisible = visible;
        }
    }

    private void ExitApplication()
    {
        _exiting = true;
        Close();
    }

    private void OnSmsReceived(object? sender, SmsMessage message)
        => DispatcherQueue.TryEnqueue(() => _toasts?.ShowIncoming(message));

    /// <summary>
    /// Puts the app's own icon in the title bar and Alt+Tab.
    /// </summary>
    /// <remarks>
    /// <c>&lt;ApplicationIcon&gt;</c> only embeds an icon in the executable, which Explorer and the
    /// taskbar use. A WinUI 3 window does not pick that up for its own title bar - it shows a
    /// generic placeholder until the icon is set on the AppWindow explicitly.
    /// </remarks>
    private void ApplyWindowIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (!File.Exists(path))
            {
                _logger.LogWarning("Window icon missing at {Path}; keeping the default.", path);
                return;
            }

            AppWindow.SetIcon(path);
        }
        catch (Exception ex)
        {
            // Cosmetic only; never stop the window opening over an icon.
            _logger.LogWarning(ex, "Could not set the window icon.");
        }
    }

    private void ApplyFlowDirection()
    {
        try
        {
            if (Content is FrameworkElement root && _viewModel is not null)
            {
                root.FlowDirection = _viewModel.Loc.IsRightToLeft
                    ? FlowDirection.RightToLeft
                    : FlowDirection.LeftToRight;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not apply the layout direction.");
        }
    }

    private void ResizeToComfortableDefault()
    {
        try
        {
            AppWindow.Resize(new SizeInt32(1280, 820));
        }
        catch (Exception)
        {
            // Sizing is cosmetic; a failure here must not stop the app from opening.
        }
    }

    private void OnTimerTick(object? sender, object e) => _ = _viewModel?.RefreshAsync();

    private void ShowStartupFailure(Exception ex)
    {
        Content = new ScrollViewer
        {
            Padding = new Thickness(24),
            Content = new TextBlock
            {
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.Wrap,
                Text =
                    "SUPERDOC SMS Manager could not start.\n\n" +
                    $"{ex.GetType().Name}: {ex.Message}\n\n" +
                    "Details are in %LOCALAPPDATA%\\SUPERDOC SMS Manager\\Logs\\.\n\n" +
                    ex
            }
        };
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (_timer is not null)
        {
            _timer.Tick -= OnTimerTick;
            _timer.Stop();
        }

        if (_smsManager is not null)
        {
            _smsManager.MessageReceived -= OnSmsReceived;
        }

        AppWindow.Closing -= OnAppWindowClosing;

        _tray?.Dispose();
        _toasts?.Dispose();
        _viewModel?.Dispose();
        _smsManager?.Dispose();
        _repo?.Dispose();
        // _loggerFactory is owned by App and intentionally left alive here.
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not Button button)
        {
            return;
        }

        // Tag comes from {Binding Id}; it arrives boxed as long, but parse as a fallback.
        var id = button.Tag switch
        {
            long value => value,
            _ => long.TryParse(button.Tag?.ToString(), out var parsed) ? parsed : (long?)null
        };

        if (id is not null)
        {
            await _viewModel.Conversations.RetryAsync(id.Value);
        }
    }

    // ----- Drag-to-resize -------------------------------------------------------------------
    //
    // Hand-rolled rather than using the Community Toolkit's PropertySizer: that control builds
    // fine in pass 1 but makes XamlCompiler's XBF generation fail in pass 2 with exit code 1 and
    // no diagnostic at all, which is the same toolchain fragility documented in docs\.
    // A Border plus pointer capture is a few lines and has no such surprise.

    private double _dragStart;
    private double _dragStartValue;
    private bool _dragging;

    private void PanelSizer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not UIElement bar || _viewModel is null)
        {
            return;
        }

        _dragStart = e.GetCurrentPoint(RootGrid).Position.X;
        _dragStartValue = _viewModel.Ui.LeftPanelWidth;
        _dragging = bar.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void PanelSizer_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || _viewModel is null)
        {
            return;
        }

        var delta = e.GetCurrentPoint(RootGrid).Position.X - _dragStart;
        _viewModel.Ui.LeftPanelWidth = _dragStartValue + delta;
        e.Handled = true;
    }

    private void PanelSizer_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is UIElement bar && _dragging)
        {
            bar.ReleasePointerCapture(e.Pointer);
        }

        _dragging = false;
    }

    private void ComposerSizer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not UIElement bar || _viewModel is null)
        {
            return;
        }

        _dragStart = e.GetCurrentPoint(RootGrid).Position.Y;
        _dragStartValue = _viewModel.Ui.ComposerHeight;
        _dragging = bar.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ComposerSizer_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || _viewModel is null)
        {
            return;
        }

        // The composer sits below the bar, so dragging upwards has to make it taller.
        var delta = _dragStart - e.GetCurrentPoint(RootGrid).Position.Y;
        _viewModel.Ui.ComposerHeight = _dragStartValue + delta;
        e.Handled = true;
    }

    private void ComposerSizer_PointerReleased(object sender, PointerRoutedEventArgs e)
        => PanelSizer_PointerReleased(sender, e);

    private void ScrollThreadToBottom()
    {
        // Two hops: the first lets the new item be created, the second lets it be laid out.
        // Scrolling before that targets an element that has no position yet.
        DispatcherQueue.TryEnqueue(() => DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var last = _viewModel?.Conversations.ThreadMessages.LastOrDefault();
                if (last is not null)
                {
                    ThreadList.ScrollIntoView(last);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not scroll the thread to the newest message.");
            }
        }));
    }

    /// <summary>
    /// Inserts the tapped emoji where the caret is, then puts the caret after it and returns
    /// focus to the box, so several can be added in a row without reaching for the mouse again.
    /// </summary>
    private void Emoji_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (_viewModel is null || e.ClickedItem is not string emoji)
        {
            return;
        }

        var start = MessageBox.SelectionStart;
        _viewModel.Conversations.InsertAtCaret(emoji, start, MessageBox.SelectionLength);

        // The binding rewrites Text, which resets the caret to the start; put it back.
        MessageBox.SelectionStart = Math.Min(start + emoji.Length, MessageBox.Text.Length);
        MessageBox.SelectionLength = 0;
        MessageBox.Focus(FocusState.Programmatic);
    }

    private void ThreadList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => _viewModel?.Conversations.SetSelectedMessages(ThreadList.SelectedItems.OfType<SmsMessage>());

    private void ConversationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView list)
        {
            _viewModel?.Conversations.SetSelectedConversations(list.SelectedItems.OfType<Conversation>());
        }
    }

    /// <summary>
    /// Asks before destroying data. Deletion here is permanent - no archive, no undo - so the
    /// dialog spells out the count and says so.
    /// </summary>
    private async Task<bool> ConfirmDestructiveAsync(string title, string message)
    {
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "Xoá",
                CloseButtonText = "Huỷ",
                DefaultButton = ContentDialogButton.Close
            };

            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            // A dialog that cannot be shown must not become a silent delete.
            _logger.LogError(ex, "Could not show the confirmation dialog; treating as cancelled.");
            return false;
        }
    }
}
