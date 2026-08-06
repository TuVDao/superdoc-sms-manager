using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using SuperDoc.Sms.Models;
using SuperDoc.Sms.Services;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SuperDoc.Sms.WinUI;

/// <summary>
/// The window shell: modem status, which screen is showing, and the two screens themselves.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly SmsManager _smsManager;
    private readonly ILogger<MainViewModel> _logger;
    private readonly DispatcherQueue _dispatcher;
    private string _deviceStatusText = string.Empty;
    private string _balanceNotice = string.Empty;
    private bool _isContactsView;
    private bool _disposed;
    private DateTimeOffset _lastBalanceEnquiry = DateTimeOffset.MinValue;

    /// <summary>How rarely the account may be queried while sends keep failing.</summary>
    private static readonly TimeSpan BalanceEnquiryInterval = TimeSpan.FromMinutes(10);

    public MainViewModel(SmsManager smsManager, ILogger<MainViewModel> logger, DispatcherQueue dispatcher)
    {
        _smsManager = smsManager;
        _logger = logger;
        _dispatcher = dispatcher;

        Loc = new Strings(logger);
        Ui = new UiSettings(smsManager, logger, Loc);

        Conversations = new ConversationsViewModel(smsManager, logger, dispatcher, Loc);
        ContactBook = new ContactsViewModel(smsManager, logger, dispatcher, Loc);

        // Switching language rewrites text the view models built earlier, such as the modem
        // status line, so they have to be recomputed rather than left in the old language.
        Loc.PropertyChanged += (_, _) =>
        {
            _ = LoadDeviceStatusAsync();
            Conversations.RefreshLocalisedText();
        };

        ShowMessagesCommand = new DelegateCommand(() => IsContactsView = false);
        ShowContactsCommand = new DelegateCommand(() => IsContactsView = true);
        ToggleLeftPanelCommand = new DelegateCommand(Ui.ToggleLeftPanel);
        AddContactForPeerCommand = new DelegateCommand(AddContactForCurrentPeer, () =>
            Conversations.SelectedConversation is { HasContact: false });

        // Renaming or adding a contact changes how every thread is titled.
        ContactBook.ContactsChanged += () => _ = Conversations.RefreshAsync();

        Conversations.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConversationsViewModel.SelectedConversation))
            {
                AddContactForPeerCommand.RaiseCanExecuteChanged();
            }
        };

        CheckBalanceCommand = new AsyncCommand(() => RefreshBalanceAsync(userAsked: true));

        // A refusal the modem cannot explain is nearly always an empty account. Saying so, with
        // the carrier's own words underneath, is the difference between a five-minute fix and an
        // afternoon spent suspecting the app.
        _smsManager.SendRefusedWithoutReason += OnSendRefusedWithoutReason;

        _smsManager.MessageReceived += OnSmsReceived;

        // The health monitor may rebuild the receive path minutes after startup; without this
        // the status line would still show whatever was true when the window opened.
        _smsManager.ReceiveStateChanged += OnReceiveStateChanged;

        _ = RefreshAsync();
        _ = ContactBook.RefreshAsync();
        _ = LoadDeviceStatusAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Every user-visible string, in the active language.</summary>
    public Strings Loc { get; }

    /// <summary>The emoji offered by the composer's picker.</summary>
    public IReadOnlyList<EmojiGroup> EmojiGroups => EmojiCatalog.Groups;

    /// <summary>Text size, language and panel geometry, persisted between runs.</summary>
    public UiSettings Ui { get; }

    public ConversationsViewModel Conversations { get; }

    public ContactsViewModel ContactBook { get; }

    public DelegateCommand ToggleLeftPanelCommand { get; }

    public DelegateCommand ShowMessagesCommand { get; }

    public DelegateCommand ShowContactsCommand { get; }

    public DelegateCommand AddContactForPeerCommand { get; }

    public string DeviceStatusText
    {
        get => _deviceStatusText;
        set => SetField(ref _deviceStatusText, value);
    }

    public AsyncCommand CheckBalanceCommand { get; }

    /// <summary>
    /// Why sending is failing, and what the carrier says about the account. Empty most of the
    /// time; shown prominently when it is not.
    /// </summary>
    public string BalanceNotice
    {
        get => _balanceNotice;
        set
        {
            if (SetField(ref _balanceNotice, value))
            {
                OnPropertyChanged(nameof(HasBalanceNotice));
            }
        }
    }

    public bool HasBalanceNotice => BalanceNotice.Length > 0;

    /// <summary>The USSD code used for the balance enquiry; carrier-specific and user-editable.</summary>
    public string BalanceUssdCode
    {
        get => _smsManager.BalanceUssdCode;
        set
        {
            _smsManager.BalanceUssdCode = value;
            OnPropertyChanged();
        }
    }

    private void OnSendRefusedWithoutReason(object? sender, EventArgs e)
    {
        // The queue retries, so one blocked message produces a burst of these. Asking the network
        // once is informative; asking it every few seconds is abuse of a shared channel.
        if (DateTimeOffset.UtcNow - _lastBalanceEnquiry < BalanceEnquiryInterval)
        {
            return;
        }

        _lastBalanceEnquiry = DateTimeOffset.UtcNow;
        _ = RefreshBalanceAsync(userAsked: false);
    }

    /// <summary>
    /// Asks the carrier about the account and shows the answer.
    /// </summary>
    /// <param name="userAsked">
    /// True when the user pressed the button, which is the only case where "there is no code for
    /// this carrier" is worth saying out loud.
    /// </param>
    private async Task RefreshBalanceAsync(bool userAsked)
    {
        var hint = userAsked ? string.Empty : Loc.SendRefusedHint + " ";

        if (_smsManager.BalanceUssdCode.Length == 0)
        {
            RunOnUi(() => BalanceNotice = userAsked ? Loc.BalanceNoCode : hint + Loc.BalanceNoCode);
            return;
        }

        RunOnUi(() => BalanceNotice = hint + Loc.BalanceChecking);

        var result = await _smsManager.CheckBalanceAsync();

        RunOnUi(() => BalanceNotice = result.Succeeded
            ? hint + result.Text
            : hint + Loc.BalanceFailed(result.Error ?? string.Empty));
    }

    public bool IsContactsView
    {
        get => _isContactsView;
        set
        {
            if (SetField(ref _isContactsView, value))
            {
                OnPropertyChanged(nameof(IsMessagesView));
                if (value)
                {
                    _ = ContactBook.RefreshAsync();
                }
            }
        }
    }

    public bool IsMessagesView => !IsContactsView;

    /// <summary>Jumps to the address book with the open thread's number already filled in.</summary>
    private void AddContactForCurrentPeer()
    {
        var conversation = Conversations.SelectedConversation;
        if (conversation is null)
        {
            return;
        }

        ContactBook.StartNewFor(conversation.PeerDisplay);
        IsContactsView = true;
    }

    /// <summary>Runs on a modem callback thread, so the refresh marshals itself to the UI thread.</summary>
    private void OnSmsReceived(object? sender, SmsMessage message) => _ = RefreshAsync();

    private void OnReceiveStateChanged(object? sender, EventArgs e) => _ = LoadDeviceStatusAsync();

    public Task RefreshAsync() => Conversations.RefreshAsync();

    private async Task LoadDeviceStatusAsync()
    {
        try
        {
            var snapshot = await _smsManager.GetDeviceSnapshotAsync();

            if (DemoMode.IsEnabled)
            {
                // "Modem unavailable" is accurate but reads as a fault; in demo mode there is no
                // fault, the modem was never opened on purpose.
                RunOnUi(() => DeviceStatusText = snapshot.Diagnostic);
                return;
            }

            var text = snapshot.IsAvailable
                ? Loc.ModemReady(
                      snapshot.DeviceStatus, snapshot.AccountPhoneNumber, snapshot.CellularClass)
                  + (snapshot.CanReceive
                      ? Loc.SendAndReceive(snapshot.ReceiveMode)
                      : Loc.SendOnly(snapshot.Diagnostic))
                : Loc.ModemUnavailable(snapshot.Diagnostic);

            RunOnUi(() => DeviceStatusText = text);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read modem status.");
            RunOnUi(() => DeviceStatusText = Loc.ModemStatusFailed(ex.Message));
        }
    }

    private void RunOnUi(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        _dispatcher.TryEnqueue(() => action());
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _smsManager.MessageReceived -= OnSmsReceived;
        _smsManager.ReceiveStateChanged -= OnReceiveStateChanged;
        _smsManager.SendRefusedWithoutReason -= OnSendRefusedWithoutReason;
    }
}
