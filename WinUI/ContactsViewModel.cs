using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using SuperDoc.Sms.Models;
using SuperDoc.Sms.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace SuperDoc.Sms.WinUI;

/// <summary>The address book: list on the left, an editor for the selected entry on the right.</summary>
public sealed class ContactsViewModel : INotifyPropertyChanged
{
    private readonly SmsManager _smsManager;
    private readonly ILogger _logger;
    private readonly DispatcherQueue _dispatcher;
    private readonly Strings _loc;

    private Contact? _selectedContact;
    private Contact _editor = new();
    private string _statusText = string.Empty;
    private bool _isEditing;

    private readonly HashSet<long> _selectedContactIds = [];

    public ContactsViewModel(SmsManager smsManager, ILogger logger, DispatcherQueue dispatcher, Strings loc)
    {
        _smsManager = smsManager;
        _logger = logger;
        _dispatcher = dispatcher;
        _loc = loc;

        NewContactCommand = new DelegateCommand(StartNew);
        SaveCommand = new AsyncCommand(SaveAsync, () => IsEditing);
        DeleteCommand = new AsyncCommand(DeleteAsync, () => IsEditing && Editor.Id != 0);
        DeleteSelectedContactsCommand = new AsyncCommand(DeleteSelectedContactsAsync, () => SelectedContactCount > 0);
        CancelCommand = new DelegateCommand(CancelEdit, () => IsEditing);
        SendMessageToContactCommand = new DelegateCommand(() =>
        {
            if (!string.IsNullOrWhiteSpace(Editor.PhoneNumber))
            {
                SendMessageRequested?.Invoke(Editor.PhoneNumber);
            }
        }, () => IsEditing && !string.IsNullOrWhiteSpace(Editor.PhoneNumber));
    }

    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    public int SelectedContactCount => _selectedContactIds.Count;

    public bool HasSelectedContacts => SelectedContactCount > 0;

    public string DeleteContactsLabel => SelectedContactCount == 1 ? "Delete 1 contact" : $"Delete {SelectedContactCount} contacts";

    public AsyncCommand DeleteSelectedContactsCommand { get; }

    public void SetSelectedContacts(IEnumerable<Contact> contacts)
    {
        _selectedContactIds.Clear();
        foreach (var c in contacts)
        {
            if (c.Id != 0)
            {
                _selectedContactIds.Add(c.Id);
            }
        }
        OnPropertyChanged(nameof(SelectedContactCount));
        OnPropertyChanged(nameof(HasSelectedContacts));
        OnPropertyChanged(nameof(DeleteContactsLabel));
        DeleteSelectedContactsCommand.RaiseCanExecuteChanged();
    }

    public async Task DeleteSelectedContactsAsync()
    {
        var count = _selectedContactIds.Count;
        if (count == 0) return;

        if (ConfirmAsync is not null && !await ConfirmAsync(_loc.Delete, $"Are you sure you want to delete {count} contact(s)?"))
        {
            return;
        }

        try
        {
            var ids = _selectedContactIds.ToList();
            var deleted = await Task.Run(() =>
            {
                var total = 0;
                foreach (var id in ids)
                {
                    if (_smsManager.DeleteContact(id))
                    {
                        total++;
                    }
                }
                return total;
            });

            _selectedContactIds.Clear();
            OnPropertyChanged(nameof(SelectedContactCount));
            OnPropertyChanged(nameof(HasSelectedContacts));
            OnPropertyChanged(nameof(DeleteContactsLabel));
            DeleteSelectedContactsCommand.RaiseCanExecuteChanged();

            StatusText = $"Deleted {deleted} contact(s).";
            if (SelectedContact is not null && ids.Contains(SelectedContact.Id))
            {
                CancelEdit();
            }
            await RefreshAsync();
            ContactsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete selected contacts.");
            StatusText = _loc.DeleteFailed(ex.Message);
        }
    }

    public event Action<string>? SendMessageRequested;

    public DelegateCommand SendMessageToContactCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after a change that affects how conversations are titled.</summary>
    public event Action? ContactsChanged;

    public ObservableCollection<Contact> Contacts { get; } = new();

    public DelegateCommand NewContactCommand { get; }

