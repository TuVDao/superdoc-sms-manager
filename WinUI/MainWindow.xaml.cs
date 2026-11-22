using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SuperDoc.Sms.Models;
using SuperDoc.Sms.Services;
using SuperDoc.Sms.Storage;
using Windows.Graphics;
using Windows.UI;
using Windows.ApplicationModel.DataTransfer;

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

        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            // Owned by App; this window borrows it and must not dispose it.
            _loggerFactory = (Application.Current as App)?.LoggerFactory ?? NullLoggerFactory.Instance;
            _logger = _loggerFactory.CreateLogger<MainWindow>();

            // After the logger exists, so a missing icon actually gets reported.
            ApplyWindowIcon();
            ConfigureTitleBarAndTheme();

            _repo = new SmsRepository(logger: _loggerFactory.CreateLogger<SmsRepository>());
            _smsManager = new SmsManager(
                _repo,
                _loggerFactory.CreateLogger<SmsManager>(),
                _loggerFactory.CreateLogger<SmsQueueProcessor>());

            _viewModel = new MainViewModel(
                _smsManager,
                _loggerFactory.CreateLogger<MainViewModel>(),
                DispatcherQueue);

            ResizeToComfortableDefault();

            AppWindow.Changed += (sender, args) =>
            {
                if (args.DidSizeChange && AppWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Restored)
                {
                    _viewModel?.Ui.SaveWindowSize(AppWindow.Size.Width, AppWindow.Size.Height);
                }
            };

            if (Content is FrameworkElement fe)
            {
                fe.DataContext = _viewModel;
            }

            _viewModel.Conversations.ConfirmAsync = ConfirmDestructiveAsync;
            _viewModel.ContactBook.ConfirmAsync = ConfirmDestructiveAsync;
            _viewModel.ConfirmActionAsync = ConfirmDestructiveAsync;

            // Arabic and other right-to-left languages need the whole layout mirrored, not just
            // translated text: panels, bubble alignment and scroll bars all flip with this.
            ApplyFlowDirection();
            _viewModel.Loc.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(ApplyFlowDirection);
            _viewModel.Conversations.ThreadChanged += ScrollThreadToBottom;

            _toasts = new ToastNotifier(_loggerFactory.CreateLogger<ToastNotifier>(), _viewModel.Loc);
            _toasts.ReplyRequested += OnToastReply;
            _smsManager.MessageReceived += OnSmsReceived;

            _viewModel.Ui.ApplyThemeRequested += theme =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (Content is FrameworkElement root) root.RequestedTheme = theme;
                });
            };
            if (Content is FrameworkElement rootElem)
            {
                rootElem.RequestedTheme = _viewModel.Ui.ElementTheme;
            }

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

    private void OnToastReply(string peer, string replyText)
    {
        _logger.LogInformation("Sending quick reply to {Peer} via toast notification.", peer);
        try
        {
            _smsManager?.SendSms(peer, replyText);
            DispatcherQueue.TryEnqueue(() => _ = _viewModel?.Conversations.RefreshAsync());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send quick reply from toast to {Peer}.", peer);
        }
    }

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

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    /// <summary>
    /// Enforces dark title bar on Windows 10 and 11, matching the window's dark theme
    /// instead of leaving the light default system frame.
    /// </summary>
    private void ConfigureTitleBarAndTheme()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            int darkMode = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref darkMode, sizeof(int));

            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                var titleBar = AppWindow.TitleBar;
                titleBar.ButtonBackgroundColor = Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                titleBar.ButtonForegroundColor = Colors.White;
                titleBar.ButtonHoverBackgroundColor = Color.FromArgb(40, 255, 255, 255);
                titleBar.ButtonHoverForegroundColor = Colors.White;
                titleBar.ButtonPressedBackgroundColor = Color.FromArgb(70, 255, 255, 255);
                titleBar.ButtonPressedForegroundColor = Colors.White;
                titleBar.ButtonInactiveForegroundColor = Color.FromArgb(160, 255, 255, 255);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not configure title bar theme.");
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
            int width = _viewModel?.Ui.SavedWindowWidth ?? 980;
            int height = _viewModel?.Ui.SavedWindowHeight ?? 640;
            if (width < 600 || height < 400)
            {
                width = 980;
                height = 640;
            }

            AppWindow.Resize(new SizeInt32(width, height));
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

    private void CopyMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SmsMessage msg })
        {
            try
            {
                var package = new DataPackage();
                package.SetText(msg.Body);
                Clipboard.SetContent(package);
                if (_viewModel is not null)
                {
                    _viewModel.Conversations.StatusText = _viewModel.Loc.MessageCopied;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to copy message text to clipboard.");
            }
        }
    }

    private void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            var handled = false;

            if (ThreadList.SelectionMode == ListViewSelectionMode.Multiple || (_viewModel?.Conversations.HasSelectedMessages ?? false))
            {
                ExitMessageSelection();
                handled = true;
            }

            if (ContactList.SelectionMode == ListViewSelectionMode.Multiple || (_viewModel?.ContactBook.ShowDeleteBar ?? false))
            {
                ExitContactSelection();
                handled = true;
            }

            if (ConversationList.SelectionMode == ListViewSelectionMode.Multiple || (_viewModel?.Conversations.ShowDeleteBar ?? false))
            {
                ExitConversationSelection();
                handled = true;
            }

            if (handled)
            {
                e.Handled = true;
            }
        }
    }

    private void ExitConversationSelection()
    {
        if (_viewModel is not null)
        {
            _viewModel.Conversations.IsSelectionMode = false;
            foreach (var c in _viewModel.Conversations.Conversations)
            {
                c.IsSelected = false;
            }
            var current = _viewModel.Conversations.SelectedConversation;
            _viewModel.Conversations.SetSelectedConversations(current is not null ? [current] : []);
        }
    }

    private void ExitContactSelection()
    {
        if (_viewModel is not null)
        {
            _viewModel.ContactBook.IsSelectionMode = false;
            foreach (var c in _viewModel.ContactBook.Contacts)
            {
                c.IsSelected = false;
            }
            var current = _viewModel.ContactBook.SelectedContact;
            _viewModel.ContactBook.SetSelectedContacts(current is not null ? [current] : []);
        }
    }

    private void ExitMessageSelection()
    {
        if (_viewModel is not null)
        {
            foreach (var m in _viewModel.Conversations.ThreadMessages)
            {
                m.IsSelected = false;
            }
            _viewModel.Conversations.SetSelectedMessages([]);
        }
    }

    private void CancelConversationSelection_Click(object sender, RoutedEventArgs e) => ExitConversationSelection();
    private void CancelContactSelection_Click(object sender, RoutedEventArgs e) => ExitContactSelection();
    private void CancelMessageSelection_Click(object sender, RoutedEventArgs e) => ExitMessageSelection();

    private void ConversationCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            var selected = _viewModel.Conversations.Conversations.Where(c => c.IsSelected).ToList();
            _viewModel.Conversations.SetSelectedConversations(selected);
        }
    }

    private void ContactCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            var selected = _viewModel.ContactBook.Contacts.Where(c => c.IsSelected).ToList();
            _viewModel.ContactBook.SetSelectedContacts(selected);
        }
    }

    private void MessageCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            var selected = _viewModel.Conversations.ThreadMessages.Where(m => m.IsSelected).ToList();
            _viewModel.Conversations.SetSelectedMessages(selected);
        }
    }

    private void SelectMessageContext_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SmsMessage msg } && _viewModel is not null)
        {
            msg.IsSelected = !msg.IsSelected;
            var selected = _viewModel.Conversations.ThreadMessages.Where(m => m.IsSelected).ToList();
            _viewModel.Conversations.SetSelectedMessages(selected);
        }
    }

    private async void EditPendingMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SmsMessage msg } && _viewModel is not null)
        {
            await _viewModel.Conversations.EditPendingMessageAsync(msg.Id, msg.Body);
            MessageBox?.Focus(FocusState.Programmatic);
        }
    }

    private async void CancelPendingMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SmsMessage msg } && _viewModel is not null)
        {
            await _viewModel.Conversations.CancelPendingMessageAsync(msg.Id);
        }
    }

    private async void RetryContextMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SmsMessage msg } && _viewModel is not null)
        {
            await _viewModel.Conversations.RetryAsync(msg.Id);
        }
    }

    private async void DeleteMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SmsMessage msg } && _viewModel is not null)
        {
            await _viewModel.Conversations.DeleteMessageAsync(msg.Id);
            ExitMessageSelection();
        }
    }

    private void MessageBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (!shift)
            {
                e.Handled = true;
                if (_viewModel?.Conversations.SendCommand.CanExecute(null) == true)
                {
                    _viewModel.Conversations.SendCommand.Execute(null);
                }
            }
        }
    }

    private void CopyPhoneNumber_Click(object sender, RoutedEventArgs e)
    {
        string? phone = null;
        if (sender is FrameworkElement { DataContext: Conversation conv })
        {
            phone = conv.PeerDisplay;
        }
        else if (sender is FrameworkElement { DataContext: Contact contact })
        {
            phone = contact.PhoneNumber;
        }

        if (!string.IsNullOrEmpty(phone))
        {
            var dp = new DataPackage();
            dp.SetText(phone);
            Clipboard.SetContent(dp);
            if (_viewModel is not null)
            {
                _viewModel.Conversations.StatusText = "Phone number copied to clipboard.";
            }
        }
    }

    private void SelectConversationContext_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Conversation conv } && _viewModel is not null)
        {
            _viewModel.Conversations.IsSelectionMode = true;
            conv.IsSelected = !conv.IsSelected;
            var selected = _viewModel.Conversations.Conversations.Where(c => c.IsSelected).ToList();
            _viewModel.Conversations.SetSelectedConversations(selected);
        }
    }

    private async void DeleteConversationContext_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Conversation conv } && _viewModel is not null)
        {
            await _viewModel.Conversations.DeleteSingleConversationAsync(conv);
            ExitConversationSelection();
        }
    }

    private void SelectContactContext_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Contact contact } && _viewModel is not null)
        {
            _viewModel.ContactBook.IsSelectionMode = true;
            contact.IsSelected = !contact.IsSelected;
            var selected = _viewModel.ContactBook.Contacts.Where(c => c.IsSelected).ToList();
            _viewModel.ContactBook.SetSelectedContacts(selected);
        }
    }

    private void ContactList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView list)
        {
            _viewModel?.ContactBook.SetSelectedContacts(list.SelectedItems.OfType<Contact>());
        }
    }

    private void DismissStatus_Click(object sender, RoutedEventArgs e)
        => _viewModel?.DismissAllStatus();

    private void DismissBalanceNotice_Click(object sender, RoutedEventArgs e)
        => _viewModel?.DismissBalanceNotice();

    private async void PresetUssd_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string code } && !string.IsNullOrWhiteSpace(code))
        {
            if (_viewModel is not null)
            {
                _viewModel.BalanceUssdCode = code;
            }
            await ShowInteractiveUssdSessionAsync(code);
        }
    }

    private async void SettingsCheckBalance_Click(object sender, RoutedEventArgs e)
    {
        var code = _viewModel?.BalanceUssdCode?.Trim();
        if (!string.IsNullOrEmpty(code))
        {
            await ShowInteractiveUssdSessionAsync(code);
        }
        else
        {
            CheckBalance_Click(sender, e);
        }
    }

    private void SaveConversationContact_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Conversation conv } && _viewModel is not null)
        {
            _viewModel.AddContactForPeer(conv.PeerDisplay);
        }
    }

    private void SendContactMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Contact c } && _viewModel is not null && !string.IsNullOrWhiteSpace(c.PhoneNumber))
        {
            _viewModel.StartConversationWith(c.PhoneNumber);
        }
    }

    private void RecipientSuggestBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput && _viewModel is not null)
        {
            var query = sender.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(query))
            {
                sender.ItemsSource = null;
            }
            else
            {
                var matches = _viewModel.ContactBook.Contacts
                    .Where(c => (!string.IsNullOrEmpty(c.DisplayName) && c.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                             || (!string.IsNullOrEmpty(c.PhoneNumber) && c.PhoneNumber.Contains(query, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                sender.ItemsSource = matches;
            }
        }
    }

    private void RecipientSuggestBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is Contact c && _viewModel is not null)
        {
            sender.Text = c.PhoneNumber;
            _viewModel.Conversations.NewRecipient = c.PhoneNumber;
        }
    }

    private void PickContactItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Contact c } && _viewModel is not null)
        {
            _viewModel.Conversations.NewRecipient = c.PhoneNumber;
            RecipientSuggestBox.Text = c.PhoneNumber;
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
    {
        _viewModel?.Conversations.SetSelectedMessages(ThreadList.SelectedItems.OfType<SmsMessage>());
        if (ThreadList.SelectionMode == ListViewSelectionMode.Multiple && ThreadList.SelectedItems.Count == 0)
        {
            ThreadList.SelectionMode = ListViewSelectionMode.None;
        }
    }

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
    /// <summary>
    /// Asks the carrier what the account balance is, letting the user correct the USSD code
    /// first because it differs per carrier and the app can only guess for a few countries.
    /// </summary>
    private async void CheckBalance_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            var defaultCode = _viewModel.BalanceUssdCode;
            var input = new TextBox
            {
                Text = defaultCode,
                PlaceholderText = string.IsNullOrEmpty(defaultCode) ? "*804# or *704#" : defaultCode,
                Header = _viewModel.Loc.BalanceCodeLabel
            };

            var contentStack = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = _viewModel.Loc.BalanceNoCode,
                        TextWrapping = TextWrapping.Wrap
                    },
                    input
                }
            };

            var presets = _viewModel.UssdPresets;
            if (presets.Count > 0)
            {
                contentStack.Children.Add(new TextBlock
                {
                    Text = "Carrier Shortcuts:",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Margin = new Thickness(0, 4, 0, 0)
                });

                var presetPanel = new StackPanel
                {
                    Spacing = 6
                };

                foreach (var (label, code) in presets)
                {
                    var btn = new Button
                    {
                        Content = label,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Left
                    };
                    btn.Click += (_, _) => input.Text = code;
                    presetPanel.Children.Add(btn);
                }

                contentStack.Children.Add(presetPanel);
            }

            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = _viewModel.Loc.CheckBalance,
                Content = contentStack,
                PrimaryButtonText = _viewModel.Loc.CheckBalance,
                CloseButtonText = _viewModel.Loc.Cancel,
                DefaultButton = ContentDialogButton.Primary
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            // Saved before the query so a correction survives even if the network is unreachable.
            _viewModel.BalanceUssdCode = input.Text;
            await ShowInteractiveUssdSessionAsync(input.Text);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not show the balance dialog.");
        }
    }

    private async Task ShowInteractiveUssdSessionAsync(string code)
    {
        if (_smsManager is null || Content?.XamlRoot is null)
        {
            return;
        }

        var session = _smsManager.CreateUssdSession();
        var messageBlock = new TextBlock
        {
            Text = $"Dialling {code}...",
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Margin = new Thickness(0, 0, 0, 10)
        };

        var progressRing = new ProgressRing
        {
            IsActive = true,
            Width = 24,
            Height = 24,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 8)
        };

        var replyBox = new TextBox
        {
            Header = "Reply with choice (e.g. 1, 2):",
            PlaceholderText = "Enter number or response...",
            CornerRadius = new CornerRadius(8),
            Visibility = Visibility.Collapsed
        };

        var stack = new StackPanel
        {
            Spacing = 8,
            MinWidth = 340,
            MaxWidth = 480,
            Children = { messageBlock, progressRing, replyBox }
        };

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = $"Carrier USSD ({code})",
            Content = stack,
            PrimaryButtonText = "Send Reply",
            IsPrimaryButtonEnabled = false,
            CloseButtonText = "Cancel"
        };

        async Task ExecuteTurnAsync(string input)
        {
            progressRing.Visibility = Visibility.Visible;
            progressRing.IsActive = true;
            replyBox.IsEnabled = false;
            dialog.IsPrimaryButtonEnabled = false;

            try
            {
                var turn = await session.SendAsync(input);
                progressRing.IsActive = false;
                progressRing.Visibility = Visibility.Collapsed;

                if (!turn.Success)
                {
                    messageBlock.Text = string.IsNullOrEmpty(turn.Error) ? turn.Text : $"USSD Error: {turn.Error}";
                    replyBox.Visibility = Visibility.Collapsed;
                    dialog.PrimaryButtonText = string.Empty;
                    dialog.IsPrimaryButtonEnabled = false;
                    dialog.CloseButtonText = "Close";
                    return;
                }

                messageBlock.Text = turn.Text;

                if (!turn.ExpectsReply)
                {
                    replyBox.Visibility = Visibility.Collapsed;
                    dialog.PrimaryButtonText = string.Empty;
                    dialog.IsPrimaryButtonEnabled = false;
                    dialog.CloseButtonText = "Done";
                }
                else
                {
                    replyBox.Visibility = Visibility.Visible;
                    replyBox.IsEnabled = true;
                    replyBox.Text = string.Empty;
                    dialog.PrimaryButtonText = "Send Reply";
                    dialog.IsPrimaryButtonEnabled = true;
                    replyBox.Focus(FocusState.Programmatic);
                }
            }
            catch (Exception ex)
            {
                progressRing.IsActive = false;
                progressRing.Visibility = Visibility.Collapsed;
                messageBlock.Text = $"USSD Error: {ex.Message}";
                replyBox.Visibility = Visibility.Collapsed;
                dialog.PrimaryButtonText = string.Empty;
                dialog.IsPrimaryButtonEnabled = false;
                dialog.CloseButtonText = "Close";
            }
        }

        dialog.PrimaryButtonClick += async (s, args) =>
        {
            args.Cancel = true;
            var reply = replyBox.Text?.Trim();
            if (!string.IsNullOrEmpty(reply))
            {
                await ExecuteTurnAsync(reply);
            }
        };

        replyBox.KeyDown += async (s, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter && dialog.IsPrimaryButtonEnabled)
            {
                e.Handled = true;
                var reply = replyBox.Text?.Trim();
                if (!string.IsNullOrEmpty(reply))
                {
                    await ExecuteTurnAsync(reply);
                }
            }
        };

        void OnIncomingSms(object? sender, SmsMessage sms)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                progressRing.IsActive = false;
                progressRing.Visibility = Visibility.Collapsed;
                var senderName = string.IsNullOrWhiteSpace(sms.From) ? "Carrier" : sms.From;
                messageBlock.Text = $"{messageBlock.Text}\n\n📨 Carrier response from {senderName}:\n{sms.Body}";
                if (dialog.CloseButtonText == "Cancel")
                {
                    dialog.CloseButtonText = "Done";
                }
            });
        }

        _smsManager.MessageReceived += OnIncomingSms;
        try
        {
            var showTask = dialog.ShowAsync();
            await ExecuteTurnAsync(code);
            await showTask;
        }
        finally
        {
            _smsManager.MessageReceived -= OnIncomingSms;
            session.Dispose();
        }
    }

    private async Task<bool> ConfirmDestructiveAsync(string title, string message)
    {
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                // From the language pack, not hard-coded: these two words were Vietnamese in
                // every one of the nineteen interface languages.
                PrimaryButtonText = _viewModel?.Loc.Delete ?? "Delete",
                CloseButtonText = _viewModel?.Loc.Cancel ?? "Cancel",
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
