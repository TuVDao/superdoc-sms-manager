using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SuperDoc.Sms.Models;
using SuperDoc.Sms.Services;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;

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
    private string _deviceStatusSummary = "Modem Off";
    private bool _isModemReady;
    private string _balanceNotice = string.Empty;
    private string _activeView = "Messages";
    private bool _disposed;
    private DateTimeOffset _lastBalanceEnquiry = DateTimeOffset.MinValue;

    /// <summary>How rarely the account may be queried while sends keep failing.</summary>
    private static readonly TimeSpan BalanceEnquiryInterval = TimeSpan.FromMinutes(10);

    public Func<string, string, Task<bool>>? ConfirmActionAsync { get; set; }

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

        var initialView = Environment.GetEnvironmentVariable("SUPERDOC_SMS_VIEW");
        if (!string.IsNullOrEmpty(initialView))
        {
            _activeView = initialView;
        }

        ShowMessagesCommand = new DelegateCommand(() => SelectView("Messages"));
        ShowContactsCommand = new DelegateCommand(() => SelectView("Contacts"));
        ShowSettingsCommand = new DelegateCommand(() => SelectView("Settings"));
        ToggleNavCommand = new DelegateCommand(Ui.ToggleNavExpansion);
        DismissBalanceNoticeCommand = new DelegateCommand(() => BalanceNotice = string.Empty);
        SetBalanceUssdCodeCommand = new DelegateCommand<string>(code =>
        {
            if (!string.IsNullOrWhiteSpace(code))
            {
                BalanceUssdCode = code;
            }
        });
        RecheckModemCommand = new AsyncCommand(RecheckModemAsync);
        RestartModemHardwareCommand = new AsyncCommand(RestartModemHardwareAsync);
        ToggleLeftPanelCommand = new DelegateCommand(Ui.ToggleLeftPanel);
        AddContactForPeerCommand = new DelegateCommand(AddContactForCurrentPeer, () =>
            Conversations.SelectedConversation is { HasContact: false });

        // Renaming or adding a contact changes how every thread is titled.
        ContactBook.ContactsChanged += () => _ = Conversations.RefreshAsync();

        Conversations.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConversationsViewModel.StatusText))
            {
                OnPropertyChanged(nameof(CurrentStatusText));
            }
        };
        ContactBook.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ContactsViewModel.StatusText))
            {
                OnPropertyChanged(nameof(CurrentStatusText));
            }
        };
        ContactBook.SendMessageRequested += StartConversationWith;

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

    public DelegateCommand ShowSettingsCommand { get; }
    public DelegateCommand ToggleNavCommand { get; }
    public DelegateCommand DismissBalanceNoticeCommand { get; }
    public DelegateCommand<string> SetBalanceUssdCodeCommand { get; }

    public AsyncCommand RecheckModemCommand { get; }
    public AsyncCommand RestartModemHardwareCommand { get; }

    public DelegateCommand AddContactForPeerCommand { get; }

    public string CurrentStatusText => !string.IsNullOrEmpty(Conversations.StatusText) ? Conversations.StatusText : ContactBook.StatusText;

    public void DismissAllStatus()
    {
        Conversations.DismissStatus();
        ContactBook.DismissStatus();
        OnPropertyChanged(nameof(CurrentStatusText));
    }

    public void DismissBalanceNotice()
    {
        _balanceNoticeTimer?.Stop();
        BalanceNotice = string.Empty;
    }

    public string DeviceStatusText
    {
        get => _deviceStatusText;
        set => SetField(ref _deviceStatusText, value);
    }

    public string DeviceStatusSummary
    {
        get => _deviceStatusSummary;
        set => SetField(ref _deviceStatusSummary, value);
    }

    public bool IsModemReady
    {
        get => _isModemReady;
        set => SetField(ref _isModemReady, value);
    }

    public AsyncCommand CheckBalanceCommand { get; }

    public void StartConversationWith(string phone)
    {
        SelectView("Messages");
        Conversations.StartNewConversation();
        Conversations.NewRecipient = phone;
    }

    private DispatcherTimer? _balanceNoticeTimer;

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
                RunOnUi(() =>
                {
                    _balanceNoticeTimer?.Stop();
                    if (!string.IsNullOrEmpty(value))
                    {
                        _balanceNoticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
                        _balanceNoticeTimer.Tick += (_, _) =>
                        {
                            _balanceNoticeTimer?.Stop();
                            BalanceNotice = string.Empty;
                        };
                        _balanceNoticeTimer.Start();
                    }
                });
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

    /// <summary>Quick USSD presets for the current country / carrier (e.g. Ethio Telecom, Safaricom).</summary>
    public IReadOnlyList<UssdPreset> UssdPresets => _smsManager.GetUssdPresets();

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

    public bool IsMessagesView => _activeView == "Messages";

    public bool IsContactsView
    {
        get => _activeView == "Contacts";
        set => SelectView(value ? "Contacts" : "Messages");
    }

    public bool IsSettingsView => _activeView == "Settings";

    public void SelectView(string view)
    {
        if (SetField(ref _activeView, view, nameof(IsMessagesView)))
        {
            OnPropertyChanged(nameof(IsContactsView));
            OnPropertyChanged(nameof(IsSettingsView));
            if (view == "Contacts")
            {
                _ = ContactBook.RefreshAsync();
            }
            else if (view == "Settings")
            {
                _ = LoadDeviceStatusAsync();
            }
        }
    }

    private async Task RecheckModemAsync()
    {
        RunOnUi(() =>
        {
            DeviceStatusSummary = "Checking...";
            DeviceStatusText = "Resetting and re-querying modem...";
        });
        await _smsManager.ResetAndReinitializeAsync();
        await LoadDeviceStatusAsync();
    }

    private async Task RestartModemHardwareAsync()
    {
        if (ConfirmActionAsync is not null)
        {
            var proceed = await ConfirmActionAsync(
                Loc.ModemResetConfirmTitle,
                Loc.ModemResetConfirmMessage);
            if (!proceed)
            {
                return;
            }
        }

        RunOnUi(() =>
        {
            DeviceStatusSummary = "Restarting...";
            DeviceStatusText = "Restarting cellular modem hardware and services...";
        });

        try
        {
            var baseDir = AppContext.BaseDirectory;
            var scriptPath = Path.Combine(baseDir, "scripts", "Fix-SIM-Detection.bat");
            if (!File.Exists(scriptPath))
            {
                scriptPath = Path.Combine(Directory.GetCurrentDirectory(), "scripts", "Fix-SIM-Detection.bat");
            }

            if (File.Exists(scriptPath))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = scriptPath,
                    UseShellExecute = true
                };
                var proc = Process.Start(psi);
                if (proc is not null)
                {
                    await proc.WaitForExitAsync();
                }
            }
            else
            {
                var ps1Path = Path.Combine(baseDir, "scripts", "restart-modem.ps1");
                if (!File.Exists(ps1Path))
                {
                    ps1Path = Path.Combine(Directory.GetCurrentDirectory(), "scripts", "restart-modem.ps1");
                }

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{ps1Path}\"",
                    Verb = "runas",
                    UseShellExecute = true
                };
                var proc = Process.Start(psi);
                if (proc is not null)
                {
                    await proc.WaitForExitAsync();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hardware modem restart was cancelled or failed.");
        }

        await RecheckModemAsync();
    }

    /// <summary>Jumps to the address book with the given phone number filled in.</summary>
    public void AddContactForPeer(string phone)
    {
        ContactBook.StartNewFor(phone);
        IsContactsView = true;
    }

    /// <summary>Jumps to the address book with the open thread's number already filled in.</summary>
    private void AddContactForCurrentPeer()
    {
        var conversation = Conversations.SelectedConversation;
        if (conversation is null)
        {
            return;
        }

        AddContactForPeer(conversation.PeerDisplay);
    }

    /// <summary>Runs on a modem callback thread, so the refresh marshals itself to the UI thread.</summary>
    private void OnSmsReceived(object? sender, SmsMessage message)
    {
        _ = RefreshAsync();
        if (!string.IsNullOrEmpty(BalanceNotice) && BalanceNotice.Contains("USSD request sent"))
        {
            var senderName = string.IsNullOrWhiteSpace(message.From) ? "Carrier" : message.From;
            RunOnUi(() => BalanceNotice = $"{senderName}: {message.Body}");
        }
    }

    private void OnReceiveStateChanged(object? sender, EventArgs e) => _ = LoadDeviceStatusAsync();

    public Task RefreshAsync() => Conversations.RefreshAsync();

    private async Task LoadDeviceStatusAsync()
    {
        try
        {
            var snapshot = await _smsManager.GetDeviceSnapshotAsync();

            if (DemoMode.IsEnabled)
            {
                RunOnUi(() =>
                {
                    DeviceStatusSummary = "Demo Mode";
                    IsModemReady = false;
                    DeviceStatusText = snapshot.Diagnostic;
                });
                return;
            }

            var isReady = snapshot.IsAvailable && snapshot.DeviceStatus == "Ready";
            string summary;

            if (!snapshot.IsAvailable || snapshot.DeviceStatus is "Off" or "Unavailable" or "DeviceFailure")
            {
                summary = "Modem Off";
            }
            else if (!string.IsNullOrEmpty(snapshot.CarrierName))
            {
                summary = snapshot.CarrierName;
            }
            else if (!string.IsNullOrEmpty(snapshot.AccountPhoneNumber) && snapshot.AccountPhoneNumber != "(None)")
            {
                summary = snapshot.AccountPhoneNumber;
            }
            else
            {
                summary = "Modem Ready";
            }

            var text = snapshot.IsAvailable
                ? Loc.ModemReady(
                      snapshot.DeviceStatus, snapshot.AccountPhoneNumber, snapshot.CellularClass)
                  + (snapshot.CanReceive
                      ? Loc.SendAndReceive(snapshot.ReceiveMode)
                      : Loc.SendOnly(snapshot.Diagnostic))
                : Loc.ModemUnavailable(snapshot.Diagnostic);

            if (!string.IsNullOrEmpty(snapshot.CarrierName))
            {
                text = $"{snapshot.CarrierName} • {text}";
            }

            RunOnUi(() =>
            {
                IsModemReady = isReady;
                DeviceStatusSummary = summary;
                DeviceStatusText = text;
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read modem status.");
            RunOnUi(() =>
            {
                IsModemReady = false;
                DeviceStatusSummary = "Modem Error";
                DeviceStatusText = Loc.ModemStatusFailed(ex.Message);
            });
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
