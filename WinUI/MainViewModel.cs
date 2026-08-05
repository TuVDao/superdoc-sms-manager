using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using MyApp.Models;
using MyApp.Services;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Message_T480s.WinUI;

/// <summary>
/// The window shell: modem status, which screen is showing, and the two screens themselves.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly SmsManager _smsManager;
    private readonly ILogger<MainViewModel> _logger;
    private readonly DispatcherQueue _dispatcher;
    private string _deviceStatusText = string.Empty;
    private bool _isContactsView;
    private bool _disposed;

    public MainViewModel(SmsManager smsManager, ILogger<MainViewModel> logger, DispatcherQueue dispatcher)
    {
        _smsManager = smsManager;
        _logger = logger;
        _dispatcher = dispatcher;

        Loc = new Strings();
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
    }
}