    public AsyncCommand SaveCommand { get; }

    public AsyncCommand DeleteCommand { get; }

    public DelegateCommand CancelCommand { get; }

    public Contact? SelectedContact
    {
        get => _selectedContact;
        set
        {
            if (SetField(ref _selectedContact, value) && value is not null)
            {
                // Edit a copy, so abandoning the form does not leave the list showing half-typed
                // values that were never saved.
                Editor = value.Clone();
                IsEditing = true;
            }
        }
    }

    /// <summary>The working copy bound to the form.</summary>
    public Contact Editor
    {
        get => _editor;
        private set => SetField(ref _editor, value);
    }

    public bool IsEditing
    {
        get => _isEditing;
        private set
        {
            if (SetField(ref _isEditing, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
                DeleteCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private Microsoft.UI.Xaml.DispatcherTimer? _statusTimer;

    public string StatusText
    {
        get => _statusText;
        set
        {
            if (SetField(ref _statusText, value))
            {
                OnPropertyChanged(nameof(HasStatusText));
                RunOnUi(() =>
                {
                    _statusTimer?.Stop();
                    if (!string.IsNullOrEmpty(value))
                    {
                        _statusTimer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                        _statusTimer.Tick += (_, _) =>
                        {
                            _statusTimer?.Stop();
                            StatusText = string.Empty;
                        };
                        _statusTimer.Start();
                    }
                });
            }
        }
    }

    public bool HasStatusText => !string.IsNullOrEmpty(_statusText);

    public void DismissStatus() => StatusText = string.Empty;

    public async Task RefreshAsync()
    {
        try
        {
            var contacts = await Task.Run(() => _smsManager.GetContacts());
            RunOnUi(() =>
            {
                var selectedId = SelectedContact?.Id;

                Contacts.Clear();
                foreach (var contact in contacts)
                {
                    Contacts.Add(contact);
                }

                if (selectedId is not null)
                {
                    _selectedContact = Contacts.FirstOrDefault(c => c.Id == selectedId);
                    OnPropertyChanged(nameof(SelectedContact));
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load contacts.");
            RunOnUi(() => StatusText = _loc.LoadContactsFailed(ex.Message));
        }
    }

    /// <summary>Opens the form for a brand-new entry, optionally pre-filled with a number.</summary>
    public void StartNewFor(string phoneNumber)
    {
        SelectedContact = null;
        _selectedContact = null;
        OnPropertyChanged(nameof(SelectedContact));

        Editor = new Contact { PhoneNumber = phoneNumber };
        IsEditing = true;
        StatusText = string.Empty;
    }

    private void StartNew() => StartNewFor(string.Empty);

    private void CancelEdit()
    {
        IsEditing = false;
        Editor = new Contact();
        _selectedContact = null;
        OnPropertyChanged(nameof(SelectedContact));
        StatusText = string.Empty;
    }

    private async Task SaveAsync()
    {
        var contact = Editor;

        if (string.IsNullOrWhiteSpace(contact.PhoneNumber))
        {
            StatusText = _loc.ContactNeedsPhone;
            return;
        }

        if (string.IsNullOrEmpty(contact.PhoneKey))
        {
            StatusText = _loc.InvalidPhone(contact.PhoneNumber);
            return;
        }

        try
        {
            await Task.Run(() => _smsManager.SaveContact(contact));
            StatusText = _loc.ContactSaved(contact.DisplayName);
            IsEditing = false;
            await RefreshAsync();
            ContactsChanged?.Invoke();
        }
        catch (InvalidOperationException ex)
        {
            // Duplicate number: a real, expected outcome rather than a crash.
            StatusText = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save contact.");
            StatusText = _loc.SaveFailed(ex.Message);
        }
    }

    private async Task DeleteAsync()
    {
        var id = Editor.Id;
        if (id == 0)
        {
            return;
        }

        try
        {
            await Task.Run(() => _smsManager.DeleteContact(id));
            StatusText = _loc.ContactDeleted;
            CancelEdit();
            await RefreshAsync();
            ContactsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete contact #{Id}.", id);
            StatusText = _loc.DeleteFailed(ex.Message);
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
}
